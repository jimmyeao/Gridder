using Gridder.Helpers;
using Gridder.Models;
using Gridder.Services.Audio;
using Gridder.Services.Audio.BeatDetection;
using Gridder.Services.Audio.PostProcessing;

namespace Gridder.Services;

/// <summary>
/// Pure C# track analysis service — replaces PythonAnalysisService.
/// Orchestrates: audio loading → beat detection → post-processing → tempo segmentation → waveform.
/// </summary>
public class TrackAnalysisService : ITrackAnalysisService, IDisposable
{
    private readonly ISeratoTagService _seratoTagService;
    private readonly BeatDetector _beatDetector = new();
    private readonly BeatPostProcessor _postProcessor = new();
    private bool _disposed;

    public TrackAnalysisService(ISeratoTagService seratoTagService)
    {
        _seratoTagService = seratoTagService;

        // Try to find ONNX model in app resources
        var modelPath = FindOnnxModel();
        if (modelPath != null)
            _beatDetector.SetModelPath(modelPath);
    }

    public async Task<AnalysisResult> AnalyzeTrackAsync(
        string filePath,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        double? firstBeatSeconds = null)
    {
        AppLogger.Log("Analysis", $"=== Analysis started for: {filePath}");
        progress?.Report($"Analyzing: {Path.GetFileName(filePath)}");

        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            // Step 1: Load audio
            progress?.Report("Loading audio...");
            var (samples, sr) = AudioDecoder.Decode(filePath);
            double duration = (double)samples.Length / sr;
            progress?.Report($"  Duration: {duration:F1}s, Sample rate: {sr}Hz");
            AppLogger.Log("Analysis", $"  Duration: {duration:F1}s, Sample rate: {sr}Hz");

            ct.ThrowIfCancellationRequested();

            // Step 2: Detect beats
            progress?.Report("Detecting beats...");
            var signalDouble = new double[samples.Length];
            for (int i = 0; i < samples.Length; i++)
                signalDouble[i] = samples[i];

            var (beatTimes, detector) = _beatDetector.DetectBeats(signalDouble, sr, progress);
            progress?.Report($"  Beat detector: {detector}");
            AppLogger.Log("Analysis", $"  Beat detector: {detector}, beats: {beatTimes.Length}");

            if (beatTimes.Length == 0)
            {
                progress?.Report("Warning: No beats detected!");
                beatTimes = [0.0];
            }

            ct.ThrowIfCancellationRequested();

            // Step 3: Post-process beats (steps 2a-2f from Python pipeline)
            var postResult = _postProcessor.Process(
                beatTimes, samples, sr, filePath,
                firstBeatSeconds, _seratoTagService, progress);

            beatTimes = postResult.Beats;

            ct.ThrowIfCancellationRequested();

            // Step 4: Segment tempo
            progress?.Report("Analyzing tempo segments...");
            var segments = TempoSegmenter.SegmentTempo(beatTimes, progress: progress);

            AppLogger.Log("Analysis", $"  Tempo segments: {segments.Length}");
            foreach (var seg in segments)
                AppLogger.Log("Analysis", $"    Seg: {seg.Bpm:F2} BPM, {seg.BeatCount} beats, start={seg.StartPosition:F3}s");

            ct.ThrowIfCancellationRequested();

            // Step 5: Generate waveform data
            progress?.Report("Generating waveform...");
            var (samplesPerPixel, peaksPos, peaksNeg, onsetEnv) =
                WaveformGenerator.Generate(samples, sr);

            progress?.Report("Analysis complete!");

            // Build result
            var result = new AnalysisResult
            {
                Version = 1,
                FilePath = filePath,
                SampleRate = sr,
                DurationSeconds = Math.Round(duration, 3),
                Beats = beatTimes.Select(b => Math.Round(b, 4)).ToArray(),
                TempoSegments = segments.Select(s => new TempoSegment
                {
                    StartBeatIndex = s.StartBeatIndex,
                    EndBeatIndex = s.EndBeatIndex,
                    StartPosition = s.StartPosition,
                    Bpm = s.Bpm,
                    BeatCount = s.BeatCount,
                }).ToArray(),
                Waveform = new WaveformResult
                {
                    SamplesPerPixel = samplesPerPixel,
                    PeaksPositive = peaksPos,
                    PeaksNegative = peaksNeg,
                    OnsetEnvelope = onsetEnv,
                },
            };

            AppLogger.Log("Analysis", $"SUCCESS: {result.Beats.Length} beats, {result.TempoSegments.Length} segments");
            return result;

        }, ct);
    }

    private static string? FindOnnxModel()
    {
        // Check common locations for the ONNX model
        var candidates = new[]
        {
            // MAUI app resources (will be extracted to app data)
            Path.Combine(FileSystem.AppDataDirectory, "beats_blstm.onnx"),
            // Alongside the executable
            Path.Combine(AppContext.BaseDirectory, "Resources", "Raw", "beats_blstm.onnx"),
            Path.Combine(AppContext.BaseDirectory, "beats_blstm.onnx"),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _beatDetector.Dispose();
            _disposed = true;
        }
    }
}
