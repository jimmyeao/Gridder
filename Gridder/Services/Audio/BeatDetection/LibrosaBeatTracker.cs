using Gridder.Services.Audio.Dsp;

namespace Gridder.Services.Audio.BeatDetection;

/// <summary>
/// Ellis 2007 beat tracker — port of librosa.beat.beat_track with HPSS preprocessing.
/// Pipeline: HPSS → percussive iSTFT → mel spectrogram → onset strength → tempo → DP beat track.
/// </summary>
public static class LibrosaBeatTracker
{
    private const int NFft = 2048;
    private const int HopLength = 512;
    private const int NMels = 128;

    /// <summary>
    /// Detect beats using HPSS-enhanced onset detection and dynamic programming.
    /// Returns beat times in seconds.
    /// </summary>
    public static double[] DetectBeats(double[] signal, int sr)
    {
        // Step 1: HPSS to isolate percussive content
        var stft = StftProcessor.ComputeStft(signal, NFft, HopLength);
        var (_, percussive) = HpssProcessor.Separate(stft, kernelSize: 31, margin: 2.0);

        // Step 2: Convert percussive STFT back to audio
        var yPerc = StftProcessor.ComputeIstft(percussive, HopLength, signal.Length);

        // Step 3: Mel spectrogram of percussive component
        var percStft = StftProcessor.ComputeStft(yPerc, NFft, HopLength);
        var percPower = StftProcessor.Power(percStft);
        var melFb = MelFilterbank.CreateMelFilterbank(sr, NFft, NMels);
        var melSpec = MelFilterbank.Apply(melFb, percPower);
        var melDb = SpectrogramUtils.PowerToDb(melSpec);

        // Step 4: Onset strength from full percussive mel spectrogram
        var onsetEnv = SpectrogramUtils.OnsetStrength(melDb, useMedian: true);

        // Step 5: Onset strength from low bands only (kick drum)
        int lowBands = Math.Min(30, melDb.Length);
        var melDbLow = new double[lowBands][];
        Array.Copy(melDb, melDbLow, lowBands);
        var onsetEnvLow = SpectrogramUtils.OnsetStrength(melDbLow, useMedian: true);

        // Step 6: Blend onset envelopes
        int onsetLen = Math.Min(onsetEnv.Length, onsetEnvLow.Length);
        var onsetCombined = new double[onsetLen];
        for (int i = 0; i < onsetLen; i++)
            onsetCombined[i] = onsetEnv[i] + 0.5 * onsetEnvLow[i];

        // Step 7: Estimate tempo
        double tempo = EstimateTempo(onsetCombined, sr, HopLength);

        // Step 8: Dynamic programming beat tracking
        var beatFrames = BeatTrackDp(onsetCombined, tempo, sr, HopLength, tightness: 100);

        // Convert frame indices to times
        return beatFrames.Select(f => (double)f * HopLength / sr).ToArray();
    }

    /// <summary>
    /// Estimate tempo via autocorrelation of onset envelope.
    /// Matches librosa.beat.tempo() with log-normal prior centered at 120 BPM.
    /// </summary>
    private static double EstimateTempo(double[] onsetEnv, int sr, int hopLength,
        double startBpm = 120.0, double stdBpm = 1.0)
    {
        double onsetSr = (double)sr / hopLength;

        // Autocorrelation window: ~8 seconds
        int acSize = Math.Min(onsetEnv.Length, (int)(8.0 * onsetSr));
        var ac = Autocorrelate(onsetEnv, acSize);

        // Compute BPM for each lag
        var freqs = new double[ac.Length];
        for (int i = 1; i < ac.Length; i++)
            freqs[i] = onsetSr * 60.0 / i;

        // Apply log-normal tempo prior
        var weighted = new double[ac.Length];
        for (int i = 1; i < ac.Length; i++)
        {
            if (freqs[i] < 1) continue;

            double logPrior = -0.5 * Math.Pow((Math.Log(freqs[i], 2) - Math.Log(startBpm, 2)) / stdBpm, 2);
            weighted[i] = ac[i] * Math.Exp(logPrior);
        }

        // Find peak
        int bestLag = 1;
        double bestVal = double.NegativeInfinity;
        for (int i = 1; i < weighted.Length; i++)
        {
            if (weighted[i] > bestVal)
            {
                bestVal = weighted[i];
                bestLag = i;
            }
        }

        double bpm = freqs[bestLag];
        return Math.Max(40, Math.Min(240, bpm));
    }

    /// <summary>
    /// Compute autocorrelation of a signal using FFT for efficiency.
    /// Returns positive-lag autocorrelation normalized by zero-lag value.
    /// </summary>
    private static double[] Autocorrelate(double[] signal, int maxSize)
    {
        int n = signal.Length;

        // Pad to next power of 2 (for FFT)
        int nFft = 1;
        while (nFft < 2 * n) nFft <<= 1;

        var padded = new double[nFft];
        Array.Copy(signal, padded, n);

        // FFT
        var spectrum = FftSharp.FFT.Forward(padded);

        // Power spectrum
        for (int i = 0; i < spectrum.Length; i++)
        {
            double mag = spectrum[i].Magnitude;
            spectrum[i] = new System.Numerics.Complex(mag * mag, 0);
        }

        // Inverse FFT (in-place)
        FftSharp.FFT.Inverse(spectrum);

        // Extract positive lags, normalized
        int resultLen = Math.Min(maxSize + 1, n);
        var result = new double[resultLen];
        double zeroLag = spectrum[0].Real;
        if (zeroLag > 0)
        {
            for (int i = 0; i < resultLen; i++)
                result[i] = spectrum[i].Real / zeroLag;
        }

        return result;
    }

    /// <summary>
    /// Dynamic programming beat tracker (Ellis 2007).
    /// Finds optimal beat positions by maximizing onset strength
    /// while penalizing deviations from expected beat period.
    /// </summary>
    private static int[] BeatTrackDp(double[] onsetEnv, double bpm, int sr, int hopLength,
        double tightness = 100)
    {
        double onsetSr = (double)sr / hopLength;
        double period = onsetSr * 60.0 / bpm; // expected frames per beat

        int nFrames = onsetEnv.Length;
        var cumscore = new double[nFrames];
        var backlink = new int[nFrames];
        Array.Fill(backlink, -1);

        // Search window: from period/2 to 2*period frames back
        int windowStart = -(int)Math.Round(2 * period);
        int windowEnd = -(int)Math.Round(period / 2);
        int windowSize = windowEnd - windowStart + 1;

        if (windowSize <= 0)
            return [];

        // Pre-compute transition costs
        var txcost = new double[windowSize];
        for (int i = 0; i < windowSize; i++)
        {
            int offset = windowStart + i;
            double logRatio = Math.Log((double)(-offset) / period);
            txcost[i] = -tightness * logRatio * logRatio;
        }

        // Forward pass
        for (int t = 0; t < nFrames; t++)
        {
            double bestScore = double.NegativeInfinity;
            int bestPrev = -1;

            for (int i = 0; i < windowSize; i++)
            {
                int prevT = t + windowStart + i;
                if (prevT < 0) continue;
                if (prevT >= nFrames) continue;

                double score = cumscore[prevT] + txcost[i];
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPrev = prevT;
                }
            }

            if (bestPrev >= 0)
                cumscore[t] = bestScore + onsetEnv[t];
            else
                cumscore[t] = onsetEnv[t];

            backlink[t] = bestPrev;
        }

        // Backtrack from highest cumulative score
        int bestEnd = 0;
        double bestEndScore = double.NegativeInfinity;
        for (int t = 0; t < nFrames; t++)
        {
            if (cumscore[t] > bestEndScore)
            {
                bestEndScore = cumscore[t];
                bestEnd = t;
            }
        }

        var beats = new List<int> { bestEnd };
        int current = bestEnd;
        while (backlink[current] >= 0 && backlink[current] != current)
        {
            current = backlink[current];
            beats.Add(current);
        }

        beats.Reverse();

        // Trim leading beats with weak onset strength
        if (beats.Count > 0)
        {
            double median = Median(onsetEnv.Where((_, i) => beats.Contains(i)).ToArray());
            int firstStrong = 0;
            for (int i = 0; i < beats.Count; i++)
            {
                if (onsetEnv[beats[i]] >= median * 0.1)
                {
                    firstStrong = i;
                    break;
                }
            }
            if (firstStrong > 0)
                beats = beats.Skip(firstStrong).ToList();
        }

        return beats.ToArray();
    }

    private static double Median(double[] values)
    {
        if (values.Length == 0) return 0;
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }
}
