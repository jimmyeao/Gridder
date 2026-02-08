namespace Gridder.Models;

public class WaveformData
{
    public int SamplesPerPixel { get; init; }
    public float[] PeaksPositive { get; init; } = [];
    public float[] PeaksNegative { get; init; } = [];
    public float[] OnsetEnvelope { get; init; } = [];
    public double DurationSeconds { get; init; }
    public int SampleRate { get; init; }

    public int PixelCount => PeaksPositive.Length;

    /// <summary>
    /// Convert a time in seconds to a pixel index in the waveform data.
    /// </summary>
    public int TimeToPixel(double seconds)
    {
        if (DurationSeconds <= 0 || PixelCount <= 0) return 0;
        return (int)Math.Clamp(seconds / DurationSeconds * PixelCount, 0, PixelCount - 1);
    }

    /// <summary>
    /// Convert a pixel index to a time in seconds.
    /// </summary>
    public double PixelToTime(int pixel)
    {
        if (PixelCount <= 0) return 0;
        return (double)pixel / PixelCount * DurationSeconds;
    }
}
