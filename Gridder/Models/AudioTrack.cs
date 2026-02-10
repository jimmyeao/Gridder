using CommunityToolkit.Mvvm.ComponentModel;

namespace Gridder.Models;

public partial class AudioTrack : ObservableObject
{
    public string FilePath { get; init; } = string.Empty;
    public string FileName => Path.GetFileName(FilePath);
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public string FileFormat => Path.GetExtension(FilePath).TrimStart('.').ToUpperInvariant();
    public bool HasExistingBeatGrid { get; set; }

    [ObservableProperty]
    private AnalysisStatus _analysisStatus = AnalysisStatus.NotAnalyzed;

    [ObservableProperty]
    private BeatGrid? _beatGrid;

    [ObservableProperty]
    private WaveformData? _waveformData;

    [ObservableProperty]
    private string? _analysisError;

    [ObservableProperty]
    private double _analysisProgress;

    public string DisplayName => string.IsNullOrWhiteSpace(Title)
        ? Path.GetFileNameWithoutExtension(FilePath)
        : Title;

    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist)
        ? "Unknown Artist"
        : Artist;

    public string DurationDisplay => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");
}

public enum AnalysisStatus
{
    NotAnalyzed,
    Analyzing,
    Analyzed,
    Error
}
