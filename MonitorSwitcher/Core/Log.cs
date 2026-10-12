using System.Diagnostics;
using System.IO;

namespace MonitorSwitcher.Core;

/// <summary>
/// Appends diagnostics to monitor-switcher.log next to config.json, so problems on
/// a user's machine can be investigated after the fact. Never throws.
/// </summary>
public static class Log
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(ConfigService.ConfigDirectory, "monitor-switcher.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} [{Thread.CurrentThread.ManagedThreadId}] {message}";
        Trace.WriteLine(line);

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(ConfigService.ConfigDirectory);

                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes)
                {
                    string previous = Path.ChangeExtension(FilePath, ".old.log");
                    File.Delete(previous);
                    File.Move(FilePath, previous);
                }

                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
    }
}
