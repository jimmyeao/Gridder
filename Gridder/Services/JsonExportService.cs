using System.Text.Json;
using Gridder.Models;

namespace Gridder.Services;

public class JsonExportService
{
    public async Task ExportAsync(AudioTrack track, string outputPath)
    {
        var export = new
        {
            filePath = track.FilePath,
            title = track.Title,
            artist = track.Artist,
            durationSeconds = track.Duration.TotalSeconds,
            markers = track.BeatGrid?.Markers.Select(m => new
            {
                positionSeconds = m.PositionSeconds,
                beatsUntilNext = m.BeatsUntilNext,
                bpm = m.Bpm,
                isTerminal = m.IsTerminal,
            }),
            allBeatPositions = track.BeatGrid?.AllBeatPositions,
        };

        var json = JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(outputPath, json);
    }
}
