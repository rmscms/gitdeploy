using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace GitDeployPro.Services.Telegram
{
    /// <summary>
    /// Shared Codex CLI install helpers (Settings + help modal).
    /// Prefers npm (visible progress). Official installer runs with a heartbeat so the window
    /// does not look frozen during a silent download.
    /// </summary>
    public static class CodexInstallHelper
    {
        public static void OpenInstallTerminal()
        {
            var shell = ResolvePowerShellExe();
            var script = string.Join(Environment.NewLine, new[]
            {
                "$ErrorActionPreference = 'Continue'",
                "function Write-Heartbeat([string]$msg) {",
                "  Write-Host (('[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $msg)) -ForegroundColor DarkCyan",
                "}",
                "function Install-CodexViaNpm {",
                "  if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {",
                "    Write-Host 'npm not found. Install Node.js LTS from https://nodejs.org then re-run Install CLI.' -ForegroundColor Red",
                "    return $false",
                "  }",
                "  Write-Host 'Installing via npm (progress shown by npm)…' -ForegroundColor Cyan",
                "  npm install -g @openai/codex --loglevel=info",
                "  if ($LASTEXITCODE -ne 0) {",
                "    Write-Host ('npm failed with exit ' + $LASTEXITCODE) -ForegroundColor Red",
                "    return $false",
                "  }",
                "  return $true",
                "}",
                "function Install-CodexOfficialWithHeartbeat {",
                "  try { Import-Module Microsoft.PowerShell.Utility -ErrorAction SilentlyContinue } catch {}",
                "  if (-not (Get-Command Get-FileHash -ErrorAction SilentlyContinue)) {",
                "    Write-Host 'Get-FileHash missing — cannot use official installer.' -ForegroundColor Yellow",
                "    return $false",
                "  }",
                "  Write-Host 'Official installer (download can look idle — heartbeat every 3s)…' -ForegroundColor Cyan",
                "  $job = Start-Job -ScriptBlock {",
                "    try {",
                "      irm 'https://chatgpt.com/codex/install.ps1' | iex",
                "      return @{ Ok = $true; Error = $null }",
                "    } catch {",
                "      return @{ Ok = $false; Error = $_.Exception.Message }",
                "    }",
                "  }",
                "  while ($job.State -eq 'Running') {",
                "    Write-Heartbeat 'Still downloading / installing Codex… please wait'",
                "    Start-Sleep -Seconds 3",
                "  }",
                "  $result = Receive-Job $job -ErrorAction SilentlyContinue",
                "  Remove-Job $job -Force -ErrorAction SilentlyContinue",
                "  if ($null -eq $result) { return $false }",
                "  if (-not $result.Ok) {",
                "    Write-Host ('Official installer failed: ' + $result.Error) -ForegroundColor Yellow",
                "    return $false",
                "  }",
                "  return $true",
                "}",
                "Write-Host '=== GitDeploy: Installing Codex CLI ===' -ForegroundColor Cyan",
                "Write-Host ('Shell: ' + $PSVersionTable.PSVersion) -ForegroundColor DarkGray",
                "Write-Host ''",
                "$ok = $false",
                "if (Get-Command npm -ErrorAction SilentlyContinue) {",
                "  $ok = Install-CodexViaNpm",
                "} else {",
                "  Write-Host 'npm not on PATH — trying official installer…' -ForegroundColor Yellow",
                "}",
                "if (-not $ok) {",
                "  $ok = Install-CodexOfficialWithHeartbeat",
                "}",
                "if (-not $ok -and (Get-Command npm -ErrorAction SilentlyContinue)) {",
                "  Write-Host 'Retrying npm as last resort…' -ForegroundColor Yellow",
                "  $ok = Install-CodexViaNpm",
                "}",
                "Write-Host ''",
                "if ($ok -or (Get-Command codex -ErrorAction SilentlyContinue)) {",
                "  Write-Host '=== Install finished ===' -ForegroundColor Green",
                "  Get-Command codex -ErrorAction SilentlyContinue | Format-List Source,Version",
                "  try { codex --version } catch {}",
                "} else {",
                "  Write-Host '=== Install did not complete ===' -ForegroundColor Red",
                "  Write-Host 'Install Node.js LTS, then click Install CLI again.' -ForegroundColor Yellow",
                "}",
                "Write-Host ''",
                "Write-Host 'Next in GitDeploy: Detect Codex → OpenRouter key → Save Codex → Test connection.' -ForegroundColor Yellow",
                "Write-Host ''",
                "pause"
            });

            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            Process.Start(new ProcessStartInfo
            {
                FileName = shell,
                Arguments = "-NoExit -NoProfile -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                UseShellExecute = true
            });
        }

        /// <summary>
        /// Opens a visible PowerShell window that upgrades Codex via npm @latest (or official installer).
        /// </summary>
        public static void OpenUpdateTerminal()
        {
            var shell = ResolvePowerShellExe();
            var script = string.Join(Environment.NewLine, new[]
            {
                "$ErrorActionPreference = 'Continue'",
                "function Write-Heartbeat([string]$msg) {",
                "  Write-Host (('[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $msg)) -ForegroundColor DarkCyan",
                "}",
                "Write-Host '=== GitDeploy: Updating Codex CLI ===' -ForegroundColor Cyan",
                "Write-Host ('Shell: ' + $PSVersionTable.PSVersion) -ForegroundColor DarkGray",
                "Write-Host ''",
                "$before = $null",
                "try { $before = (codex --version 2>$null) } catch {}",
                "if ($before) { Write-Host ('Current: ' + $before) -ForegroundColor DarkGray }",
                "$ok = $false",
                "if (Get-Command npm -ErrorAction SilentlyContinue) {",
                "  Write-Host 'Updating via npm (@latest, progress shown by npm)…' -ForegroundColor Cyan",
                "  npm install -g @openai/codex@latest --loglevel=info",
                "  if ($LASTEXITCODE -eq 0) { $ok = $true }",
                "  else { Write-Host ('npm update failed with exit ' + $LASTEXITCODE) -ForegroundColor Yellow }",
                "} else {",
                "  Write-Host 'npm not on PATH — trying official installer…' -ForegroundColor Yellow",
                "}",
                "if (-not $ok) {",
                "  try { Import-Module Microsoft.PowerShell.Utility -ErrorAction SilentlyContinue } catch {}",
                "  if (Get-Command Get-FileHash -ErrorAction SilentlyContinue) {",
                "    Write-Host 'Official installer (heartbeat every 3s)…' -ForegroundColor Cyan",
                "    $job = Start-Job -ScriptBlock {",
                "      try { irm 'https://chatgpt.com/codex/install.ps1' | iex; @{ Ok = $true; Error = $null }",
                "      } catch { @{ Ok = $false; Error = $_.Exception.Message } }",
                "    }",
                "    while ($job.State -eq 'Running') {",
                "      Write-Heartbeat 'Still downloading / updating Codex…'",
                "      Start-Sleep -Seconds 3",
                "    }",
                "    $result = Receive-Job $job -ErrorAction SilentlyContinue",
                "    Remove-Job $job -Force -ErrorAction SilentlyContinue",
                "    if ($result -and $result.Ok) { $ok = $true }",
                "    elseif ($result) { Write-Host ('Official update failed: ' + $result.Error) -ForegroundColor Yellow }",
                "  }",
                "}",
                "Write-Host ''",
                "$after = $null",
                "try { $after = (codex --version 2>$null) } catch {}",
                "if ($ok -or $after) {",
                "  Write-Host '=== Update finished ===' -ForegroundColor Green",
                "  if ($before) { Write-Host ('Before: ' + $before) }",
                "  if ($after) { Write-Host ('After:  ' + $after) -ForegroundColor Green }",
                "  Get-Command codex -ErrorAction SilentlyContinue | Format-List Source",
                "} else {",
                "  Write-Host '=== Update did not complete ===' -ForegroundColor Red",
                "  Write-Host 'Install Node.js LTS, then click Update CLI again.' -ForegroundColor Yellow",
                "}",
                "Write-Host ''",
                "pause"
            });

            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            Process.Start(new ProcessStartInfo
            {
                FileName = shell,
                Arguments = "-NoExit -NoProfile -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                UseShellExecute = true
            });
        }

        public static string? FindNpmExecutable()
        {
            return FindOnPath("npm.cmd")
                   ?? FindOnPath("npm.exe")
                   ?? FindOnPath("npm");
        }

        public static string ResolvePowerShellExe()
        {
            var pwsh = FindOnPath("pwsh.exe");
            if (!string.IsNullOrWhiteSpace(pwsh))
            {
                return pwsh;
            }

            var system = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            if (File.Exists(system))
            {
                return system;
            }

            return "powershell.exe";
        }

        public static void RefreshProcessPathFromSystem()
        {
            try
            {
                var machine = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? string.Empty;
                var user = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;
                Environment.SetEnvironmentVariable(
                    "PATH",
                    machine + Path.PathSeparator + user,
                    EnvironmentVariableTarget.Process);
            }
            catch
            {
            }
        }

        public static string? TryGetVersion(string codexPath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = codexPath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null)
                {
                    return null;
                }

                var output = p.StandardOutput.ReadToEnd();
                var err = p.StandardError.ReadToEnd();
                p.WaitForExit(8000);
                var text = (output + " " + err).Trim();
                return string.IsNullOrWhiteSpace(text) ? null : text.Split('\n')[0].Trim();
            }
            catch
            {
                return null;
            }
        }

        private static string? FindOnPath(string fileName)
        {
            try
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        var candidate = Path.Combine(dir.Trim().Trim('"'), fileName);
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            return null;
        }
    }
}
