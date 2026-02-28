using SQLite;

namespace Diffusion.Database.Models;

public class Tag
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }
    public string Name { get; set; }
    public string? Icon { get; set; }
}

public class TagCount
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string? Icon { get; set; }
    public int Count { get; set; }
}
