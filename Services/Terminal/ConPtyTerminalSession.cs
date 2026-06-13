using Microsoft.Win32.SafeHandles;
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GitDeployPro.Services.Terminal
{
    public sealed class ConPtyTerminalSession : ITerminalSession
    {
        private const int ProcThreadAttributePseudoConsole = 0x00020016;
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const uint HandleFlagInherit = 0x00000001;
        private const uint WaitObject0 = 0x00000000;

        private readonly string _shell;
        private readonly string _workingDirectory;
        private int _columns;
        private int _rows;

        private IntPtr _pseudoConsole = IntPtr.Zero;
        private IntPtr _ptyInputWriter = IntPtr.Zero;
        private IntPtr _ptyOutputReader = IntPtr.Zero;
        private IntPtr _attributeList = IntPtr.Zero;
        private NativeProcessInformation _processInformation;

        private StreamWriter? _inputWriter;
        private StreamReader? _outputReader;
        private CancellationTokenSource? _readLoopCts;
        private Task? _readLoopTask;
        private bool _disposed;
        private bool _started;

        public event Action<string>? OutputReceived;
        public event Action? SessionClosed;

        public bool IsConnected => _started && _processInformation.hProcess != IntPtr.Zero && !IsProcessExited();

        public ConPtyTerminalSession(string shell, string workingDirectory, int columns, int rows)
        {
            _shell = string.IsNullOrWhiteSpace(shell) ? "cmd.exe" : shell;
            _workingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? "C:\\" : workingDirectory;
            _columns = Math.Max(20, columns);
            _rows = Math.Max(5, rows);
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            CreatePseudoConsoleSession();
            _readLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _readLoopTask = Task.Run(() => ReadLoopAsync(_readLoopCts.Token), _readLoopCts.Token);
            _started = true;

            return Task.CompletedTask;
        }

        public async Task WriteAsync(string data, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (!IsConnected || string.IsNullOrEmpty(data) || _inputWriter == null)
            {
                return;
            }

            await _inputWriter.WriteAsync(data);
            await _inputWriter.FlushAsync();
        }

        public Task SendInterruptAsync(CancellationToken cancellationToken = default)
        {
            return WriteAsync("\u0003", cancellationToken);
        }

        public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (_pseudoConsole == IntPtr.Zero)
            {
                return Task.CompletedTask;
            }

            _columns = Math.Max(20, columns);
            _rows = Math.Max(5, rows);
            var result = ResizePseudoConsole(_pseudoConsole, new Coord((short)_columns, (short)_rows));
            if (result != 0)
            {
                throw new Win32Exception(result, "Unable to resize ConPTY session.");
            }

            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                return;
            }

            _readLoopCts?.Cancel();

            if (_readLoopTask != null)
            {
                try
                {
                    await _readLoopTask.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
                }
                catch
                {
                    // Ignore expected cancellation/timeout.
                }
            }

            try
            {
                if (_inputWriter != null)
                {
                    await _inputWriter.FlushAsync();
                }
            }
            catch
            {
                // Ignore shutdown flush failures.
            }

            CleanupNativeResources();
            SessionClosed?.Invoke();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            await StopAsync();
            _disposed = true;
        }

        private void CreatePseudoConsoleSession()
        {
            var securityAttributes = new SecurityAttributes
            {
                nLength = Marshal.SizeOf<SecurityAttributes>(),
                bInheritHandle = true
            };

            if (!CreatePipe(out var ptyInputReader, out _ptyInputWriter, ref securityAttributes, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create ConPTY input pipe.");
            }

            if (!CreatePipe(out _ptyOutputReader, out var ptyOutputWriter, ref securityAttributes, 0))
            {
                CloseHandle(ptyInputReader);
                CloseHandle(_ptyInputWriter);
                _ptyInputWriter = IntPtr.Zero;
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create ConPTY output pipe.");
            }

            try
            {
                SetHandleInformation(_ptyInputWriter, HandleFlagInherit, 0);
                SetHandleInformation(_ptyOutputReader, HandleFlagInherit, 0);

                var hr = CreatePseudoConsole(
                    new Coord((short)_columns, (short)_rows),
                    ptyInputReader,
                    ptyOutputWriter,
                    0,
                    out _pseudoConsole);

                if (hr != 0)
                {
                    throw new Win32Exception(hr, "Unable to create ConPTY.");
                }
            }
            finally
            {
                CloseHandle(ptyInputReader);
                CloseHandle(ptyOutputWriter);
            }

            BuildStartupAttributeList();

            var startupInfo = new StartupInfoEx();
            startupInfo.StartupInfo.cb = (uint)Marshal.SizeOf<StartupInfoEx>();
            startupInfo.lpAttributeList = _attributeList;

            var commandLine = _shell;
            var creationFlags = ExtendedStartupInfoPresent | CreateUnicodeEnvironment;

            var created = CreateProcessW(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                creationFlags,
                IntPtr.Zero,
                _workingDirectory,
                ref startupInfo,
                out _processInformation);

            if (!created)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Unable to start '{_shell}' with ConPTY.");
            }

            if (_processInformation.hThread != IntPtr.Zero)
            {
                CloseHandle(_processInformation.hThread);
                _processInformation.hThread = IntPtr.Zero;
            }

            var outputHandle = new SafeFileHandle(_ptyOutputReader, ownsHandle: false);
            _outputReader = new StreamReader(
                new FileStream(outputHandle, FileAccess.Read, bufferSize: 4096, isAsync: true),
                new UTF8Encoding(false, false),
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: true);

            var inputHandle = new SafeFileHandle(_ptyInputWriter, ownsHandle: false);
            _inputWriter = new StreamWriter(
                new FileStream(inputHandle, FileAccess.Write, bufferSize: 4096, isAsync: true),
                new UTF8Encoding(false))
            {
                AutoFlush = true
            };
        }

        private void BuildStartupAttributeList()
        {
            IntPtr attributeListSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);

            _attributeList = Marshal.AllocHGlobal(attributeListSize);
            if (!InitializeProcThreadAttributeList(_attributeList, 1, 0, ref attributeListSize))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to initialize ConPTY attribute list.");
            }

            if (!UpdateProcThreadAttribute(
                    _attributeList,
                    0,
                    (IntPtr)ProcThreadAttributePseudoConsole,
                    _pseudoConsole,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to configure pseudo console attribute.");
            }
        }

        private async Task ReadLoopAsync(CancellationToken cancellationToken)
        {
            if (_outputReader == null)
            {
                return;
            }

            var buffer = new char[4096];
            try
            {
                while (!cancellationToken.IsCancellationRequested && IsConnected)
                {
                    var read = await _outputReader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                    if (read > 0)
                    {
                        OutputReceived?.Invoke(new string(buffer, 0, read));
                    }
                    else if (IsProcessExited())
                    {
                        break;
                    }
                    else
                    {
                        await Task.Delay(20, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when terminal closes.
            }
            catch
            {
                // Connection-close state will be handled in UI.
            }
            finally
            {
                SessionClosed?.Invoke();
            }
        }

        private bool IsProcessExited()
        {
            if (_processInformation.hProcess == IntPtr.Zero)
            {
                return true;
            }

            return WaitForSingleObject(_processInformation.hProcess, 0) == WaitObject0;
        }

        private void CleanupNativeResources()
        {
            _started = false;

            _inputWriter?.Dispose();
            _inputWriter = null;
            _outputReader?.Dispose();
            _outputReader = null;

            if (_processInformation.hProcess != IntPtr.Zero)
            {
                if (!IsProcessExited())
                {
                    TerminateProcess(_processInformation.hProcess, 0);
                }

                CloseHandle(_processInformation.hProcess);
                _processInformation.hProcess = IntPtr.Zero;
            }

            if (_processInformation.hThread != IntPtr.Zero)
            {
                CloseHandle(_processInformation.hThread);
                _processInformation.hThread = IntPtr.Zero;
            }

            if (_ptyInputWriter != IntPtr.Zero)
            {
                CloseHandle(_ptyInputWriter);
                _ptyInputWriter = IntPtr.Zero;
            }

            if (_ptyOutputReader != IntPtr.Zero)
            {
                CloseHandle(_ptyOutputReader);
                _ptyOutputReader = IntPtr.Zero;
            }

            if (_attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(_attributeList);
                Marshal.FreeHGlobal(_attributeList);
                _attributeList = IntPtr.Zero;
            }

            if (_pseudoConsole != IntPtr.Zero)
            {
                ClosePseudoConsole(_pseudoConsole);
                _pseudoConsole = IntPtr.Zero;
            }

            _readLoopCts?.Dispose();
            _readLoopCts = null;
            _readLoopTask = null;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ConPtyTerminalSession));
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Coord
        {
            public short X;
            public short Y;

            public Coord(short x, short y)
            {
                X = x;
                Y = y;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public uint cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public uint dwX;
            public uint dwY;
            public uint dwXSize;
            public uint dwYSize;
            public uint dwXCountChars;
            public uint dwYCountChars;
            public uint dwFillAttribute;
            public uint dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx
        {
            public StartupInfo StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeProcessInformation
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(
            out IntPtr hReadPipe,
            out IntPtr hWritePipe,
            ref SecurityAttributes lpPipeAttributes,
            int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(
            Coord size,
            IntPtr hInput,
            IntPtr hOutput,
            uint dwFlags,
            out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int ResizePseudoConsole(IntPtr hPC, Coord size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(
            IntPtr lpAttributeList,
            int dwAttributeCount,
            int dwFlags,
            ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList,
            uint dwFlags,
            IntPtr attribute,
            IntPtr lpValue,
            IntPtr cbSize,
            IntPtr lpPreviousValue,
            IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(
            string? lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            [In] ref StartupInfoEx lpStartupInfo,
            out NativeProcessInformation lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
    }
}
