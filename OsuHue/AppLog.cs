using System.IO;

namespace OsuHue;

internal static class AppLog
{
    private static readonly object gate = new();

    public static void Error(string operation, Exception exception)
    {
        // Log only exception types: third-party messages can contain credentials.
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(Settings.DataFolder);
                var path = Path.Combine(Settings.DataFolder, "errors.log");
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                    File.Move(path, path + ".previous", true);
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {operation}: {exception.GetType().FullName}\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A read-only profile must not prevent cleanup or error reporting.
            }
        }
    }
}
