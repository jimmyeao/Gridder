using Gridder.Models;

namespace Gridder.Services;

public interface IPythonAnalysisService
{
    Task<AnalysisResult> AnalyzeTrackAsync(string filePath, IProgress<string>? progress = null, CancellationToken ct = default, double? firstBeatSeconds = null);
    Task<bool> CheckPythonAvailableAsync();
}
