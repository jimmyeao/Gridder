namespace Gridder.Services.Audio.BeatDetection;

/// <summary>
/// Coordinator: tries ONNX+DBN (madmom) first, falls back to librosa-style.
/// </summary>
public class BeatDetector : IDisposable
{
    private readonly OnnxBeatActivation _onnx = new();
    private readonly DbnBeatTracker _dbn = new(fps: 100, minBpm: 40, maxBpm: 240, transitionLambda: 100);
    private bool _onnxInitialized;
    private string? _modelPath;

    /// <summary>
    /// Set the path to the ONNX model file.
    /// Call this before DetectBeats to enable the madmom path.
    /// </summary>
    public void SetModelPath(string modelPath)
    {
        _modelPath = modelPath;
    }

    /// <summary>
    /// Detect beats in the audio signal.
    /// Tries madmom (ONNX + DBN) first, falls back to librosa-style.
    /// Returns (beatTimes, detectorName).
    /// </summary>
    public (double[] Beats, string Detector) DetectBeats(double[] signal, int sr,
        IProgress<string>? progress = null)
    {
        // Try madmom path first
        var madmomBeats = TryMadmom(signal, sr, progress);
        if (madmomBeats != null && madmomBeats.Length > 0)
            return (madmomBeats, "madmom");

        // Fall back to librosa-style
        progress?.Report("  Using librosa-style beat tracker...");
        progress?.Report("  Separating percussive content (HPSS)...");

        var beats = LibrosaBeatTracker.DetectBeats(signal, sr);

        progress?.Report($"  Detected {beats.Length} beats (librosa)");
        return (beats, "librosa");
    }

    private double[]? TryMadmom(double[] signal, int sr, IProgress<string>? progress)
    {
        if (_modelPath == null) return null;

        try
        {
            // Initialize ONNX session on first use
            if (!_onnxInitialized)
            {
                _onnxInitialized = true;
                if (!_onnx.Initialize(_modelPath))
                {
                    progress?.Report("  ONNX model not available, falling back to librosa");
                    return null;
                }
            }

            progress?.Report("  Computing madmom features...");
            var features = MadmomPreprocessor.ExtractFeatures(signal, sr);

            progress?.Report("  Running ONNX beat activation...");
            var activations = _onnx.Predict(features);
            if (activations == null) return null;

            progress?.Report("  Running DBN beat tracking...");
            var beats = _dbn.Track(activations);

            if (beats.Length >= 2)
            {
                var intervals = new double[beats.Length - 1];
                for (int i = 0; i < intervals.Length; i++)
                    intervals[i] = beats[i + 1] - beats[i];
                Array.Sort(intervals);
                double medianBpm = 60.0 / intervals[intervals.Length / 2];
                progress?.Report($"  Detected {beats.Length} beats, estimated tempo: {medianBpm:F1} BPM (madmom)");
            }

            return beats;
        }
        catch (Exception ex)
        {
            progress?.Report($"  madmom failed: {ex.Message}, falling back to librosa");
            return null;
        }
    }

    public void Dispose()
    {
        _onnx.Dispose();
    }
}
