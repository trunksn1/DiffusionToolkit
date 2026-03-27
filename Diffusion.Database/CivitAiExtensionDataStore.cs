using Diffusion.Database.Models;
using SQLite;

namespace Diffusion.Database;

public class PathResult
{
    public string? Path { get; set; }
}

/// <summary>
/// Data store for querying the external CivitAI extension database
/// </summary>
public class CivitAiExtensionDataStore
{
    private readonly string _databasePath;
    private bool _isAvailable;

    public bool IsAvailable => _isAvailable;
    public string DatabasePath => _databasePath;

    public CivitAiExtensionDataStore(string databasePath)
    {
        _databasePath = databasePath;
        _isAvailable = File.Exists(databasePath);
    }

    private SQLiteConnection OpenReadOnlyConnection()
    {
        return new SQLiteConnection(_databasePath, SQLiteOpenFlags.ReadOnly);
    }

    private SQLiteConnection OpenWriteConnection()
    {
        return new SQLiteConnection(_databasePath, SQLiteOpenFlags.ReadWrite);
    }

    /// <summary>
    /// Gets CivitAI extension data for an image by its path
    /// </summary>
    /// <param name="imagePath">The full path to the image file</param>
    /// <returns>CivitAiExtensionData if found, null otherwise</returns>
    public CivitAiExtensionData? GetByPath(string imagePath)
    {
        if (!_isAvailable)
            return null;

        try
        {
            using var connection = OpenReadOnlyConnection();

            var sql = @"
                SELECT
                    image_id AS ImageId,
                    lora_riforgiati AS LoraRiforgiati,
                    lora_in_forge AS LoraInForge,
                    reforged_tags AS ReforgedTags,
                    Lora_hashes AS LoraHashes,
                    TI_hashes AS TiHashes,
                    PicMetadata,
                    Exif,
                    Path
                FROM Image2
                WHERE Path = ?
                LIMIT 1";

            var results = connection.Query<CivitAiExtensionData>(sql, imagePath);
            return results.FirstOrDefault();
        }
        catch (Exception)
        {
            // If there's any error accessing the external database, return null
            return null;
        }
    }

    /// <summary>
    /// Checks if the external database is accessible
    /// </summary>
    public bool CheckAvailability()
    {
        _isAvailable = File.Exists(_databasePath);

        if (!_isAvailable)
            return false;

        try
        {
            using var connection = OpenReadOnlyConnection();
            // Try a simple query to verify the database is accessible
            connection.ExecuteScalar<int>("SELECT 1 FROM Image2 LIMIT 1");
            return true;
        }
        catch
        {
            _isAvailable = false;
            return false;
        }
    }

    /// <summary>
    /// Searches for paths matching CivitAI filter criteria.
    /// Returns paths from Image2 that match the given conditions.
    /// Text matching:
    ///   - Empty = has any non-empty data
    ///   - "=value" = exact match
    ///   - "value" = contains (auto-wrapped with %)
    ///   - "val*ue" = custom wildcard pattern (* → %)
    /// </summary>
    public List<string> SearchPaths(bool? hasAnyData, string? loraRiforgiati, string? loraInForge, string? reforgedTags)
    {
        if (!_isAvailable)
            return new List<string>();

        try
        {
            using var connection = OpenReadOnlyConnection();

            var conditions = new List<string>();
            var parameters = new List<object>();

            AddFieldCondition(conditions, parameters, "lora_riforgiati", loraRiforgiati);
            AddFieldCondition(conditions, parameters, "lora_in_forge", loraInForge);
            AddFieldCondition(conditions, parameters, "reforged_tags", reforgedTags);

            var where = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : "";
            var sql = $"SELECT Path FROM Image2{where}";

            var results = connection.Query<PathResult>(sql, parameters.ToArray());
            return results.Select(r => r.Path).Where(p => p != null).ToList()!;
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    private static void AddFieldCondition(List<string> conditions, List<object> parameters, string column, string? value)
    {
        if (value == null) return;

        if (string.IsNullOrWhiteSpace(value))
        {
            // Empty = has any non-empty data
            conditions.Add($"({column} IS NOT NULL AND {column} != '')");
            return;
        }

        // Comma-separated tokens are OR-combined, each token follows the same rules:
        //   =value  → exact match
        //   val*ue  → wildcard (* → %)
        //   value   → contains (auto-wrapped with %)
        var tokens = value.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

        var orClauses = new List<string>();
        foreach (var token in tokens)
        {
            if (token.StartsWith("="))
            {
                orClauses.Add($"{column} = ?");
                parameters.Add(token.Substring(1));
            }
            else if (token.Contains('*'))
            {
                orClauses.Add($"{column} LIKE ?");
                parameters.Add(token.Replace("*", "%"));
            }
            else
            {
                orClauses.Add($"{column} LIKE ?");
                parameters.Add($"%{token}%");
            }
        }

        if (orClauses.Count == 1)
            conditions.Add($"({orClauses[0]})");
        else
            conditions.Add("(" + string.Join(" OR ", orClauses) + ")");
    }

    /// <summary>
    /// Returns all paths in the external database (for "has CivitAI data" filter)
    /// </summary>
    public List<string> GetAllPaths()
    {
        if (!_isAvailable)
            return new List<string>();

        try
        {
            using var connection = OpenReadOnlyConnection();
            var results = connection.Query<PathResult>("SELECT Path FROM Image2");
            return results.Select(r => r.Path).Where(p => p != null).ToList()!;
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Updates the lora_riforgiati field for an image by its path
    /// </summary>
    /// <param name="imagePath">The full path to the image file</param>
    /// <param name="loraRiforgiati">The new value for lora_riforgiati</param>
    /// <returns>True if update was successful, false otherwise</returns>
    public bool UpdateLoraRiforgiati(string imagePath, string? loraRiforgiati)
    {
        if (!_isAvailable)
            return false;

        try
        {
            using var connection = OpenWriteConnection();

            var sql = "UPDATE Image2 SET lora_riforgiati = ? WHERE Path = ?";
            connection.Execute(sql, loraRiforgiati, imagePath);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
