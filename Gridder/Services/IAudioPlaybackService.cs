namespace Gridder.Services;

public interface IAudioPlaybackService
{
    Task LoadAsync(string filePath);
    void Play();
    void Pause();
    void Stop();
    void Seek(double positionSeconds);
    double CurrentPositionSeconds { get; }
    double DurationSeconds { get; }
    bool IsPlaying { get; }
    bool IsLoaded { get; }
    event Action<double>? PositionChanged;
    void PlayClick(bool isDownbeat);
}
