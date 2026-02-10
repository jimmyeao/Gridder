using System.Text;
using Gridder.Models;

namespace Gridder.Services;

/// <summary>
/// Reads and writes Serato BeatGrid data from/to audio file tags.
///
/// MP3: Stored in ID3v2 GEOB frame with description "Serato BeatGrid"
/// FLAC: Stored in Vorbis Comment field "SERATO_BEATGRID", base64-encoded
/// </summary>
public class SeratoTagService : ISeratoTagService
{
    private readonly IBeatGridSerializer _serializer;

    public SeratoTagService(IBeatGridSerializer serializer)
    {
        _serializer = serializer;
    }

    public BeatGrid? ReadBeatGrid(string filePath)
    {
        using var file = TagLib.File.Create(filePath);
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        byte[]? beatGridData = null;

        if (ext == ".mp3")
        {
            beatGridData = ReadFromId3(file);
        }
        else if (ext == ".flac")
        {
            beatGridData = ReadFromVorbis(file);
        }

        if (beatGridData == null || beatGridData.Length < 7)
            return null;

        return _serializer.Deserialize(beatGridData);
    }

    public void WriteBeatGrid(string filePath, BeatGrid beatGrid)
    {
        var data = _serializer.Serialize(beatGrid);

        using var file = TagLib.File.Create(filePath);
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (ext == ".mp3")
        {
            WriteToId3(file, data);
        }
        else if (ext == ".flac")
        {
            WriteToVorbis(file, data);
        }

        file.Save();
    }

    private static byte[]? ReadFromId3(TagLib.File file)
    {
        if (file.GetTag(TagLib.TagTypes.Id3v2) is not TagLib.Id3v2.Tag id3Tag)
            return null;

        var frames = id3Tag.GetFrames<TagLib.Id3v2.AttachmentFrame>();
        foreach (var frame in frames)
        {
            if (frame.Description == "Serato BeatGrid")
            {
                var data = frame.Data.Data;
                // Strip "Serato BeatGrid\0" header if present
                var seratoHeader = "Serato BeatGrid\0"u8;
                if (data.AsSpan().StartsWith(seratoHeader))
                    data = data[seratoHeader.Length..];
                return data;
            }
        }

        return null;
    }

    private static byte[]? ReadFromVorbis(TagLib.File file)
    {
        if (file.GetTag(TagLib.TagTypes.Xiph) is not TagLib.Ogg.XiphComment xiph)
            return null;

        var fields = xiph.GetField("SERATO_BEATGRID");
        if (fields == null || fields.Length == 0)
            return null;

        var base64 = fields[0];

        // Decode base64 (may contain linefeeds, and padding may be stripped)
        base64 = base64.Replace("\n", "").Replace("\r", "");
        // Restore padding if missing (TagLib or Serato may strip trailing '=')
        var padNeeded = (4 - base64.Length % 4) % 4;
        if (padNeeded > 0)
            base64 += new string('=', padNeeded);
        var fullData = Convert.FromBase64String(base64);

        // Strip the "application/octet-stream\0\0" prefix
        // Find the double null after the mime type
        var mimeType = "application/octet-stream";
        int prefixLen = mimeType.Length + 2; // mime + null + null

        if (fullData.Length <= prefixLen)
            return null;

        var beatGridData = new byte[fullData.Length - prefixLen];
        Array.Copy(fullData, prefixLen, beatGridData, 0, beatGridData.Length);

        // Serato's native format prepends "Serato BeatGrid\0" before the version byte.
        // Our serializer doesn't write this header, but we must handle it when reading.
        var seratoHeader = "Serato BeatGrid\0"u8;
        if (beatGridData.AsSpan().StartsWith(seratoHeader))
        {
            beatGridData = beatGridData[seratoHeader.Length..];
        }

        return beatGridData;
    }

    private static void WriteToId3(TagLib.File file, byte[] data)
    {
        var id3Tag = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true);

        // Remove existing Serato BeatGrid frame(s)
        var existing = id3Tag.GetFrames<TagLib.Id3v2.AttachmentFrame>()
            .Where(f => f.Description == "Serato BeatGrid")
            .ToList();
        foreach (var frame in existing)
            id3Tag.RemoveFrame(frame);

        // Write raw binary data directly — the GEOB frame's description field
        // already identifies this as "Serato BeatGrid", so no prefix is needed.
        // (Serato itself does NOT include a "Serato BeatGrid\0" prefix in the
        // GEOB data for MP3 files; unlike FLAC where it's required.)
        var newFrame = new TagLib.Id3v2.AttachmentFrame
        {
            Type = TagLib.PictureType.NotAPicture,
            Description = "Serato BeatGrid",
            MimeType = "application/octet-stream",
            TextEncoding = TagLib.StringType.Latin1, // Serato expects Latin1
            Data = new TagLib.ByteVector(data),
        };
        id3Tag.AddFrame(newFrame);
    }

    private static void WriteToVorbis(TagLib.File file, byte[] data)
    {
        var xiph = (TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph, true);

        // Build the full payload: mime_type + \0\0 + "Serato BeatGrid\0" + binary_data
        // The "Serato BeatGrid\0" header is part of Serato's native format.
        var mimeBytes = Encoding.ASCII.GetBytes("application/octet-stream");
        var seratoHeader = "Serato BeatGrid\0"u8;
        var fullData = new byte[mimeBytes.Length + 2 + seratoHeader.Length + data.Length];
        int pos = 0;
        mimeBytes.CopyTo(fullData, pos); pos += mimeBytes.Length;
        fullData[pos++] = 0;
        fullData[pos++] = 0;
        seratoHeader.CopyTo(fullData.AsSpan(pos)); pos += seratoHeader.Length;
        data.CopyTo(fullData, pos);

        // Base64 encode with linefeeds every 72 chars
        var base64 = Convert.ToBase64String(fullData);
        var sb = new StringBuilder();
        for (int i = 0; i < base64.Length; i += 72)
        {
            sb.Append(base64, i, Math.Min(72, base64.Length - i));
            if (i + 72 < base64.Length) sb.Append('\n');
        }

        xiph.SetField("SERATO_BEATGRID", sb.ToString());
    }

    // ── Serato Markers2 / BPMLOCK support ────────────────────────────────

    public void SetBpmLock(string filePath, bool locked)
    {
        using var file = TagLib.File.Create(filePath);
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (ext == ".mp3")
            SetBpmLockMp3(file, locked);
        else if (ext == ".flac")
            SetBpmLockFlac(file, locked);

        file.Save();
    }

    private static void SetBpmLockMp3(TagLib.File file, bool locked)
    {
        var id3Tag = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true);

        // Read existing Markers2 payload (may be null if no tag exists)
        byte[]? payload = null;
        var frames = id3Tag.GetFrames<TagLib.Id3v2.AttachmentFrame>();
        foreach (var frame in frames)
        {
            if (frame.Description == "Serato Markers2")
            {
                payload = DecodeMarkers2Mp3(frame.Data.Data);
                break;
            }
        }

        // Modify payload to set BPMLOCK
        payload = SetBpmLockInPayload(payload, locked);

        // Encode and write back
        var encoded = EncodeMarkers2Mp3(payload);

        // Remove existing Serato Markers2 frame(s)
        var existing = id3Tag.GetFrames<TagLib.Id3v2.AttachmentFrame>()
            .Where(f => f.Description == "Serato Markers2")
            .ToList();
        foreach (var f in existing)
            id3Tag.RemoveFrame(f);

        var newFrame = new TagLib.Id3v2.AttachmentFrame
        {
            Type = TagLib.PictureType.NotAPicture,
            Description = "Serato Markers2",
            MimeType = "application/octet-stream",
            TextEncoding = TagLib.StringType.Latin1,
            Data = new TagLib.ByteVector(encoded),
        };
        id3Tag.AddFrame(newFrame);
    }

    private static void SetBpmLockFlac(TagLib.File file, bool locked)
    {
        var xiph = (TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph, true);

        // Read existing payload
        byte[]? payload = null;
        var fields = xiph.GetField("SERATO_MARKERS_V2");
        if (fields != null && fields.Length > 0)
            payload = DecodeMarkers2Flac(fields[0]);

        // Modify payload to set BPMLOCK
        payload = SetBpmLockInPayload(payload, locked);

        // Encode and write back
        var encoded = EncodeMarkers2Flac(payload);
        xiph.SetField("SERATO_MARKERS_V2", encoded);
    }

    /// <summary>
    /// Decode Markers2 MP3 GEOB data → binary payload (without version header).
    /// GEOB data format: [0x01, 0x01] version + base64 string + null padding
    /// </summary>
    private static byte[]? DecodeMarkers2Mp3(byte[] geobData)
    {
        if (geobData == null || geobData.Length < 3)
            return null;

        // Skip [0x01, 0x01] version header
        int start = 2;

        // Find end of base64 string (first null byte after header)
        int end = start;
        while (end < geobData.Length && geobData[end] != 0x00)
            end++;

        if (end <= start)
            return null;

        var base64 = Encoding.ASCII.GetString(geobData, start, end - start);
        return DecodeSeratoBase64(base64);
    }

    /// <summary>
    /// Encode binary payload → Markers2 MP3 GEOB data.
    /// </summary>
    private static byte[] EncodeMarkers2Mp3(byte[] payload)
    {
        var base64 = EncodeSeratoBase64(payload);
        var base64Bytes = Encoding.ASCII.GetBytes(base64);

        // [0x01, 0x01] + base64 + null padding to minimum 470 bytes
        int totalLen = Math.Max(2 + base64Bytes.Length + 1, 470);
        var result = new byte[totalLen];
        result[0] = 0x01;
        result[1] = 0x01;
        base64Bytes.CopyTo(result, 2);
        // Rest is already zero-filled
        return result;
    }

    /// <summary>
    /// Decode Markers2 FLAC Vorbis Comment value → binary payload.
    /// Base64-encoded with "application/octet-stream\0\0Serato Markers2\0" prefix before payload.
    /// </summary>
    private static byte[]? DecodeMarkers2Flac(string fieldValue)
    {
        var base64 = fieldValue.Replace("\n", "").Replace("\r", "");
        var padNeeded = (4 - base64.Length % 4) % 4;
        if (padNeeded > 0)
            base64 += new string('=', padNeeded);
        var fullData = Convert.FromBase64String(base64);

        // Strip "application/octet-stream\0\0Serato Markers2\0" prefix
        var prefix = "application/octet-stream\0\0Serato Markers2\0"u8;
        if (fullData.Length <= prefix.Length || !fullData.AsSpan().StartsWith(prefix))
            return null;

        return fullData[prefix.Length..];
    }

    /// <summary>
    /// Encode binary payload → Markers2 FLAC Vorbis Comment value.
    /// </summary>
    private static string EncodeMarkers2Flac(byte[] payload)
    {
        var prefix = Encoding.ASCII.GetBytes("application/octet-stream\0\0Serato Markers2\0");
        var fullData = new byte[prefix.Length + payload.Length];
        prefix.CopyTo(fullData, 0);
        payload.CopyTo(fullData, prefix.Length);

        var base64 = Convert.ToBase64String(fullData);
        var sb = new StringBuilder();
        for (int i = 0; i < base64.Length; i += 72)
        {
            sb.Append(base64, i, Math.Min(72, base64.Length - i));
            if (i + 72 < base64.Length) sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Decode Serato's quirky base64: 72-char lines joined with \n,
    /// trailing = may be replaced with A, padding may be incomplete.
    /// </summary>
    private static byte[] DecodeSeratoBase64(string base64)
    {
        base64 = base64.Replace("\n", "").Replace("\r", "");
        var remainder = base64.Length % 4;
        if (remainder == 1)
            base64 += "A==";
        else if (remainder > 0)
            base64 += new string('=', 4 - remainder);
        return Convert.FromBase64String(base64);
    }

    /// <summary>
    /// Encode to Serato's base64 format: 72-char lines joined with \n.
    /// </summary>
    private static string EncodeSeratoBase64(byte[] data)
    {
        var base64 = Convert.ToBase64String(data);
        var sb = new StringBuilder();
        for (int i = 0; i < base64.Length; i += 72)
        {
            sb.Append(base64, i, Math.Min(72, base64.Length - i));
            if (i + 72 < base64.Length) sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Modify the Markers2 binary payload to set or insert the BPMLOCK entry.
    /// Preserves all other entries (CUE, LOOP, COLOR, FLIP, etc.).
    /// Payload format: [0x01, 0x01] version + entries + [0x00] terminator.
    /// Entry format: null-terminated type string + uint32 BE length + data bytes.
    /// </summary>
    private static byte[] SetBpmLockInPayload(byte[]? payload, bool locked)
    {
        var entries = new List<(string Type, byte[] Data)>();

        if (payload != null && payload.Length >= 2 &&
            payload[0] == 0x01 && payload[1] == 0x01)
        {
            // Parse existing entries (skip 2-byte version header)
            int pos = 2;
            while (pos < payload.Length)
            {
                // Check for terminator
                if (payload[pos] == 0x00)
                    break;

                // Read null-terminated type string
                int typeStart = pos;
                while (pos < payload.Length && payload[pos] != 0x00)
                    pos++;
                if (pos >= payload.Length) break;
                var type = Encoding.ASCII.GetString(payload, typeStart, pos - typeStart);
                pos++; // skip null terminator

                // Read uint32 BE data length
                if (pos + 4 > payload.Length) break;
                int dataLen = (payload[pos] << 24) | (payload[pos + 1] << 16) |
                              (payload[pos + 2] << 8) | payload[pos + 3];
                pos += 4;

                // Read data bytes
                if (pos + dataLen > payload.Length) break;
                var data = new byte[dataLen];
                Array.Copy(payload, pos, data, 0, dataLen);
                pos += dataLen;

                // Keep everything except BPMLOCK (we'll re-add it)
                if (type != "BPMLOCK")
                    entries.Add((type, data));
            }
        }

        // Append BPMLOCK entry
        entries.Add(("BPMLOCK", [(byte)(locked ? 0x01 : 0x00)]));

        // Rebuild payload
        using var ms = new MemoryStream();
        // Version header
        ms.WriteByte(0x01);
        ms.WriteByte(0x01);
        // Entries
        foreach (var (type, data) in entries)
        {
            var typeBytes = Encoding.ASCII.GetBytes(type);
            ms.Write(typeBytes);
            ms.WriteByte(0x00); // null terminator
            // uint32 BE length
            ms.WriteByte((byte)((data.Length >> 24) & 0xFF));
            ms.WriteByte((byte)((data.Length >> 16) & 0xFF));
            ms.WriteByte((byte)((data.Length >> 8) & 0xFF));
            ms.WriteByte((byte)(data.Length & 0xFF));
            ms.Write(data);
        }
        // Terminator
        ms.WriteByte(0x00);

        return ms.ToArray();
    }
}
