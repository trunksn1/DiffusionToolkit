namespace Diffusion.Toolkit.Configuration;

public class CivitaiCollectionConfig
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int? ImageCount { get; set; }
    /// <summary>
    /// Folder name override. If set, used instead of the sanitized collection name.
    /// This ensures images go into existing folders (e.g., "Concepts--Styles" instead of "Concepts_Styles").
    /// </summary>
    public string? FolderName { get; set; }
}
