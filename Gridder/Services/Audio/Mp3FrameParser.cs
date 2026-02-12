namespace Gridder.Services.Audio;

/// <summary>
/// Extract LAME encoder delay from MP3 file headers.
/// Port of mp3_utils.py:get_mp3_encoder_delay().
/// </summary>
public static class Mp3FrameParser
{
    private const int DefaultLameDelay = 576;

    /// <summary>
    /// Read the encoder delay (in samples) from an MP3 file's LAME/Xing header.
    /// Returns the encoder delay in samples, or 576 (LAME default) if the
    /// LAME header cannot be found. Returns 0 for non-MP3 files.
    /// </summary>
    public static int GetEncoderDelay(string filePath)
    {
        if (!filePath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            return 0;

        byte[] data;
        try
        {
            using var fs = File.OpenRead(filePath);
            data = new byte[Math.Min(16384, fs.Length)];
            int bytesRead = fs.Read(data, 0, data.Length);
            if (bytesRead < 128) return DefaultLameDelay;
            if (bytesRead < data.Length)
                Array.Resize(ref data, bytesRead);
        }
        catch (IOException)
        {
            return DefaultLameDelay;
        }

        // Skip ID3v2 tag if present
        int offset = 0;
        if (data.Length >= 10 && data[0] == (byte)'I' && data[1] == (byte)'D' && data[2] == (byte)'3')
        {
            int size = ((data[6] & 0x7F) << 21)
                     | ((data[7] & 0x7F) << 14)
                     | ((data[8] & 0x7F) << 7)
                     | (data[9] & 0x7F);
            offset = 10 + size;

            // Re-read if ID3 tag extends past our initial buffer
            if (offset + 4096 > data.Length)
            {
                try
                {
                    using var fs = File.OpenRead(filePath);
                    fs.Seek(offset, SeekOrigin.Begin);
                    data = new byte[4096];
                    int bytesRead = fs.Read(data, 0, data.Length);
                    if (bytesRead < data.Length)
                        Array.Resize(ref data, bytesRead);
                    offset = 0;
                }
                catch (IOException)
                {
                    return DefaultLameDelay;
                }
            }
        }

        // Find first MPEG audio frame sync (0xFFE0+)
        int maxSearch = Math.Min(data.Length - 4, offset + 8192);
        bool found = false;
        while (offset < maxSearch)
        {
            if (data[offset] == 0xFF && (data[offset + 1] & 0xE0) == 0xE0)
            {
                byte b1 = data[offset + 1];
                int mpegVer = (b1 >> 3) & 3;
                int layer = (b1 >> 1) & 3;
                if (mpegVer != 1 && layer != 0) // not reserved values
                {
                    found = true;
                    break;
                }
            }
            offset++;
        }

        if (!found) return DefaultLameDelay;

        int frameStart = offset;
        if (frameStart + 4 > data.Length) return DefaultLameDelay;

        uint header = (uint)((data[frameStart] << 24) | (data[frameStart + 1] << 16)
                           | (data[frameStart + 2] << 8) | data[frameStart + 3]);

        int mpegVersion = (int)((header >> 19) & 3);   // 3=MPEG1, 2=MPEG2, 0=MPEG2.5
        int channelMode = (int)((header >> 6) & 3);     // 3=mono, 0-2=stereo variants

        // Side information size depends on MPEG version and channel mode
        int sideInfoSize = mpegVersion == 3
            ? (channelMode != 3 ? 32 : 17)
            : (channelMode != 3 ? 17 : 9);

        // Xing/Info tag follows the frame header (4 bytes) + side information
        int xingOffset = frameStart + 4 + sideInfoSize;
        if (xingOffset + 8 > data.Length) return DefaultLameDelay;

        // Check for Xing or Info tag
        bool isXing = data[xingOffset] == (byte)'X' && data[xingOffset + 1] == (byte)'i'
                   && data[xingOffset + 2] == (byte)'n' && data[xingOffset + 3] == (byte)'g';
        bool isInfo = data[xingOffset] == (byte)'I' && data[xingOffset + 1] == (byte)'n'
                   && data[xingOffset + 2] == (byte)'f' && data[xingOffset + 3] == (byte)'o';

        if (!isXing && !isInfo) return DefaultLameDelay;

        // Parse Xing flags to skip variable-length fields
        uint xingFlags = (uint)((data[xingOffset + 4] << 24) | (data[xingOffset + 5] << 16)
                              | (data[xingOffset + 6] << 8) | data[xingOffset + 7]);

        int pos = xingOffset + 8;
        if ((xingFlags & 0x01) != 0) pos += 4; // Frames count
        if ((xingFlags & 0x02) != 0) pos += 4; // Bytes count
        if ((xingFlags & 0x04) != 0) pos += 100; // TOC
        if ((xingFlags & 0x08) != 0) pos += 4; // Quality

        // Encoder delay is at offset +21 from the version string position
        int delayOffset = pos + 21;
        if (delayOffset + 3 > data.Length) return DefaultLameDelay;

        // Verify this looks like a LAME/encoder tag (version string is ASCII)
        bool looksLikeVersion = true;
        for (int i = pos; i < pos + 9 && i < data.Length; i++)
        {
            if (data[i] != 0 && (data[i] < 32 || data[i] >= 127))
            {
                looksLikeVersion = false;
                break;
            }
        }
        if (!looksLikeVersion) return DefaultLameDelay;

        int b0 = data[delayOffset];
        int b1i = data[delayOffset + 1];

        int encoderDelay = (b0 << 4) | (b1i >> 4);

        // Sanity check: typical encoder delay is 0-5000 samples
        return (encoderDelay > 0 && encoderDelay < 5000) ? encoderDelay : DefaultLameDelay;
    }
}
