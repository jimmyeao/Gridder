using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Gridder.Helpers;

/// <summary>
/// Ensures ffmpeg is available on the system, downloading it if necessary.
/// ffmpeg is required by madmom for decoding MP3/M4A audio files.
///
/// Downloads are cached in: %LOCALAPPDATA%/Gridder/ffmpeg/
/// </summary>
public static class FfmpegProvider
{
    private static readonly string FfmpegDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Gridder", "ffmpeg");

    private static string FfmpegExeName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";

    private static string? _cachedPath;

    /// <summary>
    /// Get the path to the ffmpeg executable, downloading it if not already present.
    /// </summary>
    public static async Task<string> EnsureAvailableAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        // 1. Already resolved this session
        if (_cachedPath != null && File.Exists(_cachedPath))
            return _cachedPath;

        // 2. Already downloaded to our managed directory
        var managedPath = Path.Combine(FfmpegDir, FfmpegExeName);
        if (File.Exists(managedPath))
        {
            _cachedPath = managedPath;
            AppLogger.Log("ffmpeg", $"Using cached: {managedPath}");
            return managedPath;
        }

        // 3. Available on system PATH
        var systemPath = FindOnPath();
        if (systemPath != null)
        {
            _cachedPath = systemPath;
            AppLogger.Log("ffmpeg", $"Using system: {systemPath}");
            return systemPath;
        }

        // 4. Download it
        AppLogger.Log("ffmpeg", "Not found, downloading...");
        progress?.Report("Downloading ffmpeg (first-time setup)...");

        Directory.CreateDirectory(FfmpegDir);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            await DownloadWindowsAsync(progress, ct);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            await DownloadMacAsync(progress, ct);
        else
            throw new PlatformNotSupportedException(
                "Automatic ffmpeg download is not supported on this platform. " +
                "Please install ffmpeg manually (e.g. sudo apt install ffmpeg).");

        if (!File.Exists(managedPath))
            throw new FileNotFoundException(
                "ffmpeg download completed but the executable was not found.", managedPath);

        _cachedPath = managedPath;
        AppLogger.Log("ffmpeg", $"Downloaded to: {managedPath}");
        progress?.Report("ffmpeg ready.");
        return managedPath;
    }

    /// <summary>
    /// Returns the directory containing ffmpeg, for use in PATH injection.
    /// Returns null if ffmpeg hasn't been resolved yet.
    /// </summary>
    public static string? GetFfmpegDirectory()
    {
        if (_cachedPath != null)
            return Path.GetDirectoryName(_cachedPath);
        if (File.Exists(Path.Combine(FfmpegDir, FfmpegExeName)))
            return FfmpegDir;
        return null;
    }

    private static string? FindOnPath()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var separator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';

        foreach (var dir in pathVar.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), FfmpegExeName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Invalid path entry, skip
            }
        }

        return null;
    }

    /// <summary>
    /// Download ffmpeg essentials build for Windows (gyan.dev).
    /// The release zip contains: ffmpeg-*-essentials_build/bin/ffmpeg.exe
    /// </summary>
    private static async Task DownloadWindowsAsync(IProgress<string>? progress, CancellationToken ct)
    {
        // gyan.dev "release essentials" — smallest build with common codecs
        const string url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

        var zipPath = Path.Combine(FfmpegDir, "ffmpeg-download.zip");
        try
        {
            await DownloadFileAsync(url, zipPath, progress, ct);

            progress?.Report("Extracting ffmpeg...");
            var extractDir = Path.Combine(FfmpegDir, "_extract");
            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, recursive: true);

            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

            // Find ffmpeg.exe inside the extracted structure
            var ffmpegExe = Directory.GetFiles(extractDir, "ffmpeg.exe", SearchOption.AllDirectories)
                .FirstOrDefault()
                ?? throw new FileNotFoundException("ffmpeg.exe not found in downloaded archive.");

            File.Copy(ffmpegExe, Path.Combine(FfmpegDir, "ffmpeg.exe"), overwrite: true);

            // Also grab ffprobe if present (some libraries use it)
            var ffprobeExe = Directory.GetFiles(extractDir, "ffprobe.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (ffprobeExe != null)
                File.Copy(ffprobeExe, Path.Combine(FfmpegDir, "ffprobe.exe"), overwrite: true);

            // Clean up
            Directory.Delete(extractDir, recursive: true);
        }
        finally
        {
            if (File.Exists(zipPath))
                File.Delete(zipPath);
        }
    }

    /// <summary>
    /// Download ffmpeg static build for macOS (evermeet.cx).
    /// The download is a standalone binary in a zip archive.
    /// </summary>
    private static async Task DownloadMacAsync(IProgress<string>? progress, CancellationToken ct)
    {
        const string url = "https://evermeet.cx/ffmpeg/getrelease/zip";

        var zipPath = Path.Combine(FfmpegDir, "ffmpeg-download.zip");
        try
        {
            await DownloadFileAsync(url, zipPath, progress, ct);

            progress?.Report("Extracting ffmpeg...");
            ZipFile.ExtractToDirectory(zipPath, FfmpegDir, overwriteFiles: true);

            // Mark as executable on Unix platforms
            var ffmpegPath = Path.Combine(FfmpegDir, "ffmpeg");
            if (File.Exists(ffmpegPath) && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(ffmpegPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        finally
        {
            if (File.Exists(zipPath))
                File.Delete(zipPath);
        }
    }

    private static async Task DownloadFileAsync(
        string url, string destPath, IProgress<string>? progress, CancellationToken ct)
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long downloaded = 0;
        int bytesRead;
        var lastReport = DateTime.MinValue;

        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            downloaded += bytesRead;

            // Report progress at most once per second
            if (progress != null && totalBytes > 0 && DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(1))
            {
                var pct = (int)(downloaded * 100 / totalBytes.Value);
                var mb = downloaded / (1024.0 * 1024.0);
                progress.Report($"Downloading ffmpeg... {pct}% ({mb:F1} MB)");
                lastReport = DateTime.UtcNow;
            }
        }
    }
}
