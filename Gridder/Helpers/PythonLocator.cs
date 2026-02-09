using System.Diagnostics;

namespace Gridder.Helpers;

public static class PythonLocator
{
    private static string? _cachedPythonPath;
    private static string? _cachedStandaloneExe;

    /// <summary>
    /// Find the standalone gridder_analysis executable (PyInstaller build).
    /// Returns the full path if found, null otherwise.
    /// </summary>
    public static string? FindStandaloneExe()
    {
        if (_cachedStandaloneExe != null)
            return File.Exists(_cachedStandaloneExe) ? _cachedStandaloneExe : null;

        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var exeName = OperatingSystem.IsWindows() ? "gridder_analysis.exe" : "gridder_analysis";

        string[] searchPaths =
        [
            // Published alongside app
            Path.Combine(appDir, "gridder_analysis", exeName),
            // Sibling directory
            Path.GetFullPath(Path.Combine(appDir, "..", "gridder_analysis", exeName)),
            // Dev: built in python/dist/
            Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "..", "..", "python", "dist", "gridder_analysis", exeName)),
            Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "..", "python", "dist", "gridder_analysis", exeName)),
        ];

        foreach (var path in searchPaths)
        {
            if (File.Exists(path))
            {
                _cachedStandaloneExe = Path.GetFullPath(path);
                AppLogger.Log("Python", $"Found standalone exe: {_cachedStandaloneExe}");
                return _cachedStandaloneExe;
            }
        }

        return null;
    }

    /// <summary>
    /// Find a usable Python 3 executable on this system.
    /// Prefers the project venv (python/.venv) which has madmom installed.
    /// </summary>
    public static async Task<string?> FindPythonAsync()
    {
        if (_cachedPythonPath != null)
            return _cachedPythonPath;

        // Check for project venv first (has madmom + all deps)
        var venvPython = FindVenvPython();
        if (venvPython != null)
        {
            var result = await TryPythonAsync(venvPython);
            if (result != null)
            {
                _cachedPythonPath = result;
                AppLogger.Log("Python", $"Using venv Python: {result}");
                return result;
            }
        }

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
                AppLogger.Log("Python", $"Using system Python: {result}");
                return result;
            }
        }

        return null;
    }

    /// <summary>
    /// Look for the project's Python venv which has madmom and all dependencies.
    /// </summary>
    private static string? FindVenvPython()
    {
        try
        {
            var pythonDir = GetAnalysisPackagePath();
            var venvPython = OperatingSystem.IsWindows()
                ? Path.Combine(pythonDir, ".venv", "Scripts", "python.exe")
                : Path.Combine(pythonDir, ".venv", "bin", "python3");

            return File.Exists(venvPython) ? venvPython : null;
        }
        catch
        {
            return null;
        }
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
