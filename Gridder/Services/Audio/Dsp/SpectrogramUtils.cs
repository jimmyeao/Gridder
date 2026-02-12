namespace Gridder.Services.Audio.Dsp;

/// <summary>
/// Spectrogram utility functions: power-to-dB, onset strength, spectral difference.
/// </summary>
public static class SpectrogramUtils
{
    /// <summary>
    /// Convert power spectrogram to decibel scale.
    /// Matches librosa.power_to_db(S, ref=1.0, amin=1e-10, top_db=80).
    /// Input/output shape: [nBands][nFrames].
    /// </summary>
    public static double[][] PowerToDb(double[][] powerSpec, double refValue = 1.0,
        double amin = 1e-10, double topDb = 80.0)
    {
        int nBands = powerSpec.Length;
        int nFrames = powerSpec[0].Length;
        var db = new double[nBands][];
        double logRef = 10.0 * Math.Log10(Math.Max(amin, refValue));

        double maxVal = double.NegativeInfinity;

        for (int b = 0; b < nBands; b++)
        {
            db[b] = new double[nFrames];
            for (int t = 0; t < nFrames; t++)
            {
                db[b][t] = 10.0 * Math.Log10(Math.Max(amin, powerSpec[b][t])) - logRef;
                if (db[b][t] > maxVal) maxVal = db[b][t];
            }
        }

        // Apply top_db threshold
        if (topDb > 0 && !double.IsNegativeInfinity(maxVal))
        {
            double threshold = maxVal - topDb;
            for (int b = 0; b < nBands; b++)
                for (int t = 0; t < nFrames; t++)
                    if (db[b][t] < threshold)
                        db[b][t] = threshold;
        }

        return db;
    }

    /// <summary>
    /// Compute onset strength envelope from a spectrogram (dB scale).
    /// Matches librosa.onset.onset_strength(S=S_db, sr=sr, aggregate=np.median).
    /// Input shape: [nBands][nFrames]. Returns double[nFrames].
    /// </summary>
    public static double[] OnsetStrength(double[][] spectrogramDb, bool useMedian = true)
    {
        int nBands = spectrogramDb.Length;
        int nFrames = spectrogramDb[0].Length;

        if (nFrames < 2)
            return new double[nFrames];

        var onset = new double[nFrames];
        var bandDiffs = new double[nBands];

        for (int t = 1; t < nFrames; t++)
        {
            // Compute first-order difference, half-wave rectified
            int validBands = 0;
            for (int b = 0; b < nBands; b++)
            {
                double diff = spectrogramDb[b][t] - spectrogramDb[b][t - 1];
                bandDiffs[validBands++] = Math.Max(0, diff);
            }

            if (useMedian)
            {
                // Median aggregation across bands
                var sorted = new double[validBands];
                Array.Copy(bandDiffs, sorted, validBands);
                Array.Sort(sorted);
                onset[t] = sorted[validBands / 2];
            }
            else
            {
                // Mean aggregation
                double sum = 0;
                for (int b = 0; b < validBands; b++)
                    sum += bandDiffs[b];
                onset[t] = sum / validBands;
            }
        }

        return onset;
    }

    /// <summary>
    /// Compute onset strength from raw audio signal using STFT + mel + power_to_db.
    /// This is a convenience method matching librosa.onset.onset_strength(y=y, sr=sr).
    /// </summary>
    public static double[] OnsetStrengthFromAudio(double[] signal, int sr,
        int nFft = 2048, int hopLength = 512, int nMels = 128, bool useMedian = true)
    {
        var stft = StftProcessor.ComputeStft(signal, nFft, hopLength);
        var power = StftProcessor.Power(stft);
        var melFb = MelFilterbank.CreateMelFilterbank(sr, nFft, nMels);
        var melSpec = MelFilterbank.Apply(melFb, power);
        var melDb = PowerToDb(melSpec);
        return OnsetStrength(melDb, useMedian);
    }

    /// <summary>
    /// Madmom-style spectral difference: positive half-wave rectified first-order diff
    /// mixed with original spectrogram.
    /// diff_ratio=0.5: output = 0.5 * spec + 0.5 * max(0, spec[t] - spec[t-1])
    /// Input/output shape: [nBands][nFrames].
    /// </summary>
    public static double[][] SpectralDifference(double[][] spectrogram, double diffRatio = 0.5)
    {
        int nBands = spectrogram.Length;
        int nFrames = spectrogram[0].Length;

        var result = new double[nBands][];
        for (int b = 0; b < nBands; b++)
        {
            result[b] = new double[nFrames];
            result[b][0] = spectrogram[b][0] * (1.0 - diffRatio);

            for (int t = 1; t < nFrames; t++)
            {
                double diff = Math.Max(0, spectrogram[b][t] - spectrogram[b][t - 1]);
                result[b][t] = (1.0 - diffRatio) * spectrogram[b][t] + diffRatio * diff;
            }
        }

        return result;
    }

    /// <summary>
    /// Apply logarithmic scaling: log10(S * mul + add).
    /// Matches madmom's LogarithmicSpectrogramProcessor(mul=1, add=1).
    /// </summary>
    public static double[][] LogScale(double[][] spectrogram, double mul = 1.0, double add = 1.0)
    {
        int nBands = spectrogram.Length;
        int nFrames = spectrogram[0].Length;

        var result = new double[nBands][];
        for (int b = 0; b < nBands; b++)
        {
            result[b] = new double[nFrames];
            for (int t = 0; t < nFrames; t++)
                result[b][t] = Math.Log10(spectrogram[b][t] * mul + add);
        }

        return result;
    }
}
