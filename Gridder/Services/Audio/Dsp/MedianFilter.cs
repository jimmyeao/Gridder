namespace Gridder.Services.Audio.Dsp;

/// <summary>
/// 1D sliding-window median filter for HPSS and other DSP operations.
/// </summary>
public static class MedianFilter
{
    /// <summary>
    /// Apply a 1D median filter to the input array.
    /// Uses zero-padding at boundaries (same as scipy.ndimage.median_filter).
    /// </summary>
    public static double[] Apply(double[] input, int kernelSize)
    {
        if (kernelSize <= 1 || input.Length == 0)
            return (double[])input.Clone();

        int half = kernelSize / 2;
        var output = new double[input.Length];
        var window = new double[kernelSize];

        for (int i = 0; i < input.Length; i++)
        {
            int count = 0;
            for (int k = -half; k <= half; k++)
            {
                int idx = i + k;
                window[count++] = (idx >= 0 && idx < input.Length) ? input[idx] : 0.0;
            }

            Array.Sort(window, 0, count);
            output[i] = window[count / 2];
        }

        return output;
    }

    /// <summary>
    /// Apply median filter along rows (axis=1, time dimension) of a 2D array.
    /// Input shape: [frequency][time]. Filters each frequency bin independently.
    /// </summary>
    public static double[][] FilterAlongTime(double[][] spectrogram, int kernelSize)
    {
        int nFreq = spectrogram.Length;
        var result = new double[nFreq][];

        for (int f = 0; f < nFreq; f++)
            result[f] = Apply(spectrogram[f], kernelSize);

        return result;
    }

    /// <summary>
    /// Apply median filter along columns (axis=0, frequency dimension) of a 2D array.
    /// Input shape: [frequency][time]. Filters each time frame independently.
    /// </summary>
    public static double[][] FilterAlongFrequency(double[][] spectrogram, int kernelSize)
    {
        if (spectrogram.Length == 0)
            return spectrogram;

        int nFreq = spectrogram.Length;
        int nTime = spectrogram[0].Length;
        var result = new double[nFreq][];
        for (int f = 0; f < nFreq; f++)
            result[f] = new double[nTime];

        // Extract each column, filter, and put back
        var column = new double[nFreq];
        for (int t = 0; t < nTime; t++)
        {
            for (int f = 0; f < nFreq; f++)
                column[f] = spectrogram[f][t];

            var filtered = Apply(column, kernelSize);

            for (int f = 0; f < nFreq; f++)
                result[f][t] = filtered[f];
        }

        return result;
    }
}
