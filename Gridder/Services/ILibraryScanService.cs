using Gridder.Models;

namespace Gridder.Services;

public interface ILibraryScanService
{
    Task<IReadOnlyList<AudioTrack>> ScanFolderAsync(string folderPath, CancellationToken ct = default);
}
