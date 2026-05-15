using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LedController.UI.Services;

internal static class AppLog
{
    private static readonly object Sync = new();
    private static readonly string LogDirectory = ResolveLogDirectory();

    internal static string LogFilePath => Path.Combine(LogDirectory, "app.log");

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
            .AppendLine(context)
            .AppendLine(ex.ToString())
            .ToString();

        Write("EXCEPTION", details);
    }

    internal static bool IsBenignBackgroundException(Exception ex)
    {
        foreach (var candidate in Flatten(ex))
        {
            if (candidate.Message.Contains("com.canonical.AppMenu.Registrar", StringComparison.Ordinal))
            {
                return true;
            }

            if (candidate.Message.Contains("org.freedesktop.DBus.Error.ServiceUnknown", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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
            // Swallow logging errors to avoid crashing the app.
        }
    }

    private static string ResolveLogDirectory()
    {
        var repoLogDir = TryResolveRepoLogDirectory();
        if (!string.IsNullOrWhiteSpace(repoLogDir))
        {
            return repoLogDir;
        }

        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UMKGL Solutions",
            "LEDapp",
            "logs");

        return baseDir;
    }

    private static string? TryResolveRepoLogDirectory()
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

        return null;
    }

    private static IEnumerable<Exception> Flatten(Exception ex)
    {
        var pending = new Stack<Exception>();
        pending.Push(ex);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return current;

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }
    }

    internal static void Maintenance()
    {
        try
        {
            Rotate(LogFilePath);
            Rotate(Path.Combine(LogDirectory, "ble.log"));
        }
        catch
        {
        }
    }

    private static void Rotate(string filePath)
    {
        if (!File.Exists(filePath)) return;

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length < 5 * 1024 * 1024) return; // 5MB

        lock (Sync)
        {
            try
            {
                for (int i = 4; i >= 1; i--)
                {
                    var oldFile = $"{filePath}.{i}";
                    var newFile = $"{filePath}.{i + 1}";
                    if (File.Exists(oldFile))
                    {
                        if (i == 4) File.Delete(oldFile);
                        else File.Move(oldFile, newFile, true);
                    }
                }

                File.Move(filePath, $"{filePath}.1", true);
            }
            catch
            {
            }
        }
    }
}
