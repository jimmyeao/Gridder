namespace Gridder.Helpers;

/// <summary>
/// Generates a short click/tick WAV sound in memory for the metronome.
/// </summary>
public static class ClickGenerator
{
    /// <summary>
    /// Generate a short percussive click as a WAV byte array.
    /// </summary>
    public static byte[] GenerateClickWav(int sampleRate = 44100, double durationMs = 15, double frequency = 1000)
    {
        int numSamples = (int)(sampleRate * durationMs / 1000.0);
        short[] samples = new short[numSamples];

        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / sampleRate;
            // Sine wave with exponential decay envelope
            double envelope = Math.Exp(-t * 300);
            double value = Math.Sin(2 * Math.PI * frequency * t) * envelope;
            samples[i] = (short)(value * short.MaxValue * 0.8);
        }

        return EncodeWav(samples, sampleRate, 1, 16);
    }

    /// <summary>
    /// Generate a higher-pitched click for downbeats.
    /// </summary>
    public static byte[] GenerateDownbeatClickWav(int sampleRate = 44100)
    {
        return GenerateClickWav(sampleRate, durationMs: 20, frequency: 1500);
    }

    private static byte[] EncodeWav(short[] samples, int sampleRate, int channels, int bitsPerSample)
    {
        int bytesPerSample = bitsPerSample / 8;
        int dataSize = samples.Length * bytesPerSample;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // RIFF header
        bw.Write("RIFF"u8);
        bw.Write(36 + dataSize);
        bw.Write("WAVE"u8);

        // fmt chunk
        bw.Write("fmt "u8);
        bw.Write(16); // chunk size
        bw.Write((short)1); // PCM
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * bytesPerSample); // byte rate
        bw.Write((short)(channels * bytesPerSample)); // block align
        bw.Write((short)bitsPerSample);

        // data chunk
        bw.Write("data"u8);
        bw.Write(dataSize);
        foreach (var sample in samples)
            bw.Write(sample);

        return ms.ToArray();
    }
}
