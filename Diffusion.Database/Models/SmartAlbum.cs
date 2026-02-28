using SQLite;

namespace Diffusion.Database.Models;

public class SmartAlbum
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }
    public string Name { get; set; }
    public string RulesJson { get; set; }
    public bool MatchAll { get; set; } = true;
    public DateTime CreatedDate { get; set; }
    public DateTime? LastEvaluatedDate { get; set; }
}
