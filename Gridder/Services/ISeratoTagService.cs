using Gridder.Models;

namespace Gridder.Services;

public interface ISeratoTagService
{
    BeatGrid? ReadBeatGrid(string filePath);
    void WriteBeatGrid(string filePath, BeatGrid beatGrid);
    void SetBpmLock(string filePath, bool locked);
}
