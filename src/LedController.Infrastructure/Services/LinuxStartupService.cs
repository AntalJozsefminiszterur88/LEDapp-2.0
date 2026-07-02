using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using LedController.Core.Interfaces;

namespace LedController.Infrastructure.Services;

public sealed class LinuxStartupService : IStartupService
{
    private const string StartupHiddenArgument = "--start-minimized-to-tray";
    private const string DesktopFileName = "LEDapp-2.0.desktop";
    private const string UserServiceFileName = "ledapp-2.0.service";

    private readonly string _desktopEntryPath;
    private readonly string _userServicePath;

    public string DesktopEntryPath => _desktopEntryPath;
    public string UserServicePath => _userServicePath;

    public LinuxStartupService()
    {
        var configDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _desktopEntryPath = Path.Combine(configDirectory, "autostart", DesktopFileName);
        _userServicePath = Path.Combine(configDirectory, "systemd", "user", UserServiceFileName);
    }

    public bool IsEnabled()
    {
        try
        {
            return File.Exists(_userServicePath) || File.Exists(_desktopEntryPath);
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
                if (TryEnableUserService())
                {
                    DeleteFileIfExists(_desktopEntryPath);
                    return true;
                }

                return TryEnableDesktopEntry();
            }

            TryDisableUserService();
            DeleteFileIfExists(_desktopEntryPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void NormalizeExistingRegistration()
    {
        try
        {
            if (File.Exists(_userServicePath))
            {
                var expectedService = BuildUserService();
                if (!string.IsNullOrWhiteSpace(expectedService))
                {
                    WriteFileIfChanged(_userServicePath, expectedService);
                    RunSystemctlUser("daemon-reload");
                }

                DeleteFileIfExists(_desktopEntryPath);
                return;
            }

            if (File.Exists(_desktopEntryPath))
            {
                var expectedDesktopEntry = BuildDesktopEntry();
                if (!string.IsNullOrWhiteSpace(expectedDesktopEntry))
                {
                    WriteFileIfChanged(_desktopEntryPath, expectedDesktopEntry);
                }
            }
        }
        catch
        {
        }
    }

    private bool TryEnableDesktopEntry()
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

        WriteFileIfChanged(_desktopEntryPath, content);
        return true;
    }

    private bool TryEnableUserService()
    {
        var serviceContent = BuildUserService();
        if (string.IsNullOrWhiteSpace(serviceContent))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(_userServicePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        WriteFileIfChanged(_userServicePath, serviceContent);
        if (RunSystemctlUser("daemon-reload") &&
            RunSystemctlUser("enable", UserServiceFileName))
        {
            RunSystemctlUser("start", UserServiceFileName);
            return true;
        }

        DeleteFileIfExists(_userServicePath);
        RunSystemctlUser("daemon-reload");
        return false;
    }

    private void TryDisableUserService()
    {
        try
        {
            if (File.Exists(_userServicePath))
            {
                RunSystemctlUser("disable", "--now", UserServiceFileName);
                File.Delete(_userServicePath);
                RunSystemctlUser("daemon-reload");
            }
        }
        catch
        {
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
        var workingDirectory = ResolveWorkingDirectory();
        var lines = new List<string>
        {
            "[Desktop Entry]",
            "Type=Application",
            "Version=1.0",
            "Name=LEDapp-2.0",
            "Comment=Bluetooth LED controller",
            $"Exec={command}"
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            lines.Add($"Path={workingDirectory}");
        }

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

    private string BuildUserService()
    {
        var launcherPath = ResolveLauncherPath();
        if (string.IsNullOrWhiteSpace(launcherPath))
        {
            return string.Empty;
        }

        var workingDirectory = Path.GetDirectoryName(launcherPath) ?? string.Empty;
        var lines = new List<string>
        {
            "[Unit]",
            "Description=LEDapp 2.0 tray controller",
            "After=graphical-session.target",
            "Wants=graphical-session.target",
            "StartLimitIntervalSec=0",
            string.Empty,
            "[Service]",
            "Type=simple",
            $"WorkingDirectory={workingDirectory}",
            $"ExecStart={launcherPath} {StartupHiddenArgument}",
            "Restart=on-failure",
            "RestartSec=5"
        };

        AppendEnvironmentLine(lines, "DISPLAY");
        AppendEnvironmentLine(lines, "XDG_RUNTIME_DIR");
        AppendEnvironmentLine(lines, "DBUS_SESSION_BUS_ADDRESS");
        AppendEnvironmentLine(lines, "XDG_CURRENT_DESKTOP");
        AppendEnvironmentLine(lines, "XDG_SESSION_TYPE");

        lines.Add(string.Empty);
        lines.Add("[Install]");
        lines.Add("WantedBy=default.target");
        lines.Add(string.Empty);

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildStartupCommand()
    {
        var launcherPath = ResolveLauncherPath();
        if (!string.IsNullOrWhiteSpace(launcherPath))
        {
            return $"{QuoteDesktopArgument(launcherPath)} {StartupHiddenArgument}";
        }

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

    private static string ResolveWorkingDirectory()
    {
        var launcherPath = ResolveLauncherPath();
        if (!string.IsNullOrWhiteSpace(launcherPath))
        {
            return Path.GetDirectoryName(launcherPath) ?? string.Empty;
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            processPath = Assembly.GetEntryAssembly()?.Location;
        }

        return string.IsNullOrWhiteSpace(processPath)
            ? string.Empty
            : Path.GetDirectoryName(processPath) ?? string.Empty;
    }

    private static string ResolveLauncherPath()
    {
        foreach (var baseDirectory in EnumerateBaseDirectories())
        {
            var current = baseDirectory;
            for (var i = 0; i < 8 && !string.IsNullOrWhiteSpace(current); i++)
            {
                var candidate = Path.Combine(current, "inditas-linux.sh");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                current = Directory.GetParent(current)?.FullName;
            }
        }

        return string.Empty;
    }

    private static IEnumerable<string> EnumerateBaseDirectories()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var processDirectory = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrWhiteSpace(processDirectory))
            {
                yield return processDirectory;
            }
        }

        var entryAssemblyLocation = Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrWhiteSpace(entryAssemblyLocation))
        {
            var entryDirectory = Path.GetDirectoryName(entryAssemblyLocation);
            if (!string.IsNullOrWhiteSpace(entryDirectory))
            {
                yield return entryDirectory;
            }
        }
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
        var launcherPath = ResolveLauncherPath();
        if (!string.IsNullOrWhiteSpace(launcherPath))
        {
            var launcherDirectory = Path.GetDirectoryName(launcherPath);
            if (!string.IsNullOrWhiteSpace(launcherDirectory))
            {
                var launcherCandidates = new[]
                {
                    Path.Combine(launcherDirectory, "src", "LedController.UI", "Assets", "logo.png"),
                    Path.Combine(launcherDirectory, "src", "LedController.UI", "Assets", "logo.ico"),
                    Path.Combine(launcherDirectory, "src", "LedController.UI", "Assets", "trayicon.png")
                };

                var launcherIcon = launcherCandidates.FirstOrDefault(File.Exists);
                if (!string.IsNullOrWhiteSpace(launcherIcon))
                {
                    return launcherIcon;
                }
            }
        }

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

    private static void AppendEnvironmentLine(ICollection<string> lines, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            lines.Add($"Environment={name}={value}");
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void WriteFileIfChanged(string path, string content)
    {
        var existing = File.Exists(path)
            ? File.ReadAllText(path)
            : null;

        if (string.Equals(existing, content, StringComparison.Ordinal))
        {
            return;
        }

        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static bool RunSystemctlUser(params string[] arguments)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "systemctl",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            process.StartInfo.ArgumentList.Add("--user");
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            process.WaitForExit(10000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
