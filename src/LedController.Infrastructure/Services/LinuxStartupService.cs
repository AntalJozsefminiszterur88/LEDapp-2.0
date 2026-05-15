using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using LedController.Core.Interfaces;

namespace LedController.Infrastructure.Services;

public sealed class LinuxStartupService : IStartupService
{
    private const string StartupHiddenArgument = "--start-minimized-to-tray";
    private const string DesktopFileName = "LEDapp-2.0.desktop";

    private readonly string _desktopEntryPath;

    public string DesktopEntryPath => _desktopEntryPath;

    public LinuxStartupService()
    {
        var configDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _desktopEntryPath = Path.Combine(configDirectory, "autostart", DesktopFileName);
    }

    public bool IsEnabled()
    {
        try
        {
            return File.Exists(_desktopEntryPath);
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
            if (enabled)
            {
                var content = BuildDesktopEntry();
                if (string.IsNullOrWhiteSpace(content))
                {
                    return false;
                }

                var directory = Path.GetDirectoryName(_desktopEntryPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(_desktopEntryPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            else if (File.Exists(_desktopEntryPath))
            {
                File.Delete(_desktopEntryPath);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private string BuildDesktopEntry()
    {
        var command = BuildStartupCommand();
        if (string.IsNullOrWhiteSpace(command))
        {
            return string.Empty;
        }

        var iconPath = ResolveIconPath();
        var lines = new List<string>
        {
            "[Desktop Entry]",
            "Type=Application",
            "Version=1.0",
            "Name=LEDapp-2.0",
            "Comment=Bluetooth LED controller",
            $"Exec={command}"
        };

        if (!string.IsNullOrWhiteSpace(iconPath))
        {
            lines.Add($"Icon={iconPath}");
        }

        lines.Add("Terminal=false");
        lines.Add("StartupNotify=true");
        lines.Add("StartupWMClass=LEDapp-2.0");
        lines.Add("Categories=Utility;");
        lines.Add("X-GNOME-Autostart-enabled=true");
        lines.Add(string.Empty);

        return string.Join(Environment.NewLine, lines);
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

        if (string.Equals(Path.GetFileName(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrWhiteSpace(entryAssemblyPath))
            {
                return $"{QuoteDesktopArgument("dotnet")} {QuoteDesktopArgument(entryAssemblyPath)} {StartupHiddenArgument}";
            }
        }

        if (string.Equals(Path.GetExtension(processPath), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            return $"{QuoteDesktopArgument("dotnet")} {QuoteDesktopArgument(processPath)} {StartupHiddenArgument}";
        }

        return $"{QuoteDesktopArgument(processPath)} {StartupHiddenArgument}";
    }

    private static string QuoteDesktopArgument(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal);

        return $"\"{escaped}\"";
    }

    private static string ResolveIconPath()
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

        var baseDirectory = string.Equals(Path.GetFileName(processPath), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location)
            : Path.GetDirectoryName(processPath);

        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return string.Empty;
        }

        var candidates = new[]
        {
            Path.Combine(baseDirectory, "logo.png"),
            Path.Combine(baseDirectory, "Assets", "logo.png"),
            Path.Combine(baseDirectory, "logo.ico"),
            Path.Combine(baseDirectory, "Assets", "logo.ico"),
            Path.Combine(baseDirectory, "trayicon.png"),
            Path.Combine(baseDirectory, "Assets", "trayicon.png")
        };

        var iconPath = candidates.FirstOrDefault(File.Exists);
        return iconPath ?? string.Empty;
    }
}
