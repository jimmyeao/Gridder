using System.Diagnostics;

namespace Gridder.Helpers;

public static class PythonLocator
{
    private static string? _cachedPythonPath;

    /// <summary>
    /// Find a usable Python 3 executable on this system.
    /// </summary>
    public static async Task<string?> FindPythonAsync()
    {
        if (_cachedPythonPath != null)
            return _cachedPythonPath;

        // Candidates differ by platform
        string[] candidates;

        if (OperatingSystem.IsWindows())
        {
            candidates = ["python", "python3", "py -3"];
        }
        else if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            candidates = [
                "python3",
                "/opt/homebrew/bin/python3",
                "/usr/local/bin/python3",
                "/usr/bin/python3",
            ];
        }
        else
        {
            candidates = ["python3", "python"];
        }

        foreach (var candidate in candidates)
        {
            var result = await TryPythonAsync(candidate);
            if (result != null)
            {
                _cachedPythonPath = result;
                return result;
            }
        }

        return null;
    }

    private static async Task<string?> TryPythonAsync(string command)
    {
        try
        {
            // Split command for cases like "py -3"
            var parts = command.Split(' ', 2);
            var psi = new ProcessStartInfo
            {
                FileName = parts[0],
                Arguments = parts.Length > 1 ? $"{parts[1]} --version" : "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0) return null;

            var version = (output + error).Trim();
            if (version.StartsWith("Python 3"))
            {
                // Return the full command (not just the executable) for "py -3" style
                return command;
            }
        }
        catch
        {
            // Command not found or other error
        }

        return null;
    }

    /// <summary>
    /// Get the path to the gridder_analysis Python package.
    /// </summary>
    public static string GetAnalysisPackagePath()
    {
        // Look for the python directory relative to the app
        var appDir = AppDomain.CurrentDomain.BaseDirectory;

        // Try several possible locations
        string[] searchPaths =
        [
            Path.Combine(appDir, "..", "..", "..", "..", "..", "python"),       // dev: from bin/Debug/net10.0-windows/.../
            Path.Combine(appDir, "..", "..", "..", "..", "python"),             // dev: shorter path
            Path.Combine(appDir, "python"),                                     // published alongside app
            Path.GetFullPath(Path.Combine(appDir, "..", "python")),            // sibling directory
        ];

        foreach (var path in searchPaths)
        {
            var fullPath = Path.GetFullPath(path);
            if (Directory.Exists(Path.Combine(fullPath, "gridder_analysis")))
                return fullPath;
        }

        // Last resort: look relative to the solution root
        var dir = new DirectoryInfo(appDir);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "python", "gridder_analysis");
            if (Directory.Exists(candidate))
                return Path.Combine(dir.FullName, "python");
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find the 'python/gridder_analysis' directory. " +
            "Ensure the Python analysis package is deployed alongside the app.");
    }
}
