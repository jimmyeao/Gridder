using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gridder.Models;

namespace Gridder.ViewModels;

public partial class WaveformEditorViewModel : ObservableObject
{
    [ObservableProperty]
    private AudioTrack? _track;

    [ObservableProperty]
    private WaveformData? _waveformData;

    [ObservableProperty]
    private BeatGrid? _beatGrid;

    // View state
    [ObservableProperty]
    private double _pixelsPerSecond = 100.0;

    [ObservableProperty]
    private double _scrollPositionSeconds;

    [ObservableProperty]
    private double _viewWidthPixels = 800;

    [ObservableProperty]
    private double _playbackPositionSeconds;

    // Editing state
    [ObservableProperty]
    private int _selectedBeatIndex = -1;

    [ObservableProperty]
    private bool _isDraggingBeat;

    // Derived
    public double ViewStartSeconds => ScrollPositionSeconds;
    public double ViewEndSeconds => ScrollPositionSeconds + (ViewWidthPixels / PixelsPerSecond);
    public double TotalDurationSeconds => Track?.Duration.TotalSeconds ?? 0;
    public double TotalWidthPixels => TotalDurationSeconds * PixelsPerSecond;

    public string BpmDisplay
    {
        get
        {
            if (BeatGrid?.Markers.Count > 0)
            {
                var terminal = BeatGrid.Markers.LastOrDefault(m => m.IsTerminal);
                if (terminal != null)
                    return $"{terminal.Bpm:F1} BPM";
            }
            return "-- BPM";
        }
    }

    public string BeatsDisplay => BeatGrid?.AllBeatPositions.Count > 0
        ? $"{BeatGrid.AllBeatPositions.Count} beats"
        : "";

    public string SegmentsDisplay => BeatGrid?.Markers.Count > 1
        ? $"{BeatGrid.Markers.Count} segments"
        : BeatGrid?.Markers.Count == 1 ? "Constant tempo" : "";

    [RelayCommand]
    private void ZoomIn()
    {
        PixelsPerSecond = Math.Min(PixelsPerSecond * 1.5, 2000);
        OnPropertyChanged(nameof(ViewEndSeconds));
        OnPropertyChanged(nameof(TotalWidthPixels));
    }

    [RelayCommand]
    private void ZoomOut()
    {
        PixelsPerSecond = Math.Max(PixelsPerSecond / 1.5, 10);
        OnPropertyChanged(nameof(ViewEndSeconds));
        OnPropertyChanged(nameof(TotalWidthPixels));
    }

    [RelayCommand]
    private void ZoomToFit()
    {
        if (TotalDurationSeconds > 0 && ViewWidthPixels > 0)
        {
            PixelsPerSecond = ViewWidthPixels / TotalDurationSeconds;
            ScrollPositionSeconds = 0;
            OnPropertyChanged(nameof(ViewEndSeconds));
            OnPropertyChanged(nameof(TotalWidthPixels));
        }
    }

    public void LoadTrack(AudioTrack track)
    {
        Track = track;
        WaveformData = track.WaveformData;
        BeatGrid = track.BeatGrid;
        ScrollPositionSeconds = 0;
        SelectedBeatIndex = -1;

        OnPropertyChanged(nameof(TotalDurationSeconds));
        OnPropertyChanged(nameof(TotalWidthPixels));
        OnPropertyChanged(nameof(BpmDisplay));
        OnPropertyChanged(nameof(BeatsDisplay));
        OnPropertyChanged(nameof(SegmentsDisplay));

        ZoomToFit();
    }

    partial void OnScrollPositionSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(ViewStartSeconds));
        OnPropertyChanged(nameof(ViewEndSeconds));
    }

    partial void OnPixelsPerSecondChanged(double value)
    {
        OnPropertyChanged(nameof(ViewStartSeconds));
        OnPropertyChanged(nameof(ViewEndSeconds));
        OnPropertyChanged(nameof(TotalWidthPixels));
    }

    partial void OnViewWidthPixelsChanged(double value)
    {
        OnPropertyChanged(nameof(ViewEndSeconds));
    }

    partial void OnBeatGridChanged(BeatGrid? value)
    {
        OnPropertyChanged(nameof(BpmDisplay));
        OnPropertyChanged(nameof(BeatsDisplay));
        OnPropertyChanged(nameof(SegmentsDisplay));
    }

    /// <summary>
    /// Convert a time in seconds to an X pixel coordinate on the canvas.
    /// </summary>
    public float TimeToX(double seconds) =>
        (float)((seconds - ScrollPositionSeconds) * PixelsPerSecond);

    /// <summary>
    /// Convert an X pixel coordinate to a time in seconds.
    /// </summary>
    public double XToTime(float x) =>
        x / PixelsPerSecond + ScrollPositionSeconds;
}
