using Gridder.Models;

namespace Gridder.Services;

public interface IBeatGridSerializer
{
    byte[] Serialize(BeatGrid beatGrid);
    BeatGrid Deserialize(byte[] data);
}
