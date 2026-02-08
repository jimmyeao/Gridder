using Gridder.Helpers;
using Gridder.Models;
using static Gridder.Helpers.AppLogger;

namespace Gridder.Services;

/// <summary>
/// Serializes/deserializes the Serato BeatGrid binary format.
///
/// Format (Holzhaus/serato-tags spec):
///   [0x01]           - 1 byte version
///   [0x00]           - 1 byte padding
///   [uint32 BE]      - 4 bytes marker count
///   For each non-terminal marker (all except last):
///     [float32 BE]   - position in seconds
///     [uint32 BE]    - beats until next marker
///   Terminal marker (last):
///     [float32 BE]   - position in seconds
///     [float32 BE]   - BPM
///   [0x00]           - 1 byte footer
/// </summary>
public class BeatGridSerializer : IBeatGridSerializer
{
    public byte[] Serialize(BeatGrid beatGrid)
    {
        if (beatGrid.Markers.Count == 0)
            throw new ArgumentException("BeatGrid must have at least one marker.");

        using var ms = new MemoryStream();

        // Header: version + padding
        ms.WriteByte(0x01);
        ms.WriteByte(0x00);

        // Marker count (uint32 big-endian)
        ms.Write(BigEndianHelper.GetBytesBigEndian((uint)beatGrid.Markers.Count));

        for (int i = 0; i < beatGrid.Markers.Count; i++)
        {
            var marker = beatGrid.Markers[i];

            // Position (float32 big-endian)
            ms.Write(BigEndianHelper.GetBytesBigEndian((float)marker.PositionSeconds));

            if (marker.IsTerminal)
            {
                // BPM (float32 big-endian)
                ms.Write(BigEndianHelper.GetBytesBigEndian((float)marker.Bpm!.Value));
            }
            else
            {
                // Beats until next (uint32 big-endian)
                ms.Write(BigEndianHelper.GetBytesBigEndian((uint)(marker.BeatsUntilNext ?? 0)));
            }
        }

        // Footer
        ms.WriteByte(0x00);

        var result = ms.ToArray();
        Log("Serializer", $"Serialized {beatGrid.Markers.Count} markers into {result.Length} bytes");
        Log("Serializer", $"  Header: {result[0]:X2} {result[1]:X2}, Count bytes: {result[2]:X2} {result[3]:X2} {result[4]:X2} {result[5]:X2}");
        return result;
    }

    public BeatGrid Deserialize(byte[] data)
    {
        if (data.Length < 7)
            throw new ArgumentException("Data too short for Serato BeatGrid format.");

        var grid = new BeatGrid();
        var span = data.AsSpan();

        // Header
        byte version = data[0];
        if (version != 0x01)
            throw new FormatException($"Unsupported Serato BeatGrid version: 0x{version:X2}");

        // Marker count (uint32 at offset 2)
        uint count = BigEndianHelper.ReadUInt32BigEndian(span[2..]);
        int offset = 6;

        if (data.Length < 6 + (count * 8) + 1)
            throw new ArgumentException($"Data too short for {count} markers.");

        for (int i = 0; i < count; i++)
        {
            float position = BigEndianHelper.ReadFloatBigEndian(span[offset..]);
            offset += 4;

            if (i == count - 1)
            {
                // Terminal marker
                float bpm = BigEndianHelper.ReadFloatBigEndian(span[offset..]);
                offset += 4;
                grid.Markers.Add(new BeatGridMarker
                {
                    PositionSeconds = position,
                    Bpm = bpm,
                });
            }
            else
            {
                // Non-terminal marker
                uint beatsUntilNext = BigEndianHelper.ReadUInt32BigEndian(span[offset..]);
                offset += 4;
                grid.Markers.Add(new BeatGridMarker
                {
                    PositionSeconds = position,
                    BeatsUntilNext = (int)beatsUntilNext,
                });
            }
        }

        return grid;
    }
}
