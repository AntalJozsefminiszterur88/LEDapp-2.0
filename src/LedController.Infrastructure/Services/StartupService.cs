using System.Reflection;
using LedController.Core.Interfaces;
using Microsoft.Win32;

namespace LedController.Infrastructure.Services;

public sealed class StartupService : IStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LEDapp-2.0";

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                var command = BuildStartupCommand();
                if (string.IsNullOrWhiteSpace(command))
                {
                    return false;
                }

                key.SetValue(ValueName, command, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildStartupCommand()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            processPath = Assembly.GetEntryAssembly()?.Location;
        }

        if (string.IsNullOrWhiteSpace(processPath))
        {
            return string.Empty;
        }

        if (string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrWhiteSpace(entryAssemblyPath))
            {
                return $"\"{processPath}\" \"{entryAssemblyPath}\"";
            }
        }

        if (string.Equals(Path.GetExtension(processPath), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            return $"\"dotnet\" \"{processPath}\"";
        }

        return $"\"{processPath}\"";
    }
}
