using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gridder.Models;
using Gridder.Services;

namespace Gridder.ViewModels;

public partial class PlaybackViewModel : ObservableObject
{
    private readonly IAudioPlaybackService _playback;
    private BeatGrid? _beatGrid;
    private int _lastBeatIndex = -1;

    public PlaybackViewModel(IAudioPlaybackService playback)
    {
        _playback = playback;
        _playback.PositionChanged += OnPositionChanged;
    }

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isLoaded;

    [ObservableProperty]
    private double _currentPositionSeconds;

    [ObservableProperty]
    private double _durationSeconds;

    [ObservableProperty]
    private bool _isMetronomeEnabled = true;

    [ObservableProperty]
    private string _positionDisplay = "0:00.0";

    /// <summary>
    /// Fired when playback position updates, so the waveform can redraw the cursor.
    /// </summary>
    public event Action<double>? PositionUpdated;

    public async Task LoadTrackAsync(string filePath, BeatGrid? beatGrid)
    {
        _beatGrid = beatGrid;
        _lastBeatIndex = -1;

        await _playback.LoadAsync(filePath);
        DurationSeconds = _playback.DurationSeconds;
        IsLoaded = _playback.IsLoaded;
        CurrentPositionSeconds = 0;
        IsPlaying = false;
        UpdatePositionDisplay();
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (!IsLoaded) return;

        if (IsPlaying)
        {
            _playback.Pause();
            IsPlaying = false;
        }
        else
        {
            _playback.Play();
            IsPlaying = true;
        }
    }

    [RelayCommand]
    private void Stop()
    {
        _playback.Stop();
        IsPlaying = false;
        CurrentPositionSeconds = 0;
        _lastBeatIndex = -1;
        UpdatePositionDisplay();
        PositionUpdated?.Invoke(0);
    }

    [RelayCommand]
    private void SkipBack()
    {
        var newPos = Math.Max(0, CurrentPositionSeconds - 5);
        _playback.Seek(newPos);
        _lastBeatIndex = -1; // reset so metronome recalibrates
    }

    [RelayCommand]
    private void SkipForward()
    {
        var newPos = Math.Min(DurationSeconds, CurrentPositionSeconds + 5);
        _playback.Seek(newPos);
        _lastBeatIndex = -1;
    }

    public void SeekTo(double seconds)
    {
        _playback.Seek(seconds);
        _lastBeatIndex = -1;
        CurrentPositionSeconds = seconds;
        UpdatePositionDisplay();
        PositionUpdated?.Invoke(seconds);
    }

    private void OnPositionChanged(double positionSeconds)
    {
        CurrentPositionSeconds = positionSeconds;
        UpdatePositionDisplay();
        PositionUpdated?.Invoke(positionSeconds);

        // Metronome: check if we crossed a beat
        if (IsMetronomeEnabled && _beatGrid?.AllBeatPositions.Count > 0)
        {
            CheckMetronome(positionSeconds);
        }
    }

    private void CheckMetronome(double position)
    {
        var beats = _beatGrid!.AllBeatPositions;

        // Find the current beat index (binary search)
        int idx = beats.BinarySearch(position);
        if (idx < 0) idx = ~idx - 1;
        if (idx < 0) idx = 0;

        // If we've advanced to a new beat since last check, play click
        if (idx > _lastBeatIndex && idx < beats.Count)
        {
            bool isDownbeat = idx % 4 == 0;
            _playback.PlayClick(isDownbeat);
            _lastBeatIndex = idx;
        }
    }

    private void UpdatePositionDisplay()
    {
        var ts = TimeSpan.FromSeconds(CurrentPositionSeconds);
        PositionDisplay = ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss\.f")
            : ts.ToString(@"m\:ss\.f");
    }
}
