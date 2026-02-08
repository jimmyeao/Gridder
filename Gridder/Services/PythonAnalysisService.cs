using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Gridder.Helpers;
using Gridder.Models;

namespace Gridder.Services;

public class PythonAnalysisService : IPythonAnalysisService
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public static string LogFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Gridder", "analysis.log");

    private static void Log(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogFilePath)!;
            Directory.CreateDirectory(dir);
            File.AppendAllText(LogFilePath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    public async Task<bool> CheckPythonAvailableAsync()
    {
        var python = await PythonLocator.FindPythonAsync();
        return python != null;
    }

    public async Task<AnalysisResult> AnalyzeTrackAsync(
        string filePath,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        Log($"=== Analysis started for: {filePath}");

        var pythonCommand = await PythonLocator.FindPythonAsync();
        if (pythonCommand == null)
        {
            Log("ERROR: Python 3 not found on PATH");
            throw new InvalidOperationException(
                "Python 3 not found. Please install Python 3.10+ and ensure it's on your PATH.");
        }
        Log($"Python command: {pythonCommand}");

        string pythonDir;
        try
        {
            pythonDir = PythonLocator.GetAnalysisPackagePath();
        }
        catch (DirectoryNotFoundException ex)
        {
            Log($"ERROR: {ex.Message}");
            throw new InvalidOperationException(ex.Message, ex);
        }
        Log($"Python package dir: {pythonDir}");

        progress?.Report("Starting analysis...");

        // Build the process arguments
        // Handle "py -3" style commands
        var parts = pythonCommand.Split(' ', 2);
        var fileName = parts[0];
        var baseArgs = parts.Length > 1 ? parts[1] + " " : "";
        var arguments = $"{baseArgs}-m gridder_analysis \"{filePath}\"";
        Log($"Command: {fileName} {arguments}");

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = pythonDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Set PYTHONPATH so the module can be found
        psi.Environment["PYTHONPATH"] = pythonDir;

        using var process = new Process { StartInfo = psi };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
                stdout.AppendLine(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stderr.AppendLine(e.Data);
                Log($"[stderr] {e.Data}");
                progress?.Report(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Wait for process with timeout and cancellation
        using var timeoutCts = new CancellationTokenSource(DefaultTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }

            if (timeoutCts.IsCancellationRequested)
                throw new TimeoutException($"Analysis timed out after {DefaultTimeout.TotalMinutes} minutes.");
            throw;
        }

        Log($"Process exited with code: {process.ExitCode}");

        if (process.ExitCode != 0)
        {
            var errorMsg = stderr.ToString().Trim();
            Log($"ERROR (exit {process.ExitCode}):\n{errorMsg}");
            throw new InvalidOperationException(
                $"Python analysis failed (exit code {process.ExitCode}):\n{errorMsg}");
        }

        // Parse the JSON output
        var jsonOutput = stdout.ToString().Trim();
        Log($"stdout length: {jsonOutput.Length} chars");

        if (string.IsNullOrEmpty(jsonOutput))
        {
            var msg = "Python analysis produced no output. stderr:\n" + stderr;
            Log($"ERROR: {msg}");
            throw new InvalidOperationException(msg);
        }

        try
        {
            var result = JsonSerializer.Deserialize<AnalysisResult>(jsonOutput);
            if (result == null)
                throw new InvalidOperationException("Failed to deserialize analysis result.");

            Log($"SUCCESS: {result.Beats?.Length ?? 0} beats, {result.TempoSegments?.Length ?? 0} segments");
            progress?.Report("Analysis complete!");
            return result;
        }
        catch (JsonException ex)
        {
            var snippet = jsonOutput[..Math.Min(500, jsonOutput.Length)];
            Log($"JSON parse error: {ex.Message}\nRaw: {snippet}");
            throw new InvalidOperationException(
                $"Failed to parse analysis output: {ex.Message}\nRaw output: {snippet}", ex);
        }
    }
}
