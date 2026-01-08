using Diffusion.Database.Models;
using SQLite;

namespace Diffusion.Database;

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
