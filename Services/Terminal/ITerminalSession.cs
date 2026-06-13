using System;
using System.Threading;
using System.Threading.Tasks;

namespace GitDeployPro.Services.Terminal
{
    public interface ITerminalSession : IAsyncDisposable
    {
        event Action<string>? OutputReceived;
        event Action? SessionClosed;

        bool IsConnected { get; }

        Task StartAsync(CancellationToken cancellationToken = default);
        Task WriteAsync(string data, CancellationToken cancellationToken = default);
        Task SendInterruptAsync(CancellationToken cancellationToken = default);
        Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default);
        Task StopAsync(CancellationToken cancellationToken = default);
    }
}
