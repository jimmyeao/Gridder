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
                return frame.Data.Data;
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

        // Decode base64 (may contain linefeeds)
        base64 = base64.Replace("\n", "").Replace("\r", "");
        var fullData = Convert.FromBase64String(base64);

        // Strip the "application/octet-stream\0\0" prefix
        // Find the double null after the mime type
        var mimeType = "application/octet-stream";
        int prefixLen = mimeType.Length + 2; // mime + null + null

        if (fullData.Length <= prefixLen)
            return null;

        var beatGridData = new byte[fullData.Length - prefixLen];
        Array.Copy(fullData, prefixLen, beatGridData, 0, beatGridData.Length);
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

        // Add new frame
        var newFrame = new TagLib.Id3v2.AttachmentFrame
        {
            Type = TagLib.PictureType.NotAPicture,
            Description = "Serato BeatGrid",
            MimeType = "application/octet-stream",
            Data = new TagLib.ByteVector(data),
        };
        id3Tag.AddFrame(newFrame);
    }

    private static void WriteToVorbis(TagLib.File file, byte[] data)
    {
        var xiph = (TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph, true);

        // Build the full payload: mime_type + null + null + binary_data
        var mimeBytes = Encoding.ASCII.GetBytes("application/octet-stream");
        var fullData = new byte[mimeBytes.Length + 2 + data.Length];
        mimeBytes.CopyTo(fullData, 0);
        fullData[mimeBytes.Length] = 0;
        fullData[mimeBytes.Length + 1] = 0;
        data.CopyTo(fullData, mimeBytes.Length + 2);

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
}
