using SQLite;

namespace Diffusion.Database.Models;

public class PromptTemplate
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }
    public string Name { get; set; }
    public string? Prompt { get; set; }
    public string? NegativePrompt { get; set; }
    public string? Category { get; set; }
    public string? Notes { get; set; }
    public string? Model { get; set; }
    public string? Sampler { get; set; }
    public decimal? CFGScale { get; set; }
    public int? Steps { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime? LastUsedDate { get; set; }
    public int UseCount { get; set; }
}
