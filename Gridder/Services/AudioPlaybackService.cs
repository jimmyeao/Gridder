using Gridder.Helpers;
using Plugin.Maui.Audio;

namespace Gridder.Services;

public class AudioPlaybackService : IAudioPlaybackService, IDisposable
{
    private readonly IAudioManager _audioManager;
    private IAudioPlayer? _player;
    private IDispatcherTimer? _positionTimer;
    private byte[]? _clickWav;
    private byte[]? _downbeatClickWav;

    // Offset added to playback position to align with librosa's timeline.
    // MP3 decoders strip ~1152 samples of encoder delay that librosa preserves,
    // so playback runs ahead of librosa's coordinate system by this amount.
    private double _playbackOffsetSeconds;

    public double CurrentPositionSeconds => (_player?.CurrentPosition ?? 0) + _playbackOffsetSeconds;
    public double DurationSeconds => _player?.Duration ?? 0;
    public bool IsPlaying => _player?.IsPlaying ?? false;
    public bool IsLoaded => _player != null;

    public event Action<double>? PositionChanged;

    public AudioPlaybackService(IAudioManager audioManager)
    {
        _audioManager = audioManager;

        // Pre-generate click sounds
        _clickWav = ClickGenerator.GenerateClickWav();
        _downbeatClickWav = ClickGenerator.GenerateDownbeatClickWav();
    }

    public async Task LoadAsync(string filePath)
    {
        Stop();
        _player?.Dispose();

        // MP3 encoder delay: librosa's decoder preserves the ~1152-sample LAME
        // padding, but playback decoders strip it. Add an offset so the reported
        // position aligns with librosa's waveform/beat coordinate system.
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        _playbackOffsetSeconds = ext == ".mp3" ? 1152.0 / 44100.0 : 0.0;

        // Read entire file into memory so we don't hold a file lock
        // (TagLibSharp needs write access for saving beatgrids)
        var bytes = await File.ReadAllBytesAsync(filePath);
        var stream = new MemoryStream(bytes);
        _player = _audioManager.CreatePlayer(stream);

        // Set up position timer
        if (_positionTimer == null)
        {
            _positionTimer = Application.Current?.Dispatcher.CreateTimer();
            if (_positionTimer != null)
            {
                _positionTimer.Interval = TimeSpan.FromMilliseconds(30); // ~33fps update
                _positionTimer.Tick += (s, e) =>
                {
                    if (_player?.IsPlaying == true)
                    {
                        PositionChanged?.Invoke(CurrentPositionSeconds);
                    }
                };
            }
        }
    }

    public void Play()
    {
        _player?.Play();
        _positionTimer?.Start();
    }

    public void Pause()
    {
        _player?.Pause();
        _positionTimer?.Stop();
        PositionChanged?.Invoke(CurrentPositionSeconds);
    }

    public void Stop()
    {
        _player?.Stop();
        _positionTimer?.Stop();
        PositionChanged?.Invoke(0);
    }

    public void Seek(double positionSeconds)
    {
        // Convert from librosa timeline to player timeline
        _player?.Seek(Math.Max(0, positionSeconds - _playbackOffsetSeconds));
        PositionChanged?.Invoke(positionSeconds);
    }

    public void PlayClick(bool isDownbeat)
    {
        try
        {
            var wavData = isDownbeat ? _downbeatClickWav : _clickWav;
            if (wavData == null) return;

            var stream = new MemoryStream(wavData);
            var clickPlayer = _audioManager.CreatePlayer(stream);
            clickPlayer.Play();

            // Dispose after it finishes (fire and forget)
            _ = Task.Delay(100).ContinueWith(_ =>
            {
                clickPlayer.Dispose();
                stream.Dispose();
            });
        }
        catch
        {
            // Don't let click errors interrupt playback
        }
    }

    public void Dispose()
    {
        _positionTimer?.Stop();
        _player?.Dispose();
    }
}
