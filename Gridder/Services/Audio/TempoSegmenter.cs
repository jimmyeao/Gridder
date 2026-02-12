namespace Gridder.Services.Audio;

/// <summary>
/// Segment beats into regions of consistent tempo — port of tempo_segmenter.py.
/// Verification matches Serato's interpolation grid (pinned at both endpoints).
/// </summary>
public static class TempoSegmenter
{
    public record Segment(int StartBeatIndex, int EndBeatIndex, double StartPosition,
        double Bpm, int BeatCount);

    /// <summary>
    /// Build tempo segments from detected beat positions.
    /// Pipeline: build initial segments → bridge outliers → consolidate similar.
    /// </summary>
    public static Segment[] SegmentTempo(double[] beatTimes, double maxDriftMs = 15.0,
        IProgress<string>? progress = null)
    {
        if (beatTimes.Length < 2)
        {
            if (beatTimes.Length == 1)
                return [new Segment(0, 0, beatTimes[0], 120.0, 1)];
            return [];
        }

        progress?.Report($"  Segmenting {beatTimes.Length} beats (drift tolerance: {maxDriftMs}ms)...");

        var segments = BuildSegments(beatTimes, maxDriftMs);
        progress?.Report($"  Initial: {segments.Count} segments");

        segments = BridgeOutliers(segments, beatTimes, maxDriftMs, progress);
        progress?.Report($"  After bridging: {segments.Count} segments");

        segments = ConsolidateSimilar(segments, beatTimes, maxDriftMs);
        progress?.Report($"  After consolidation: {segments.Count} segments");

        // Log final segments
        progress?.Report($"  Final: {segments.Count} segment(s):");
        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            double implicitBpm = seg.Bpm;
            if (i + 1 < segments.Count)
            {
                var nxt = segments[i + 1];
                double span = nxt.StartPosition - seg.StartPosition;
                if (span > 0 && seg.BeatCount > 0)
                    implicitBpm = 60.0 * seg.BeatCount / span;
            }
            progress?.Report($"    Seg {i + 1}: {seg.BeatCount} beats, " +
                $"start={seg.StartPosition:F3}s, implicit BPM={implicitBpm:F2}");
        }

        return segments.ToArray();
    }

    private static List<Segment> BuildSegments(double[] beats, double maxDriftMs)
    {
        double maxDriftSec = maxDriftMs / 1000.0;
        var segments = new List<Segment>();
        int segStart = 0;

        while (segStart < beats.Length - 1)
        {
            int segEnd = FindSegmentEnd(beats, segStart, maxDriftSec);
            double bpm = ImplicitBpm(beats, segStart, segEnd);

            segments.Add(new Segment(
                segStart, segEnd,
                Math.Round(beats[segStart], 6),
                Math.Round(bpm, 2),
                segEnd - segStart));

            segStart = segEnd;
        }

        // Handle last beat if standalone
        if (segStart == beats.Length - 1 &&
            (segments.Count == 0 || segments[^1].EndBeatIndex < beats.Length - 1))
        {
            double bpm = segments.Count > 0 ? segments[^1].Bpm : 120.0;
            segments.Add(new Segment(segStart, segStart, Math.Round(beats[segStart], 6),
                Math.Round(bpm, 2), 1));
        }

        return segments;
    }

    private static int FindSegmentEnd(double[] beats, int start, double maxDriftSec)
    {
        int bestEnd = start + 1;

        for (int candidateEnd = start + 2; candidateEnd < beats.Length; candidateEnd++)
        {
            int n = candidateEnd - start;
            double startPos = beats[start];
            double endPos = beats[candidateEnd];
            double interval = (endPos - startPos) / n;

            // Check intermediate beats against Serato's interpolation
            double maxDrift = 0;
            for (int k = 1; k < n; k++)
            {
                double expected = startPos + k * interval;
                double actual = beats[start + k];
                double drift = Math.Abs(actual - expected);
                if (drift > maxDrift) maxDrift = drift;
            }

            if (maxDrift > maxDriftSec)
                break;

            bestEnd = candidateEnd;
        }

        return bestEnd;
    }

    private static List<Segment> BridgeOutliers(List<Segment> segments, double[] beats,
        double maxDriftMs, IProgress<string>? progress,
        int minBeats = 8, double bpmOutlierPct = 5.0)
    {
        if (segments.Count <= 1) return segments;

        int totalBeats = segments.Sum(s => s.BeatCount);
        if (totalBeats == 0) return segments;

        double weightedBpm = segments.Sum(s => s.Bpm * s.BeatCount) / totalBeats;
        double maxDriftSec = maxDriftMs / 1000.0;

        var isOutlier = segments.Select(seg =>
            seg.BeatCount < minBeats &&
            Math.Abs(seg.Bpm - weightedBpm) / weightedBpm * 100 > bpmOutlierPct
        ).ToArray();

        for (int i = 0; i < segments.Count; i++)
        {
            if (isOutlier[i])
            {
                progress?.Report($"    Outlier seg {i + 1}: {segments[i].Bpm:F1} BPM, " +
                    $"{segments[i].BeatCount} beats @ {segments[i].StartPosition:F1}s");
            }
        }

        var result = new List<Segment>();
        int idx = 0;

        while (idx < segments.Count)
        {
            if (isOutlier[idx])
            {
                int j = idx;
                while (j < segments.Count && isOutlier[j]) j++;

                var prevGood = result.Count > 0 ? result[^1] : (Segment?)null;
                var nextGood = j < segments.Count ? segments[j] : (Segment?)null;

                bool bridged = false;
                if (prevGood != null && nextGood != null)
                {
                    int bridgeStart = prevGood.StartBeatIndex;
                    int bridgeEnd = nextGood.StartBeatIndex;
                    int bridgeCount = bridgeEnd - bridgeStart;

                    if (bridgeCount >= 2)
                    {
                        double drift = SeratoMaxDriftGoodOnly(beats, bridgeStart, bridgeEnd,
                            segments[idx].StartBeatIndex, segments[j - 1].EndBeatIndex);

                        if (drift <= maxDriftSec * 2)
                        {
                            double newBpm = ImplicitBpm(beats, bridgeStart, bridgeEnd);
                            result[^1] = new Segment(bridgeStart, bridgeEnd,
                                prevGood.StartPosition, Math.Round(newBpm, 2), bridgeCount);
                            bridged = true;
                            progress?.Report($"    Bridged over {j - idx} outlier seg(s), " +
                                $"drift={drift * 1000:F1}ms");
                        }
                    }
                }

                if (!bridged)
                {
                    if (j - idx > 1)
                    {
                        int combStart = segments[idx].StartBeatIndex;
                        int combEnd = segments[j - 1].EndBeatIndex;
                        double combBpm = ImplicitBpm(beats, combStart, combEnd);
                        result.Add(new Segment(combStart, combEnd,
                            segments[idx].StartPosition, Math.Round(combBpm, 2),
                            combEnd - combStart));
                    }
                    else
                    {
                        result.Add(segments[idx]);
                    }
                }

                idx = j;
            }
            else
            {
                result.Add(segments[idx]);
                idx++;
            }
        }

        return result;
    }

    private static List<Segment> ConsolidateSimilar(List<Segment> segments, double[] beats,
        double maxDriftMs)
    {
        if (segments.Count <= 1) return segments;

        double maxDriftSec = maxDriftMs / 1000.0;
        bool merged = true;

        while (merged)
        {
            merged = false;
            var result = new List<Segment> { segments[0] };

            for (int i = 1; i < segments.Count; i++)
            {
                var prev = result[^1];
                var curr = segments[i];

                int combinedStart = prev.StartBeatIndex;
                int combinedEnd = curr.EndBeatIndex;
                int combinedCount = combinedEnd - combinedStart;

                if (combinedCount >= 2)
                {
                    double drift = SeratoMaxDrift(beats, combinedStart, combinedEnd);
                    if (drift <= maxDriftSec)
                    {
                        double combinedBpm = ImplicitBpm(beats, combinedStart, combinedEnd);
                        result[^1] = new Segment(combinedStart, combinedEnd,
                            prev.StartPosition, Math.Round(combinedBpm, 2), combinedCount);
                        merged = true;
                        continue;
                    }
                }

                result.Add(curr);
            }

            segments = result;
        }

        return segments;
    }

    private static double SeratoMaxDrift(double[] beats, int startIdx, int endIdx)
    {
        int n = endIdx - startIdx;
        if (n < 2) return 0;

        double startPos = beats[startIdx];
        double endPos = beats[endIdx];
        double interval = (endPos - startPos) / n;

        double maxDrift = 0;
        for (int k = 1; k < n; k++)
        {
            double expected = startPos + k * interval;
            double actual = beats[startIdx + k];
            double drift = Math.Abs(actual - expected);
            if (drift > maxDrift) maxDrift = drift;
        }

        return maxDrift;
    }

    private static double SeratoMaxDriftGoodOnly(double[] beats, int startIdx, int endIdx,
        int outlierStart, int outlierEnd)
    {
        int n = endIdx - startIdx;
        if (n < 2) return 0;

        double startPos = beats[startIdx];
        double endPos = beats[endIdx];
        double interval = (endPos - startPos) / n;

        double maxDrift = 0;
        for (int k = 1; k < n; k++)
        {
            int beatIdx = startIdx + k;
            if (beatIdx >= outlierStart && beatIdx <= outlierEnd)
                continue;

            double expected = startPos + k * interval;
            double actual = beats[beatIdx];
            double drift = Math.Abs(actual - expected);
            if (drift > maxDrift) maxDrift = drift;
        }

        return maxDrift;
    }

    private static double ImplicitBpm(double[] beats, int startIdx, int endIdx)
    {
        if (endIdx <= startIdx) return 120.0;
        double span = beats[endIdx] - beats[startIdx];
        if (span <= 0) return 120.0;
        return 60.0 * (endIdx - startIdx) / span;
    }
}
