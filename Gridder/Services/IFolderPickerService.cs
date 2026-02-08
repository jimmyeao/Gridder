namespace Gridder.Services;

public interface IFolderPickerService
{
    Task<string?> PickFolderAsync();
}
