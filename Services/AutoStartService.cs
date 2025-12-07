using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace GitDeployPro.Services
{
    public class AutoStartService
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "GitDeployPro";

        public void SetAutoStart(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
                if (key == null) return;

                if (enable)
                {
                    var exePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                    key.SetValue(ValueName, $"\"{exePath}\"");
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                }
            }
            catch
            {
            }
        }

        public bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                if (key == null) return false;
                var value = key.GetValue(ValueName) as string;
                return !string.IsNullOrWhiteSpace(value);
            }
            catch
            {
                return false;
            }
        }
    }
}

