using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Diffusion.Database.Models;
using Diffusion.IO;
using Diffusion.Toolkit.Models;

namespace Diffusion.Toolkit.Services;

/// <summary>
/// Manages the non-destructive, user-supplied metadata overlay. Overlay rows are stored in a
/// dedicated table keyed by the file's SHA-256 hash and are never written into the image file
/// or the scanner-owned Image columns.
/// </summary>
public class UserMetadataService
{
    private const string ManualSource = "manual";

    /// <summary>
    /// Resolves the SHA-256 hash for an image, using the value already loaded with the preview
    /// when present, otherwise computing it from the file (caller should invoke off the UI thread).
    /// Returns null when the file is missing.
    /// </summary>
    public string? ResolveHash(ImageViewModel image)
    {
        if (image == null) return null;

        if (!string.IsNullOrEmpty(image.Hash))
        {
            return image.Hash;
        }

        if (string.IsNullOrEmpty(image.Path) || !File.Exists(image.Path))
        {
            return null;
        }

        var hash = HashFunctions.CalculateSHA256(image.Path);
        image.Hash = hash;
        return hash;
    }

    /// <summary>
    /// Loads the overlay rows for an image as display view models. Runs the (potentially file-hashing)
    /// work on a background thread.
    /// </summary>
    public Task<List<UserMetadataItemViewModel>> LoadForImageAsync(ImageViewModel image)
    {
        return Task.Run(() =>
        {
            var result = new List<UserMetadataItemViewModel>();

            var hash = ResolveHash(image);
            if (string.IsNullOrEmpty(hash))
            {
                return result;
            }

            var rows = ServiceLocator.DataStore.GetUserMetadata(hash);

            foreach (var row in rows)
            {
                result.Add(new UserMetadataItemViewModel
                {
                    Key = row.Key,
                    Value = row.Value,
                    Source = string.IsNullOrEmpty(row.Source) ? ManualSource : row.Source,
                    SourceUrl = row.SourceUrl
                });
            }

            return result;
        });
    }

    /// <summary>
    /// Inserts or updates a single overlay entry. Returns false when the hash could not be resolved.
    /// </summary>
    public bool Save(ImageViewModel image, string key, string? value, string source = ManualSource, string? sourceUrl = null)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        var hash = ResolveHash(image);
        if (string.IsNullOrEmpty(hash)) return false;

        ServiceLocator.DataStore.UpsertUserMetadata(hash, key.Trim(), value, source, sourceUrl);
        return true;
    }

    public void Delete(ImageViewModel image, string key)
    {
        var hash = ResolveHash(image);
        if (string.IsNullOrEmpty(hash)) return;

        ServiceLocator.DataStore.DeleteUserMetadata(hash, key);
    }

    public void DeleteAll(ImageViewModel image)
    {
        var hash = ResolveHash(image);
        if (string.IsNullOrEmpty(hash)) return;

        ServiceLocator.DataStore.DeleteAllUserMetadata(hash);
    }

    /// <summary>
    /// Exports every overlay entry to an indented JSON array.
    /// </summary>
    public Task ExportAsync(string path)
    {
        return Task.Run(() =>
        {
            var rows = ServiceLocator.DataStore.GetAllUserMetadata();

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(rows, options);

            File.WriteAllText(path, json);

            return rows.Count;
        });
    }

    /// <summary>
    /// Imports overlay entries from a JSON array produced by <see cref="ExportAsync"/>, upserting
    /// on (FileHash, Key). Returns the number of entries imported.
    /// </summary>
    public Task<int> ImportAsync(string path)
    {
        return Task.Run(() =>
        {
            if (!File.Exists(path)) return 0;

            var json = File.ReadAllText(path);

            var rows = JsonSerializer.Deserialize<List<UserMetadata>>(json) ?? new List<UserMetadata>();

            var valid = rows
                .Where(r => !string.IsNullOrEmpty(r.FileHash) && !string.IsNullOrEmpty(r.Key))
                .ToList();

            if (valid.Count > 0)
            {
                ServiceLocator.DataStore.ImportUserMetadata(valid);
            }

            return valid.Count;
        });
    }
}
