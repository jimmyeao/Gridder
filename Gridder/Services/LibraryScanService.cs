using Gridder.Models;

namespace Gridder.Services;

public class LibraryScanService : ILibraryScanService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac"
    };

    private readonly ITrackMetadataService _metadataService;

    public LibraryScanService(ITrackMetadataService metadataService)
    {
        _metadataService = metadataService;
    }

    public async Task<IReadOnlyList<AudioTrack>> ScanFolderAsync(string folderPath, CancellationToken ct = default)
    {
        if (!Directory.Exists(folderPath))
            return [];

        var audioFiles = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var tracks = new List<AudioTrack>(audioFiles.Count);

        foreach (var filePath in audioFiles)
        {
            ct.ThrowIfCancellationRequested();

            var track = new AudioTrack { FilePath = filePath };

            try
            {
                await _metadataService.ReadMetadataAsync(track);
            }
            catch
            {
                // If metadata reading fails, keep the track with just the file path
                track.Title = Path.GetFileNameWithoutExtension(filePath);
            }

            tracks.Add(track);
        }

        return tracks;
    }
}
