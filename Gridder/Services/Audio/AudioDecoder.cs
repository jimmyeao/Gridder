namespace Gridder.Services.Audio;

/// <summary>
/// Unified audio decoder: MP3/FLAC/WAV → float[] PCM at target sample rate, mono.
/// </summary>
public static class AudioDecoder
{
    private const int TargetSampleRate = 44100;

    /// <summary>
    /// Decode an audio file to mono float samples normalized to [-1, 1].
    /// Returns (samples, sampleRate).
    /// </summary>
    public static (float[] Samples, int SampleRate) Decode(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();

        var (samples, sr, channels) = ext switch
        {
            ".mp3" => DecodeMp3(filePath),
            ".flac" => DecodeFlac(filePath),
            ".wav" => DecodeWav(filePath),
            _ => throw new NotSupportedException($"Unsupported audio format: {ext}")
        };

        // Mix to mono if stereo
        if (channels > 1)
            samples = MixToMono(samples, channels);

        // Resample if necessary
        if (sr != TargetSampleRate)
            samples = Resample(samples, sr, TargetSampleRate);

        return (samples, TargetSampleRate);
    }

    private static (float[] Samples, int SampleRate, int Channels) DecodeMp3(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var mpegFile = new NLayer.MpegFile(stream);

        int sampleRate = mpegFile.SampleRate;
        int channels = mpegFile.Channels;

        // Read all samples
        var buffer = new List<float>();
        var readBuf = new float[4096];
        int read;
        while ((read = mpegFile.ReadSamples(readBuf, 0, readBuf.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
                buffer.Add(readBuf[i]);
        }

        return (buffer.ToArray(), sampleRate, channels);
    }

    private static (float[] Samples, int SampleRate, int Channels) DecodeFlac(string filePath)
    {
        // Use FlacBox to decode FLAC → WAV PCM via WaveOverFlacStream
        using var flacStream = File.OpenRead(filePath);

        // Read stream info for metadata
        var flacReader = new FlacBox.FlacReader(flacStream, false);
        while (flacReader.Read())
        {
            if (flacReader.RecordType == FlacBox.FlacRecordType.MetadataBlock
                && flacReader.MetadataBlockType == FlacBox.FlacMetadataBlockType.Streaminfo)
                break;
            if (flacReader.RecordType == FlacBox.FlacRecordType.Frame)
                break;
        }

        var info = flacReader.Streaminfo;
        int sampleRate = info.SampleRate;
        int channels = info.ChannelsCount;
        int bitsPerSample = info.BitsPerSample;
        flacReader.Close();

        // Re-open and decode via WaveOverFlacStream (produces WAV PCM bytes)
        using var flacStream2 = File.OpenRead(filePath);
        using var waveStream = new FlacBox.WaveOverFlacStream(flacStream2, FlacBox.WaveOverFlacStreamMode.Decode);

        // WaveOverFlacStream wraps the FLAC as a WAV — skip the 44-byte WAV header
        var wavHeader = new byte[44];
        int headerRead = waveStream.Read(wavHeader, 0, 44);
        if (headerRead < 44)
            throw new InvalidDataException("FLAC decode: failed to read WAV header from stream");

        // Parse WAV header to get actual data size
        // Bytes 40-43: data chunk size (little-endian)
        int dataSize = wavHeader[40] | (wavHeader[41] << 8) | (wavHeader[42] << 16) | (wavHeader[43] << 24);

        // Read all PCM data
        var pcmData = new byte[dataSize > 0 ? dataSize : (int)(info.TotalSampleCount * channels * (bitsPerSample / 8))];
        int totalRead = 0;
        while (totalRead < pcmData.Length)
        {
            int read = waveStream.Read(pcmData, totalRead, pcmData.Length - totalRead);
            if (read == 0) break;
            totalRead += read;
        }

        if (totalRead < pcmData.Length)
            Array.Resize(ref pcmData, totalRead);

        var samples = ConvertToFloat(pcmData, bitsPerSample, 1 /* PCM */);
        return (samples, sampleRate, channels);
    }

    private static (float[] Samples, int SampleRate, int Channels) DecodeWav(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var reader = new BinaryReader(stream);

        // Read RIFF header
        var riffId = new string(reader.ReadChars(4));
        if (riffId != "RIFF")
            throw new InvalidDataException("Not a valid WAV file (missing RIFF header)");

        reader.ReadInt32(); // file size
        var waveId = new string(reader.ReadChars(4));
        if (waveId != "WAVE")
            throw new InvalidDataException("Not a valid WAV file (missing WAVE identifier)");

        // Find fmt chunk
        int audioFormat = 1;
        int numChannels = 2;
        int sampleRate = 44100;
        int bitsPerSample = 16;

        while (stream.Position < stream.Length - 8)
        {
            var chunkId = new string(reader.ReadChars(4));
            int chunkSize = reader.ReadInt32();

            if (chunkId == "fmt ")
            {
                audioFormat = reader.ReadInt16();
                numChannels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32(); // byte rate
                reader.ReadInt16(); // block align
                bitsPerSample = reader.ReadInt16();

                // Skip any extra format bytes
                if (chunkSize > 16)
                    reader.ReadBytes(chunkSize - 16);
            }
            else if (chunkId == "data")
            {
                // Read audio data
                var rawData = reader.ReadBytes(chunkSize);
                var samples = ConvertToFloat(rawData, bitsPerSample, audioFormat);
                return (samples, sampleRate, numChannels);
            }
            else
            {
                // Skip unknown chunks
                if (chunkSize > 0 && stream.Position + chunkSize <= stream.Length)
                    reader.ReadBytes(chunkSize);
                else
                    break;
            }
        }

        throw new InvalidDataException("WAV file missing data chunk");
    }

    private static float[] ConvertToFloat(byte[] rawData, int bitsPerSample, int audioFormat)
    {
        if (audioFormat == 3) // IEEE float
        {
            if (bitsPerSample == 32)
            {
                var samples = new float[rawData.Length / 4];
                Buffer.BlockCopy(rawData, 0, samples, 0, rawData.Length);
                return samples;
            }
            if (bitsPerSample == 64)
            {
                int count = rawData.Length / 8;
                var samples = new float[count];
                for (int i = 0; i < count; i++)
                    samples[i] = (float)BitConverter.ToDouble(rawData, i * 8);
                return samples;
            }
        }

        // PCM integer formats
        if (bitsPerSample == 16)
        {
            int count = rawData.Length / 2;
            var samples = new float[count];
            for (int i = 0; i < count; i++)
            {
                short value = (short)(rawData[i * 2] | (rawData[i * 2 + 1] << 8));
                samples[i] = value / 32768f;
            }
            return samples;
        }

        if (bitsPerSample == 24)
        {
            int count = rawData.Length / 3;
            var samples = new float[count];
            for (int i = 0; i < count; i++)
            {
                int value = rawData[i * 3] | (rawData[i * 3 + 1] << 8) | (rawData[i * 3 + 2] << 16);
                if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000); // sign extend
                samples[i] = value / 8388608f;
            }
            return samples;
        }

        if (bitsPerSample == 32)
        {
            int count = rawData.Length / 4;
            var samples = new float[count];
            for (int i = 0; i < count; i++)
            {
                int value = BitConverter.ToInt32(rawData, i * 4);
                samples[i] = value / 2147483648f;
            }
            return samples;
        }

        if (bitsPerSample == 8)
        {
            var samples = new float[rawData.Length];
            for (int i = 0; i < rawData.Length; i++)
                samples[i] = (rawData[i] - 128) / 128f;
            return samples;
        }

        throw new NotSupportedException($"Unsupported bits per sample: {bitsPerSample}");
    }

    private static float[] MixToMono(float[] samples, int channels)
    {
        int frameCount = samples.Length / channels;
        var mono = new float[frameCount];

        for (int i = 0; i < frameCount; i++)
        {
            float sum = 0;
            for (int ch = 0; ch < channels; ch++)
                sum += samples[i * channels + ch];
            mono[i] = sum / channels;
        }

        return mono;
    }

    /// <summary>
    /// Simple linear interpolation resampler.
    /// Sufficient for beat detection (not audiophile quality).
    /// </summary>
    private static float[] Resample(float[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate) return input;

        double ratio = (double)fromRate / toRate;
        int outputLength = (int)(input.Length / ratio);
        var output = new float[outputLength];

        for (int i = 0; i < outputLength; i++)
        {
            double srcPos = i * ratio;
            int srcIdx = (int)srcPos;
            double frac = srcPos - srcIdx;

            if (srcIdx + 1 < input.Length)
                output[i] = (float)(input[srcIdx] * (1 - frac) + input[srcIdx + 1] * frac);
            else if (srcIdx < input.Length)
                output[i] = input[srcIdx];
        }

        return output;
    }
}
