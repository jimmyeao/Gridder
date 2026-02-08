namespace Gridder.Helpers;

/// <summary>
/// Simple file logger for diagnostics.
/// Writes to %LOCALAPPDATA%\Gridder\gridder.log
/// </summary>
public static class AppLogger
{
    public static string LogFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Gridder", "gridder.log");

    public static void Log(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogFilePath)!;
            Directory.CreateDirectory(dir);
            File.AppendAllText(LogFilePath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    public static void Log(string category, string message)
    {
        Log($"[{category}] {message}");
    }
}
