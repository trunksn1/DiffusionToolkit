namespace Diffusion.Toolkit.Models;

public class CivitaiCollectionModel : BaseNotify
{
    public int Id
    {
        get;
        set => SetField(ref field, value);
    }

    public string Name
    {
        get;
        set => SetField(ref field, value);
    } = "";

    public bool Enabled
    {
        get;
        set => SetField(ref field, value);
    } = true;

    public int? ImageCount
    {
        get;
        set => SetField(ref field, value);
    }

    public string? FolderName
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>
    /// The name passed to Python as the collection name (used as folder name).
    /// Uses FolderName if set, otherwise falls back to Name.
    /// </summary>
    public string EffectiveFolderName => string.IsNullOrWhiteSpace(FolderName) ? Name : FolderName;
}
