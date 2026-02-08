using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Gridder.Models;

public partial class BeatGrid : ObservableObject
{
    public ObservableCollection<BeatGridMarker> Markers { get; } = new();

    [ObservableProperty]
    private List<double> _allBeatPositions = new();

    /// <summary>
    /// Expand markers into individual beat positions.
    /// </summary>
    public void ExpandBeats(double trackDurationSeconds)
    {
        var beats = new List<double>();

        if (Markers.Count == 0)
        {
            AllBeatPositions = beats;
            return;
        }

        for (int i = 0; i < Markers.Count - 1; i++)
        {
            var marker = Markers[i];
            var nextMarker = Markers[i + 1];

            if (marker.BeatsUntilNext is > 0)
            {
                double interval = (nextMarker.PositionSeconds - marker.PositionSeconds) / marker.BeatsUntilNext.Value;
                for (int b = 0; b < marker.BeatsUntilNext.Value; b++)
                {
                    beats.Add(marker.PositionSeconds + b * interval);
                }
            }
        }

        var terminal = Markers[^1];
        if (terminal.Bpm is > 0)
        {
            double interval = 60.0 / terminal.Bpm.Value;
            double pos = terminal.PositionSeconds;
            while (pos <= trackDurationSeconds)
            {
                beats.Add(pos);
                pos += interval;
            }
        }

        AllBeatPositions = beats;
    }

    /// <summary>
    /// Recompute Serato markers from a list of beat positions.
    /// Detects tempo changes and creates segment boundaries.
    /// </summary>
    public void CompressToMarkers(double bpmChangeTolerance = 1.5)
    {
        if (AllBeatPositions.Count < 2)
            return;

        var sorted = AllBeatPositions.OrderBy(b => b).ToList();
        Markers.Clear();

        var ibis = new double[sorted.Count - 1];
        for (int i = 0; i < ibis.Length; i++)
            ibis[i] = sorted[i + 1] - sorted[i];

        var bpms = ibis.Select(ibi => ibi > 0 ? 60.0 / ibi : 0).ToArray();

        var segments = new List<(int startIndex, int beatCount, double avgBpm)>();
        int segStart = 0;
        double segBpmSum = bpms[0];
        int segCount = 1;

        for (int i = 1; i < bpms.Length; i++)
        {
            double segAvg = segBpmSum / segCount;
            if (Math.Abs(bpms[i] - segAvg) > bpmChangeTolerance)
            {
                segments.Add((segStart, segCount, segAvg));
                segStart = i;
                segBpmSum = bpms[i];
                segCount = 1;
            }
            else
            {
                segBpmSum += bpms[i];
                segCount++;
            }
        }
        segments.Add((segStart, segCount, segBpmSum / segCount));

        for (int i = 0; i < segments.Count; i++)
        {
            var (startIndex, beatCount, avgBpm) = segments[i];
            bool isTerminal = i == segments.Count - 1;

            if (isTerminal)
            {
                Markers.Add(new BeatGridMarker
                {
                    PositionSeconds = sorted[startIndex],
                    Bpm = Math.Round(avgBpm, 2)
                });
            }
            else
            {
                Markers.Add(new BeatGridMarker
                {
                    PositionSeconds = sorted[startIndex],
                    BeatsUntilNext = beatCount
                });
            }
        }
    }
}
