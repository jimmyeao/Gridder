namespace Gridder.Services.Audio.Dsp;

/// <summary>
/// Mel and logarithmic filterbank construction and application.
/// Supports both standard mel filterbanks (for librosa path) and
/// log-spaced filterbanks (for madmom path).
/// </summary>
public static class MelFilterbank
{
    /// <summary>Convert frequency in Hz to mel scale.</summary>
    public static double HzToMel(double hz)
        => 2595.0 * Math.Log10(1.0 + hz / 700.0);

    /// <summary>Convert mel scale value to Hz.</summary>
    public static double MelToHz(double mel)
        => 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);

    /// <summary>
    /// Create a mel filterbank matrix of shape [nMels][nFft/2+1].
    /// Each row is a triangular filter in the mel frequency domain.
    /// Matches librosa.filters.mel() behavior.
    /// </summary>
    public static double[][] CreateMelFilterbank(int sr, int nFft, int nMels = 128,
        double fMin = 0.0, double fMax = 0.0)
    {
        if (fMax <= 0) fMax = sr / 2.0;

        int nFreqBins = nFft / 2 + 1;
        double[] fftFreqs = new double[nFreqBins];
        for (int i = 0; i < nFreqBins; i++)
            fftFreqs[i] = (double)i * sr / nFft;

        // Mel points: nMels + 2 points (including edges)
        double melMin = HzToMel(fMin);
        double melMax = HzToMel(fMax);
        var melPoints = new double[nMels + 2];
        for (int i = 0; i < nMels + 2; i++)
            melPoints[i] = MelToHz(melMin + (melMax - melMin) * i / (nMels + 1));

        // Create triangular filters
        var filterbank = new double[nMels][];
        for (int m = 0; m < nMels; m++)
        {
            filterbank[m] = new double[nFreqBins];
            double lower = melPoints[m];
            double center = melPoints[m + 1];
            double upper = melPoints[m + 2];

            for (int f = 0; f < nFreqBins; f++)
            {
                double freq = fftFreqs[f];
                if (freq >= lower && freq <= center && center > lower)
                    filterbank[m][f] = (freq - lower) / (center - lower);
                else if (freq > center && freq <= upper && upper > center)
                    filterbank[m][f] = (upper - freq) / (upper - center);
            }

            // Normalize (slaney normalization, matching librosa default)
            double enorm = 2.0 / (melPoints[m + 2] - melPoints[m]);
            for (int f = 0; f < nFreqBins; f++)
                filterbank[m][f] *= enorm;
        }

        return filterbank;
    }

    /// <summary>
    /// Create a logarithmic filterbank for madmom's feature extraction.
    /// Bands are spaced at bandsPerOctave intervals from fMin to fMax.
    /// Returns double[nBands][nFft/2+1].
    /// </summary>
    public static double[][] CreateLogFilterbank(int sr, int nFft, int bandsPerOctave = 12,
        double fMin = 30.0, double fMax = 17000.0, bool normalize = true)
    {
        if (fMax > sr / 2.0) fMax = sr / 2.0;

        int nFreqBins = nFft / 2 + 1;
        double[] fftFreqs = new double[nFreqBins];
        for (int i = 0; i < nFreqBins; i++)
            fftFreqs[i] = (double)i * sr / nFft;

        // Compute number of bands
        double nOctaves = Math.Log2(fMax / fMin);
        int nBands = (int)Math.Round(bandsPerOctave * nOctaves);
        if (nBands < 1) nBands = 1;

        // Generate center frequencies (log-spaced)
        var centers = new double[nBands + 2];
        for (int i = 0; i < nBands + 2; i++)
            centers[i] = fMin * Math.Pow(2.0, (double)i / bandsPerOctave);

        // Create triangular filters
        var filterbank = new double[nBands][];
        for (int b = 0; b < nBands; b++)
        {
            filterbank[b] = new double[nFreqBins];
            double lower = centers[b];
            double center = centers[b + 1];
            double upper = centers[b + 2];

            for (int f = 0; f < nFreqBins; f++)
            {
                double freq = fftFreqs[f];
                if (freq >= lower && freq <= center && center > lower)
                    filterbank[b][f] = (freq - lower) / (center - lower);
                else if (freq > center && freq <= upper && upper > center)
                    filterbank[b][f] = (upper - freq) / (upper - center);
            }

            if (normalize)
            {
                double sum = 0;
                for (int f = 0; f < nFreqBins; f++)
                    sum += filterbank[b][f];
                if (sum > 0)
                {
                    for (int f = 0; f < nFreqBins; f++)
                        filterbank[b][f] /= sum;
                }
            }
        }

        return filterbank;
    }

    /// <summary>
    /// Apply a filterbank matrix to a power spectrogram.
    /// filterbank shape: [nBands][nFreqBins], spectrogram shape: [nFreqBins][nFrames]
    /// Returns: [nBands][nFrames]
    /// </summary>
    public static double[][] Apply(double[][] filterbank, double[][] spectrogram)
    {
        int nBands = filterbank.Length;
        int nFreqBins = spectrogram.Length;
        int nFrames = spectrogram[0].Length;

        var result = new double[nBands][];
        for (int b = 0; b < nBands; b++)
        {
            result[b] = new double[nFrames];
            for (int t = 0; t < nFrames; t++)
            {
                double sum = 0;
                int filterLen = Math.Min(filterbank[b].Length, nFreqBins);
                for (int f = 0; f < filterLen; f++)
                    sum += filterbank[b][f] * spectrogram[f][t];
                result[b][t] = sum;
            }
        }

        return result;
    }
}
