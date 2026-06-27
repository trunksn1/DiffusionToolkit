using SQLite;

namespace Diffusion.Database.Models;

/// <summary>
/// A non-destructive, user-supplied metadata overlay entry.
/// Keyed by the image file's SHA-256 hash (not Image.Id or Path) so it survives
/// rescans, file moves/renames and full database rebuilds. Never written into the
/// scanner-owned Image columns or into the image file itself.
/// </summary>
public class UserMetadata
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed(Name = "IDX_UserMetadata", Order = 1, Unique = true)]
    public string FileHash { get; set; }

    [Indexed(Name = "IDX_UserMetadata", Order = 2, Unique = true)]
    public string Key { get; set; }

    public string? Value { get; set; }

    /// <summary>"manual" or "civitai".</summary>
    public string Source { get; set; }

    /// <summary>Origin URL when the entry was fetched (e.g. a Civitai image page).</summary>
    public string? SourceUrl { get; set; }

    public DateTime CreatedDate { get; set; }
}
