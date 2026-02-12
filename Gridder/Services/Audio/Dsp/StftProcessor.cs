using System.Numerics;

namespace Gridder.Services.Audio.Dsp;

/// <summary>
/// Short-Time Fourier Transform and inverse using FftSharp.
/// Matches librosa's default STFT behavior (center=True, Hann window).
/// </summary>
public static class StftProcessor
{
    /// <summary>
    /// Compute the STFT of a real-valued signal.
    /// Returns Complex[nFreqBins][nFrames] where nFreqBins = nFft/2 + 1.
    /// Uses center padding and Hann windowing (matching librosa defaults).
    /// </summary>
    public static Complex[][] ComputeStft(double[] signal, int nFft = 2048, int hopLength = 512)
    {
        // Center-pad the signal (reflect padding, matching librosa)
        int pad = nFft / 2;
        var padded = new double[signal.Length + 2 * pad];
        Array.Copy(signal, 0, padded, pad, signal.Length);

        // Reflect padding at boundaries
        for (int i = 0; i < pad; i++)
        {
            int srcIdx = Math.Min(pad - i, signal.Length - 1);
            padded[i] = signal[srcIdx];
        }
        for (int i = 0; i < pad; i++)
        {
            int srcIdx = Math.Max(signal.Length - 2 - i, 0);
            padded[pad + signal.Length + i] = signal[srcIdx];
        }

        // Create Hann window
        var window = new FftSharp.Windows.Hanning().Create(nFft);

        int nFrames = 1 + (padded.Length - nFft) / hopLength;
        int nFreqBins = nFft / 2 + 1;

        // Output: [frequency][time] to match librosa's (n_freq, n_frames) shape
        var result = new Complex[nFreqBins][];
        for (int f = 0; f < nFreqBins; f++)
            result[f] = new Complex[nFrames];

        var frame = new double[nFft];

        for (int t = 0; t < nFrames; t++)
        {
            int start = t * hopLength;

            // Extract and window the frame
            for (int i = 0; i < nFft; i++)
            {
                int idx = start + i;
                frame[i] = (idx < padded.Length ? padded[idx] : 0.0) * window[i];
            }

            // FFT
            var spectrum = FftSharp.FFT.Forward(frame);

            // Store positive frequency bins
            for (int f = 0; f < nFreqBins; f++)
                result[f][t] = spectrum[f];
        }

        return result;
    }

    /// <summary>
    /// Compute the inverse STFT using overlap-add reconstruction.
    /// Input: Complex[nFreqBins][nFrames], output length can be specified.
    /// </summary>
    public static double[] ComputeIstft(Complex[][] stft, int hopLength = 512, int? length = null)
    {
        int nFreqBins = stft.Length;
        int nFrames = stft[0].Length;
        int nFft = (nFreqBins - 1) * 2;

        var window = new FftSharp.Windows.Hanning().Create(nFft);

        // Output buffer (with center padding accounted for)
        int expectedLength = nFft + (nFrames - 1) * hopLength;
        var output = new double[expectedLength];
        var windowSum = new double[expectedLength];

        var fullSpectrum = new Complex[nFft];

        for (int t = 0; t < nFrames; t++)
        {
            // Reconstruct full spectrum (conjugate mirror symmetry)
            for (int f = 0; f < nFreqBins; f++)
                fullSpectrum[f] = stft[f][t];

            for (int f = 1; f < nFreqBins - 1; f++)
                fullSpectrum[nFft - f] = Complex.Conjugate(fullSpectrum[f]);

            // Inverse FFT (in-place)
            FftSharp.FFT.Inverse(fullSpectrum);

            // Overlap-add with synthesis window
            int start = t * hopLength;
            for (int i = 0; i < nFft; i++)
            {
                int idx = start + i;
                if (idx < expectedLength)
                {
                    output[idx] += fullSpectrum[i].Real * window[i];
                    windowSum[idx] += window[i] * window[i];
                }
            }
        }

        // Normalize by window sum
        for (int i = 0; i < expectedLength; i++)
        {
            if (windowSum[i] > 1e-8)
                output[i] /= windowSum[i];
        }

        // Remove center padding and trim to requested length
        int pad = nFft / 2;
        int outputLength = length ?? Math.Max(0, expectedLength - 2 * pad);
        var result = new double[outputLength];
        int copyLen = Math.Min(outputLength, expectedLength - pad);
        if (copyLen > 0)
            Array.Copy(output, pad, result, 0, copyLen);

        return result;
    }

    /// <summary>
    /// Compute the magnitude spectrogram from a complex STFT.
    /// Returns double[nFreqBins][nFrames].
    /// </summary>
    public static double[][] Magnitude(Complex[][] stft)
    {
        int nFreq = stft.Length;
        int nFrames = stft[0].Length;
        var mag = new double[nFreq][];

        for (int f = 0; f < nFreq; f++)
        {
            mag[f] = new double[nFrames];
            for (int t = 0; t < nFrames; t++)
                mag[f][t] = stft[f][t].Magnitude;
        }

        return mag;
    }

    /// <summary>
    /// Compute the power spectrogram (magnitude squared) from a complex STFT.
    /// Returns double[nFreqBins][nFrames].
    /// </summary>
    public static double[][] Power(Complex[][] stft)
    {
        int nFreq = stft.Length;
        int nFrames = stft[0].Length;
        var pow = new double[nFreq][];

        for (int f = 0; f < nFreq; f++)
        {
            pow[f] = new double[nFrames];
            for (int t = 0; t < nFrames; t++)
            {
                double m = stft[f][t].Magnitude;
                pow[f][t] = m * m;
            }
        }

        return pow;
    }
}
