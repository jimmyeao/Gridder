using System.Text.Json.Serialization;

namespace Gridder.Models;

public record AnalysisResult
{
    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("file_path")]
    public string FilePath { get; init; } = string.Empty;

    [JsonPropertyName("sample_rate")]
    public int SampleRate { get; init; }

    [JsonPropertyName("duration_seconds")]
    public double DurationSeconds { get; init; }

    [JsonPropertyName("beats")]
    public double[] Beats { get; init; } = [];

    [JsonPropertyName("tempo_segments")]
    public TempoSegment[] TempoSegments { get; init; } = [];

    [JsonPropertyName("waveform")]
    public WaveformResult? Waveform { get; init; }
}

public record TempoSegment
{
    [JsonPropertyName("start_beat_index")]
    public int StartBeatIndex { get; init; }

    [JsonPropertyName("end_beat_index")]
    public int EndBeatIndex { get; init; }

    [JsonPropertyName("start_position")]
    public double StartPosition { get; init; }

    [JsonPropertyName("bpm")]
    public double Bpm { get; init; }

    [JsonPropertyName("beat_count")]
    public int BeatCount { get; init; }
}

public record WaveformResult
{
    [JsonPropertyName("samples_per_pixel")]
    public int SamplesPerPixel { get; init; }

    [JsonPropertyName("peaks_positive")]
    public float[] PeaksPositive { get; init; } = [];

    [JsonPropertyName("peaks_negative")]
    public float[] PeaksNegative { get; init; } = [];

    [JsonPropertyName("onset_envelope")]
    public float[] OnsetEnvelope { get; init; } = [];
}
