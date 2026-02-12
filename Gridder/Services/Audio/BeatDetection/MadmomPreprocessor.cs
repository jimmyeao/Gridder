using Gridder.Services.Audio.Dsp;

namespace Gridder.Services.Audio.BeatDetection;

/// <summary>
/// Feature extraction matching madmom's BLSTM beat processor input.
/// Computes multi-resolution log filterbank spectral difference features.
///
/// Pipeline per frame size (1024, 2048, 4096):
///   1. STFT with hop=441 (100fps at 44100Hz)
///   2. Log filterbank (12 bands/octave, 30-17000Hz)
///   3. Log scaling: log10(S + 1)
///   4. Spectral difference: 0.5 * S + 0.5 * max(0, S[t] - S[t-1])
///
/// Features are concatenated across frame sizes → ~330 features per frame.
/// </summary>
public static class MadmomPreprocessor
{
    private static readonly int[] FrameSizes = [1024, 2048, 4096];
    private const int Fps = 100;
    private const int BandsPerOctave = 12;
    private const double FMin = 30.0;
    private const double FMax = 17000.0;

    /// <summary>
    /// Extract features for madmom BLSTM inference.
    /// Returns float[nFrames, nFeatures] suitable for ONNX input.
    /// </summary>
    public static float[,] ExtractFeatures(double[] signal, int sr)
    {
        int hopLength = sr / Fps; // 441 for 44100Hz

        // Compute features for each frame size
        var allFeatures = new List<double[][]>();
        int minFrames = int.MaxValue;

        foreach (int frameSize in FrameSizes)
        {
            var features = ComputeStreamFeatures(signal, sr, frameSize, hopLength);
            allFeatures.Add(features);
            if (features[0].Length < minFrames)
                minFrames = features[0].Length;
        }

        // Trim all streams to same frame count and concatenate
        int totalBands = allFeatures.Sum(f => f.Length);
        var result = new float[minFrames, totalBands];

        int bandOffset = 0;
        foreach (var features in allFeatures)
        {
            int nBands = features.Length;
            for (int t = 0; t < minFrames; t++)
            {
                for (int b = 0; b < nBands; b++)
                    result[t, bandOffset + b] = (float)features[b][t];
            }
            bandOffset += nBands;
        }

        return result;
    }

    private static double[][] ComputeStreamFeatures(double[] signal, int sr,
        int frameSize, int hopLength)
    {
        // 1. STFT
        var stft = StftProcessor.ComputeStft(signal, frameSize, hopLength);

        // 2. Power spectrogram
        var power = StftProcessor.Power(stft);

        // 3. Log filterbank
        var filterbank = MelFilterbank.CreateLogFilterbank(sr, frameSize,
            BandsPerOctave, FMin, FMax, normalize: true);
        var filtered = MelFilterbank.Apply(filterbank, power);

        // 4. Log scaling: log10(S * 1 + 1)
        var logSpec = SpectrogramUtils.LogScale(filtered, mul: 1.0, add: 1.0);

        // 5. Spectral difference (diff_ratio=0.5, positive diffs only)
        var diffSpec = SpectrogramUtils.SpectralDifference(logSpec, diffRatio: 0.5);

        return diffSpec;
    }
}
