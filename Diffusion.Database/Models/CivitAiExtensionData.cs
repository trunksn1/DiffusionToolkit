namespace Diffusion.Database.Models;

/// <summary>
/// Model representing data from the external CivitAI extension database (image2 table)
/// </summary>
public class CivitAiExtensionData
{
    public int ImageId { get; set; }
    public string? LoraRiforgiati { get; set; }
    public string? LoraInForge { get; set; }
    public string? ReforgedTags { get; set; }
    public string? LoraHashes { get; set; }
    public string? TiHashes { get; set; }
    public string? PicMetadata { get; set; }
    public string? Exif { get; set; }
    public string? Path { get; set; }

    /// <summary>
    /// Returns true if any data field has a value
    /// </summary>
    public bool HasData =>
        !string.IsNullOrEmpty(LoraRiforgiati) ||
        !string.IsNullOrEmpty(LoraInForge) ||
        !string.IsNullOrEmpty(ReforgedTags) ||
        !string.IsNullOrEmpty(LoraHashes) ||
        !string.IsNullOrEmpty(TiHashes) ||
        !string.IsNullOrEmpty(PicMetadata) ||
        !string.IsNullOrEmpty(Exif);
}
