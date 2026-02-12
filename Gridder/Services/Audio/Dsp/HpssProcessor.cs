using System.Numerics;

namespace Gridder.Services.Audio.Dsp;

/// <summary>
/// Harmonic-Percussive Source Separation (HPSS).
/// Matches librosa.decompose.hpss() behavior.
/// </summary>
public static class HpssProcessor
{
    /// <summary>
    /// Separate a complex STFT into harmonic and percussive components.
    /// Uses median filtering and soft masking with configurable margin.
    ///
    /// Input shape: Complex[nFreqBins][nFrames]
    /// Returns: (harmonicStft, percussiveStft) with same shape.
    /// </summary>
    public static (Complex[][] Harmonic, Complex[][] Percussive) Separate(
        Complex[][] stft, int kernelSize = 31, double power = 2.0, double margin = 1.0)
    {
        // Compute magnitude spectrogram
        var mag = StftProcessor.Magnitude(stft);
        int nFreq = mag.Length;
        int nFrames = mag[0].Length;

        // Median filter along time axis → harmonic mask
        var harmMedian = MedianFilter.FilterAlongTime(mag, kernelSize);

        // Median filter along frequency axis → percussive mask
        var percMedian = MedianFilter.FilterAlongFrequency(mag, kernelSize);

        // Compute soft masks
        var harmonic = new Complex[nFreq][];
        var percussive = new Complex[nFreq][];

        for (int f = 0; f < nFreq; f++)
        {
            harmonic[f] = new Complex[nFrames];
            percussive[f] = new Complex[nFrames];

            for (int t = 0; t < nFrames; t++)
            {
                double h = Math.Pow(harmMedian[f][t], power);
                double p = Math.Pow(percMedian[f][t] * margin, power);
                double total = h + p + 1e-10;

                double harmMask = h / total;
                double percMask = Math.Pow(percMedian[f][t], power)
                    / (Math.Pow(percMedian[f][t], power)
                       + Math.Pow(harmMedian[f][t] * margin, power) + 1e-10);

                harmonic[f][t] = stft[f][t] * harmMask;
                percussive[f][t] = stft[f][t] * percMask;
            }
        }

        return (harmonic, percussive);
    }
}
