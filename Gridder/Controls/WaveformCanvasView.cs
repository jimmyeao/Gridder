using Gridder.Models;
using Gridder.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace Gridder.Controls;

public class WaveformCanvasView : SKCanvasView
{
    // Colors
    private static readonly SKColor BgColor = new(13, 13, 13);                  // #0d0d0d
    private static readonly SKColor WaveformColor = new(0, 160, 220);           // blue-cyan
    private static readonly SKColor WaveformPeakColor = new(0, 212, 255);       // bright cyan
    private static readonly SKColor BeatLineColor = new(255, 255, 255, 60);     // semi-transparent white
    private static readonly SKColor DownbeatLineColor = new(0, 212, 255, 140);  // cyan
    private static readonly SKColor SegmentLineColor = new(255, 100, 50, 200);  // orange-red
    private static readonly SKColor SelectedBeatColor = new(255, 200, 0);       // yellow
    private static readonly SKColor FirstBeatOverrideColor = new(255, 200, 0, 200); // gold
    private static readonly SKColor PlaybackCursorColor = new(255, 50, 50);     // red
    private static readonly SKColor TimeRulerColor = new(80, 80, 80);
    private static readonly SKColor TimeRulerTextColor = new(120, 120, 120);
    private static readonly SKColor CenterLineColor = new(40, 40, 50);

    private const float TimeRulerHeight = 24f;

    public WaveformEditorViewModel? ViewModel { get; set; }

    public WaveformCanvasView()
    {
        PaintSurface += OnPaintSurface;
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var info = e.Info;
        canvas.Clear(BgColor);

        if (ViewModel == null) return;

        ViewModel.ViewWidthPixels = info.Width;

        var waveformArea = new SKRect(0, TimeRulerHeight, info.Width, info.Height);

        DrawTimeRuler(canvas, info);
        DrawCenterLine(canvas, waveformArea);
        DrawWaveform(canvas, waveformArea);
        DrawBeatMarkers(canvas, waveformArea);
        DrawFirstBeatMarker(canvas, waveformArea);
        DrawPlaybackCursor(canvas, waveformArea);
    }

    private void DrawTimeRuler(SKCanvas canvas, SKImageInfo info)
    {
        using var linePaint = new SKPaint { Color = TimeRulerColor, StrokeWidth = 1, IsAntialias = true };
        using var textPaint = new SKPaint
        {
            Color = TimeRulerTextColor,
            IsAntialias = true,
        };
        using var textFont = new SKFont(SKTypeface.Default, 10);

        var vm = ViewModel!;
        var start = vm.ViewStartSeconds;
        var end = vm.ViewEndSeconds;

        // Determine tick interval based on zoom level
        double tickInterval = GetTickInterval(vm.PixelsPerSecond);

        // Round start to nearest tick
        double firstTick = Math.Ceiling(start / tickInterval) * tickInterval;

        for (double t = firstTick; t <= end; t += tickInterval)
        {
            float x = vm.TimeToX(t);
            canvas.DrawLine(x, 0, x, TimeRulerHeight, linePaint);

            // Time label
            var minutes = (int)(t / 60);
            var seconds = t % 60;
            var label = minutes > 0 ? $"{minutes}:{seconds:00.0}" : $"{seconds:0.0}s";
            canvas.DrawText(label, x + 3, TimeRulerHeight - 5, SKTextAlign.Left, textFont, textPaint);
        }

        // Bottom border line of ruler
        canvas.DrawLine(0, TimeRulerHeight, info.Width, TimeRulerHeight, linePaint);
    }

    private static double GetTickInterval(double pixelsPerSecond)
    {
        // Choose tick interval so ticks are ~80-150px apart
        double[] intervals = [0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60];
        foreach (var interval in intervals)
        {
            if (interval * pixelsPerSecond >= 80)
                return interval;
        }
        return 60;
    }

    private void DrawCenterLine(SKCanvas canvas, SKRect area)
    {
        float centerY = area.MidY;
        using var paint = new SKPaint { Color = CenterLineColor, StrokeWidth = 1 };
        canvas.DrawLine(area.Left, centerY, area.Right, centerY, paint);
    }

    private void DrawWaveform(SKCanvas canvas, SKRect area)
    {
        var waveform = ViewModel?.WaveformData;
        if (waveform == null || waveform.PixelCount == 0) return;

        var vm = ViewModel!;
        float centerY = area.MidY;
        float halfHeight = area.Height / 2 * 0.85f; // leave some margin

        using var fillPaint = new SKPaint
        {
            Color = WaveformColor,
            Style = SKPaintStyle.Fill,
            IsAntialias = false,
        };

        using var peakPaint = new SKPaint
        {
            Color = WaveformPeakColor,
            Style = SKPaintStyle.Fill,
            IsAntialias = false,
        };

        // Determine visible range in waveform pixel indices
        int startPixel = Math.Max(0, waveform.TimeToPixel(vm.ViewStartSeconds));
        int endPixel = Math.Min(waveform.PixelCount - 1, waveform.TimeToPixel(vm.ViewEndSeconds));

        for (int i = startPixel; i <= endPixel; i++)
        {
            double time = waveform.PixelToTime(i);
            float x = vm.TimeToX(time);

            float posY = waveform.PeaksPositive[i] * halfHeight;
            float negY = waveform.PeaksNegative[i] * halfHeight;

            // Use brighter color for louder parts
            var paint = Math.Abs(waveform.PeaksPositive[i]) > 0.7f ? peakPaint : fillPaint;

            // Draw vertical bar from negative peak to positive peak
            canvas.DrawLine(x, centerY - posY, x, centerY - negY, paint);
        }
    }

    private void DrawBeatMarkers(SKCanvas canvas, SKRect area)
    {
        var beatGrid = ViewModel?.BeatGrid;
        if (beatGrid == null) return;

        var vm = ViewModel!;

        using var beatPaint = new SKPaint { Color = BeatLineColor, StrokeWidth = 1, IsAntialias = true };
        using var downbeatPaint = new SKPaint { Color = DownbeatLineColor, StrokeWidth = 1.5f, IsAntialias = true };
        using var segmentPaint = new SKPaint { Color = SegmentLineColor, StrokeWidth = 2.5f, IsAntialias = true };
        using var selectedPaint = new SKPaint { Color = SelectedBeatColor, StrokeWidth = 2, IsAntialias = true };

        // Collect segment marker positions for segment-boundary highlighting
        var segmentPositions = new HashSet<double>(
            beatGrid.Markers.Where(m => !m.IsTerminal).Select(m => m.PositionSeconds));
        // Also include terminal marker position
        if (beatGrid.Markers.Count > 0)
            segmentPositions.Add(beatGrid.Markers[^1].PositionSeconds);

        // Draw individual beat lines
        for (int i = 0; i < beatGrid.AllBeatPositions.Count; i++)
        {
            double beatTime = beatGrid.AllBeatPositions[i];
            if (beatTime < vm.ViewStartSeconds || beatTime > vm.ViewEndSeconds)
                continue;

            float x = vm.TimeToX(beatTime);
            bool isDownbeat = i % 4 == 0;

            // Check if this beat is at a segment boundary
            bool isSegmentBoundary = segmentPositions.Any(sp => Math.Abs(sp - beatTime) < 0.05);

            SKPaint paint;
            if (i == vm.SelectedBeatIndex)
                paint = selectedPaint;
            else if (isSegmentBoundary)
                paint = segmentPaint;
            else if (isDownbeat)
                paint = downbeatPaint;
            else
                paint = beatPaint;

            canvas.DrawLine(x, area.Top, x, area.Bottom, paint);
        }
    }

    private void DrawFirstBeatMarker(SKCanvas canvas, SKRect area)
    {
        var overridePos = ViewModel?.Track?.FirstBeatOverride;
        if (overridePos == null || ViewModel == null) return;

        double time = overridePos.Value;
        if (time < ViewModel.ViewStartSeconds || time > ViewModel.ViewEndSeconds) return;

        float x = ViewModel.TimeToX(time);

        using var paint = new SKPaint
        {
            Color = FirstBeatOverrideColor,
            StrokeWidth = 2,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash([6, 4], 0),
        };

        canvas.DrawLine(x, area.Top, x, area.Bottom, paint);

        // Draw "1" label at top
        using var labelPaint = new SKPaint
        {
            Color = FirstBeatOverrideColor,
            IsAntialias = true,
        };
        using var labelFont = new SKFont(SKTypeface.Default, 12) { Embolden = true };
        canvas.DrawText("1", x + 3, area.Top + 14, SKTextAlign.Left, labelFont, labelPaint);
    }

    private void DrawPlaybackCursor(SKCanvas canvas, SKRect area)
    {
        var vm = ViewModel!;
        if (vm.PlaybackPositionSeconds <= 0) return;

        float x = vm.TimeToX(vm.PlaybackPositionSeconds);
        if (x < area.Left || x > area.Right) return;

        using var paint = new SKPaint
        {
            Color = PlaybackCursorColor,
            StrokeWidth = 2,
            IsAntialias = true,
        };

        canvas.DrawLine(x, area.Top, x, area.Bottom, paint);
    }

    /// <summary>
    /// Hit-test: find the beat index nearest to the given X coordinate.
    /// Returns -1 if no beat is within the hit threshold.
    /// </summary>
    public int HitTestBeat(float x, float hitThresholdPixels = 10)
    {
        var beatGrid = ViewModel?.BeatGrid;
        if (beatGrid == null || ViewModel == null) return -1;

        double time = ViewModel.XToTime(x);
        int bestIndex = -1;
        double bestDist = double.MaxValue;

        for (int i = 0; i < beatGrid.AllBeatPositions.Count; i++)
        {
            double beatTime = beatGrid.AllBeatPositions[i];
            double dist = Math.Abs(beatTime - time);
            double pixelDist = dist * ViewModel.PixelsPerSecond;

            if (pixelDist < hitThresholdPixels && dist < bestDist)
            {
                bestDist = dist;
                bestIndex = i;
            }
        }

        return bestIndex;
    }
}
