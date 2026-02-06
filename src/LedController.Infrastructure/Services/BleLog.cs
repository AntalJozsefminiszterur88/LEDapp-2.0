using System;
using System.IO;
using System.Text;

namespace LedController.Infrastructure.Services;

internal static class BleLog
{
    private static readonly object Sync = new();
    private static readonly string LogDirectory = ResolveLogDirectory();

    internal static string LogFilePath => Path.Combine(LogDirectory, "ble.log");

    internal static void Info(string message)
    {
        Write("INFO", message);
    }

    internal static void Error(string message)
    {
        Write("ERROR", message);
    }

    internal static void Exception(string context, Exception ex)
    {
        var details = new StringBuilder()
            .AppendLine($"{context}")
            .AppendLine(ex.ToString())
            .ToString();

        Write("EXCEPTION", details);
    }

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);

            var line = $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}";
            lock (Sync)
            {
                File.AppendAllText(LogFilePath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Swallow logging errors to avoid breaking BLE workflows.
        }
    }

    private static string ResolveLogDirectory()
    {
        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrWhiteSpace(current); i++)
        {
            if (File.Exists(Path.Combine(current, "LedController.sln"))
                || Directory.Exists(Path.Combine(current, ".git")))
            {
                return Path.Combine(current, "logs");
            }

            current = Directory.GetParent(current)?.FullName;
        }

        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UMKGL Solutions",
            "LEDapp",
            "logs");

        return baseDir;
    }
}
