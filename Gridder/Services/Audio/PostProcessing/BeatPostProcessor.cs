using Gridder.Models;
using Gridder.Services.Audio.Dsp;

namespace Gridder.Services.Audio.PostProcessing;

/// <summary>
/// Post-processing pipeline for detected beats — port of __main__.py steps 2a-2f.
/// Handles: first beat override, weak beat trimming, extrapolation,
/// false detection removal, grid snapping, and Serato offset calibration.
/// </summary>
public class BeatPostProcessor
{
    private const int Mpeg1DecoderDelay = 529;

    /// <summary>
    /// Result of post-processing: cleaned beat times and pre-snap beats for calibration.
    /// </summary>
    public record PostProcessResult(double[] Beats, double[] PreSnapBeats);

    /// <summary>
    /// Run all post-processing steps on detected beat times.
    /// </summary>
    public PostProcessResult Process(
        double[] beatTimes,
        float[] audioSamples,
        int sr,
        string filePath,
        double? firstBeatOverride,
        ISeratoTagService seratoTagService,
        IProgress<string>? progress = null)
    {
        var beats = (double[])beatTimes.Clone();

        // Step 2a: First beat override
        if (firstBeatOverride.HasValue)
        {
            beats = ApplyFirstBeatOverride(beats, firstBeatOverride.Value, progress);
            // Skip 2b and 2c when user specifies first beat
        }
        else
        {
            // Step 2b: Trim weak leading beats
            beats = TrimWeakLeadingBeats(beats, audioSamples, sr, progress);

            // Step 2c: Extrapolate first beats
            beats = ExtrapolateFirstBeats(beats, audioSamples, sr, progress);
        }

        // Step 2d: Remove false detections
        beats = RemoveFalseDetections(beats, progress);

        // Save pre-snap beats for Serato calibration
        var preSnapBeats = (double[])beats.Clone();

        // Step 2e: Snap to grid if constant tempo
        beats = SnapToGrid(beats, progress);

        // Step 2f: Apply Serato position offset
        beats = ApplySeratoOffset(beats, preSnapBeats, audioSamples, sr, filePath,
            seratoTagService, progress);

        return new PostProcessResult(beats, preSnapBeats);
    }

    private static double[] ApplyFirstBeatOverride(double[] beats, double firstBeat,
        IProgress<string>? progress)
    {
        if (beats.Length == 0)
            return [firstBeat];

        // Find nearest detected beat within 50ms
        double nearestDist = double.MaxValue;
        int nearestIdx = 0;
        for (int i = 0; i < beats.Length; i++)
        {
            double dist = Math.Abs(beats[i] - firstBeat);
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearestIdx = i;
            }
        }

        if (nearestDist <= 0.050)
        {
            progress?.Report($"  First beat override: {firstBeat:F3}s " +
                $"(nearest detected: {beats[nearestIdx]:F3}s, dist={nearestDist * 1000:F1}ms)");
            beats = beats[nearestIdx..];
        }
        else
        {
            progress?.Report($"  First beat override: {firstBeat:F3}s " +
                $"(inserted, nearest was {beats[nearestIdx]:F3}s at {nearestDist * 1000:F1}ms)");
            beats = beats.Where(b => b >= firstBeat).Prepend(firstBeat).ToArray();
        }

        progress?.Report("  Skipping weak-beat trimming and extrapolation (user override)");
        return beats;
    }

    private static double[] TrimWeakLeadingBeats(double[] beats, float[] audio, int sr,
        IProgress<string>? progress)
    {
        if (beats.Length < 4) return beats;

        int window = (int)(0.03 * sr); // 30ms window
        var beatRms = new double[beats.Length];
        for (int i = 0; i < beats.Length; i++)
        {
            int center = (int)(beats[i] * sr);
            int start = Math.Max(0, center - window);
            int end = Math.Min(audio.Length, center + window);
            double sumSq = 0;
            int count = 0;
            for (int j = start; j < end; j++)
            {
                sumSq += audio[j] * audio[j];
                count++;
            }
            beatRms[i] = count > 0 ? Math.Sqrt(sumSq / count) : 0;
        }

        // Median of upper half
        var sorted = (double[])beatRms.Clone();
        Array.Sort(sorted);
        var upperHalf = sorted[(sorted.Length / 2)..];
        double strongBeatRms = upperHalf[upperHalf.Length / 2]; // median of upper half
        double threshold = strongBeatRms * 0.20;

        int firstStrong = 0;
        for (int i = 0; i < beatRms.Length; i++)
        {
            if (beatRms[i] >= threshold)
            {
                firstStrong = i;
                break;
            }
        }

        if (firstStrong > 0)
        {
            progress?.Report($"  Trimmed {firstStrong} weak intro beat(s) " +
                $"(energy < {threshold:F4}, threshold={strongBeatRms:F4})");
            beats = beats[firstStrong..];
        }

        return beats;
    }

    private static double[] ExtrapolateFirstBeats(double[] beats, float[] audio, int sr,
        IProgress<string>? progress)
    {
        if (beats.Length < 2) return beats;

        int nCheck = Math.Min(8, beats.Length - 1);
        var intervals = new double[nCheck];
        for (int i = 0; i < nCheck; i++)
            intervals[i] = beats[i + 1] - beats[i];
        Array.Sort(intervals);
        double medianInterval = intervals[nCheck / 2];

        // RMS threshold from first detected beat
        int extWindow = (int)(0.03 * sr);
        int firstSample = (int)(beats[0] * sr);
        int start = Math.Max(0, firstSample - extWindow);
        int end = Math.Min(audio.Length, firstSample + extWindow);
        double sumSq = 0;
        int count = 0;
        for (int j = start; j < end; j++) { sumSq += audio[j] * audio[j]; count++; }
        double firstBeatRms = count > 0 ? Math.Sqrt(sumSq / count) : 0;
        double extrapThreshold = firstBeatRms * 0.25;

        double originalFirst = beats[0];
        var beatList = new List<double>(beats);
        int prepended = 0;

        while (true)
        {
            double extrapolated = beatList[0] - medianInterval;
            if (extrapolated < 0) break;

            int sampleIdx = (int)(extrapolated * sr);
            if (sampleIdx >= audio.Length) break;

            int rStart = Math.Max(0, sampleIdx - extWindow);
            int rEnd = Math.Min(audio.Length, sampleIdx + extWindow);
            double rSumSq = 0;
            int rCount = 0;
            for (int j = rStart; j < rEnd; j++) { rSumSq += audio[j] * audio[j]; rCount++; }
            double rms = rCount > 0 ? Math.Sqrt(rSumSq / rCount) : 0;

            if (rms < extrapThreshold) break;

            beatList.Insert(0, extrapolated);
            prepended++;
        }

        if (prepended > 0)
        {
            progress?.Report($"  Prepended {prepended} beat(s): first beat " +
                $"{originalFirst:F3}s -> {beatList[0]:F3}s");
        }

        return beatList.ToArray();
    }

    private static double[] RemoveFalseDetections(double[] beats, IProgress<string>? progress)
    {
        if (beats.Length < 4) return beats;

        var intervals = new double[beats.Length - 1];
        for (int i = 0; i < intervals.Length; i++)
            intervals[i] = beats[i + 1] - beats[i];
        var sortedIntervals = (double[])intervals.Clone();
        Array.Sort(sortedIntervals);
        double medianInterval = sortedIntervals[sortedIntervals.Length / 2];
        double tolerance = 0.20;

        var beatList = new List<double>(beats);
        int removed = 0;
        int idx = 1;

        while (idx < beatList.Count - 1)
        {
            double before = beatList[idx] - beatList[idx - 1];
            double after = beatList[idx + 1] - beatList[idx];
            double combined = beatList[idx + 1] - beatList[idx - 1];

            bool beforeOk = Math.Abs(before - medianInterval) / medianInterval <= tolerance;
            bool afterOk = Math.Abs(after - medianInterval) / medianInterval <= tolerance;
            bool combinedOk = Math.Abs(combined - medianInterval) / medianInterval <= tolerance;

            if (!(beforeOk && afterOk) && combinedOk)
            {
                beatList.RemoveAt(idx);
                removed++;
            }
            else
            {
                // Double-hit detection
                bool combined2xOk = Math.Abs(combined - 2 * medianInterval) / medianInterval <= tolerance;
                bool bothShort = before < medianInterval * (1 - tolerance) &&
                                 after < medianInterval * (1 - tolerance);
                if (bothShort && combined2xOk)
                {
                    beatList.RemoveAt(idx);
                    removed++;
                }
                else
                {
                    idx++;
                }
            }
        }

        if (removed > 0)
        {
            progress?.Report($"  Removed {removed} false beat detection(s) " +
                $"(interval tolerance: +/-{tolerance * 100:F0}% of {medianInterval * 1000:F1}ms median)");
        }

        return beatList.ToArray();
    }

    private static double[] SnapToGrid(double[] beats, IProgress<string>? progress)
    {
        if (beats.Length < 8) return beats;

        var intervals = new double[beats.Length - 1];
        for (int i = 0; i < intervals.Length; i++)
            intervals[i] = beats[i + 1] - beats[i];

        var sorted = (double[])intervals.Clone();
        Array.Sort(sorted);
        double q25 = sorted[(int)(sorted.Length * 0.25)];
        double q75 = sorted[(int)(sorted.Length * 0.75)];
        double medianInterval = sorted[sorted.Length / 2];
        double iqrRatio = medianInterval > 0 ? (q75 - q25) / medianInterval : 1.0;

        if (iqrRatio >= 0.03) return beats; // Not constant tempo

        // Estimate best interval using candidate beat counts
        double totalSpan = beats[^1] - beats[0];
        int baseCount = (int)Math.Round(totalSpan / medianInterval);

        double bestEst = baseCount > 0 ? totalSpan / baseCount : medianInterval;
        int bestGood = 0;

        for (int delta = -5; delta <= 5; delta++)
        {
            int c = baseCount + delta;
            if (c <= 0) continue;
            double trial = totalSpan / c;

            int nGood = 0;
            double firstBeat = beats[0];
            for (int i = 0; i < beats.Length; i++)
            {
                double offset = (beats[i] - firstBeat) % trial;
                double modDist = Math.Min(offset, trial - offset);
                if (modDist < 0.030) nGood++;
            }

            if (nGood > bestGood)
            {
                bestGood = nGood;
                bestEst = trial;
            }
        }

        double estInterval = bestEst;

        // Cumulative beat numbering
        var beatNumbers = new int[beats.Length];
        for (int i = 1; i < beats.Length; i++)
        {
            double gap = beats[i] - beats[i - 1];
            beatNumbers[i] = beatNumbers[i - 1] + Math.Max(1, (int)Math.Round(gap / estInterval));
        }

        // Remove duplicates
        var uniqueIndices = new List<int> { 0 };
        for (int i = 1; i < beatNumbers.Length; i++)
        {
            if (beatNumbers[i] != beatNumbers[uniqueIndices[^1]])
                uniqueIndices.Add(i);
        }

        var cleanBeats = uniqueIndices.Select(i => beats[i]).ToArray();
        var cleanNumbers = uniqueIndices.Select(i => beatNumbers[i]).ToArray();
        int nDupes = beats.Length - cleanBeats.Length;

        // Seed regression
        double slope = estInterval;
        double intercept = Median(cleanBeats.Zip(cleanNumbers,
            (b, n) => b - (double)n * slope).ToArray());

        // Iterative regression
        bool[] goodMask = new bool[cleanBeats.Length];
        for (int i = 0; i < cleanBeats.Length; i++)
        {
            double fitted = intercept + cleanNumbers[i] * slope;
            goodMask[i] = Math.Abs(cleanBeats[i] - fitted) < 0.030;
        }

        progress?.Report($"  Grid snap seed: interval={slope:F6}s ({60 / slope:F2} BPM), " +
            $"intercept={intercept:F4}, initial good={goodMask.Count(g => g)}/{cleanBeats.Length}");

        for (int iter = 0; iter < 5; iter++)
        {
            var goodIdx = Enumerable.Range(0, cleanBeats.Length).Where(i => goodMask[i]).ToArray();
            if (goodIdx.Length < 4) break;

            // Linear regression: polyfit degree 1
            (slope, intercept) = LinearFit(
                goodIdx.Select(i => (double)cleanNumbers[i]).ToArray(),
                goodIdx.Select(i => cleanBeats[i]).ToArray());

            if (slope <= 0) break;

            var newMask = new bool[cleanBeats.Length];
            for (int i = 0; i < cleanBeats.Length; i++)
            {
                double fitted = intercept + cleanNumbers[i] * slope;
                newMask[i] = Math.Abs(cleanBeats[i] - fitted) < 0.030;
            }

            progress?.Report($"  Grid snap iter {iter}: slope={slope:F6} ({60 / slope:F2} BPM), " +
                $"good={newMask.Count(g => g)}/{cleanBeats.Length}");

            if (newMask.SequenceEqual(goodMask)) break;
            goodMask = newMask;
        }

        int nGoodFinal = goodMask.Count(g => g);
        if (nGoodFinal >= cleanBeats.Length * 0.80 && slope > 0)
        {
            // Generate perfect grid
            var goodNumbers = Enumerable.Range(0, cleanBeats.Length)
                .Where(i => goodMask[i]).Select(i => cleanNumbers[i]).ToArray();
            int firstNum = goodNumbers[0];
            int lastNum = goodNumbers[^1];

            var grid = new List<double>();
            for (int n = firstNum; n <= lastNum; n++)
            {
                double pos = intercept + n * slope;
                if (pos >= 0) grid.Add(pos);
            }

            double gridBpm = 60.0 / slope;
            progress?.Report($"  Snapped {grid.Count} beats to {gridBpm:F2} BPM grid " +
                $"(was {beats.Length} beats, {nDupes} dupes, " +
                $"{beats.Length - nDupes - nGoodFinal} outliers, IQR ratio: {iqrRatio:F4})");

            return grid.ToArray();
        }

        progress?.Report($"  Grid snap: only {nGoodFinal}/{cleanBeats.Length} fit " +
            $"(IQR={iqrRatio:F4}), deferring to segmenter");
        return beats;
    }

    private static double[] ApplySeratoOffset(double[] beats, double[] preSnapBeats,
        float[] audio, int sr, string filePath, ISeratoTagService seratoTagService,
        IProgress<string>? progress)
    {
        // Try calibration against existing Serato BeatGrid
        double? calibratedOffset = null;
        double duration = (double)audio.Length / sr;

        try
        {
            var existingGrid = seratoTagService.ReadBeatGrid(filePath);
            if (existingGrid != null && existingGrid.Markers.Count >= 1)
            {
                existingGrid.ExpandBeats(duration);
                var seratoBeats = existingGrid.AllBeatPositions;

                if (seratoBeats.Count >= 4)
                {
                    // Method 1: Direct matching — find pairs within 100ms
                    var offsets = new List<double>();
                    int checkCount = Math.Min(100, preSnapBeats.Length);

                    for (int i = 0; i < checkCount; i++)
                    {
                        double ourBeat = preSnapBeats[i];
                        double minDist = double.MaxValue;
                        int minIdx = 0;
                        for (int j = 0; j < seratoBeats.Count; j++)
                        {
                            double dist = Math.Abs(seratoBeats[j] - ourBeat);
                            if (dist < minDist) { minDist = dist; minIdx = j; }
                        }
                        if (minDist < 0.100)
                            offsets.Add(seratoBeats[minIdx] - ourBeat);
                    }

                    // Method 2: Grid-point matching for single-marker grids
                    if (offsets.Count < 10 && existingGrid.Markers.Count == 1)
                    {
                        var marker = existingGrid.Markers[0];
                        if (marker.IsTerminal && marker.Bpm.HasValue && marker.Bpm.Value > 0)
                        {
                            double seratoStart = marker.PositionSeconds;
                            double seratoInterval = 60.0 / marker.Bpm.Value;
                            var gridOffsets = new List<double>();

                            for (int i = 0; i < checkCount; i++)
                            {
                                double ourBeat = preSnapBeats[i];
                                double k = Math.Round((ourBeat - seratoStart) / seratoInterval);
                                double nearestGrid = seratoStart + k * seratoInterval;
                                double off = nearestGrid - ourBeat;
                                if (Math.Abs(off) < seratoInterval * 0.25)
                                    gridOffsets.Add(off);
                            }

                            if (gridOffsets.Count >= 10)
                            {
                                offsets = gridOffsets;
                                progress?.Report($"  Serato calibration: used grid-point matching " +
                                    $"(single-marker, {marker.Bpm.Value:F1} BPM)");
                            }
                        }
                    }

                    if (offsets.Count >= 10)
                    {
                        offsets.Sort();
                        calibratedOffset = offsets[offsets.Count / 2]; // median

                        if (calibratedOffset < -0.010 || calibratedOffset > 0.060)
                        {
                            progress?.Report($"  Serato calibration rejected: offset={calibratedOffset * 1000:F1}ms " +
                                "out of range [-10, 60]ms, using computed offset");
                            calibratedOffset = null;
                        }
                        else
                        {
                            var iqr = offsets[(int)(offsets.Count * 0.75)] - offsets[(int)(offsets.Count * 0.25)];
                            progress?.Report($"  Serato calibration: {offsets.Count} matched beats, " +
                                $"offset={calibratedOffset * 1000:F1}ms (IQR={iqr * 1000:F1}ms)");
                        }
                    }
                    else
                    {
                        progress?.Report($"  Serato calibration: only {offsets.Count} matched beats " +
                            "(need 10+), using computed offset");
                    }
                }
            }
        }
        catch
        {
            // Serato tag reading failed, use computed offset
        }

        double totalOffset;
        if (calibratedOffset.HasValue)
        {
            totalOffset = calibratedOffset.Value;
            progress?.Report($"  Serato offset: +{totalOffset * 1000:F1}ms [calibrated from existing grid]");
        }
        else
        {
            // Compute from codec delay + onset-to-peak
            int encoderDelay = Mp3FrameParser.GetEncoderDelay(filePath);
            double codecDelay = encoderDelay > 0 ? (double)(encoderDelay + Mpeg1DecoderDelay) / sr : 0;

            // Onset-to-peak measurement
            double onsetToPeak = 0.005; // 5ms fallback
            if (beats.Length >= 4)
            {
                int peakWindow = (int)(0.025 * sr);
                var peakOffsets = new List<double>();

                foreach (double bt in beats)
                {
                    int s = (int)(bt * sr);
                    int e = Math.Min(s + peakWindow, audio.Length);
                    if (e - s < 10) continue;

                    double maxVal = 0;
                    int peakIdx = 0;
                    for (int j = s; j < e; j++)
                    {
                        double absVal = Math.Abs(audio[j]);
                        if (absVal > maxVal) { maxVal = absVal; peakIdx = j - s; }
                    }
                    peakOffsets.Add((double)peakIdx / sr);
                }

                if (peakOffsets.Count > 0)
                {
                    peakOffsets.Sort();
                    onsetToPeak = peakOffsets[peakOffsets.Count / 2];
                    onsetToPeak = Math.Max(0.001, Math.Min(0.020, onsetToPeak));
                }
            }

            totalOffset = codecDelay + onsetToPeak;
            progress?.Report($"  Serato offset: +{totalOffset * 1000:F1}ms total " +
                $"(codec delay {codecDelay * 1000:F1}ms" +
                (encoderDelay > 0 ? $" [{encoderDelay}+{Mpeg1DecoderDelay} samples]" : "") +
                $" + onset-to-peak {onsetToPeak * 1000:F1}ms [measured])" +
                " [no existing Serato grid]");
        }

        // Apply offset
        var result = new double[beats.Length];
        for (int i = 0; i < beats.Length; i++)
            result[i] = beats[i] + totalOffset;

        return result;
    }

    private static double Median(double[] values)
    {
        if (values.Length == 0) return 0;
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    /// <summary>Least-squares linear fit: y = slope * x + intercept.</summary>
    private static (double Slope, double Intercept) LinearFit(double[] x, double[] y)
    {
        int n = x.Length;
        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
        for (int i = 0; i < n; i++)
        {
            sumX += x[i];
            sumY += y[i];
            sumXY += x[i] * y[i];
            sumX2 += x[i] * x[i];
        }
        double denom = n * sumX2 - sumX * sumX;
        if (Math.Abs(denom) < 1e-12)
            return (0, sumY / n);

        double slope = (n * sumXY - sumX * sumY) / denom;
        double intercept = (sumY - slope * sumX) / n;
        return (slope, intercept);
    }
}
