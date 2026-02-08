using Gridder.Models;

namespace Gridder.Services;

public interface ITrackMetadataService
{
    Task ReadMetadataAsync(AudioTrack track);
}
