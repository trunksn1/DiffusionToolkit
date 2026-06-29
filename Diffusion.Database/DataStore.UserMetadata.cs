using System.Text.RegularExpressions;
using Diffusion.Database.Models;

namespace Diffusion.Database
{
    public partial class DataStore
    {
        private static readonly Regex CivIdInPathRegex = new Regex("CIV_ID__(?<id>\\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex CivIdInUrlRegex = new Regex("/images/(?<id>\\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Persists the SHA-256 hash onto the scanner-owned Image row so the user-metadata overlay
        /// (keyed by file hash) can be reached from SQL search. Safe: the value is the real file hash.
        /// </summary>
        public void SetImageHash(int imageId, string hash)
        {
            if (imageId <= 0 || string.IsNullOrEmpty(hash)) return;

            using var db = OpenConnection();

            var command = db.CreateCommand($"UPDATE {nameof(Image)} SET Hash = ? WHERE Id = ?", hash, imageId);

            lock (_lock)
            {
                command.ExecuteNonQuery();
            }

            db.Close();
        }

        /// <summary>
        /// Fills <see cref="Image.Hash"/> for the row at the given path when it is currently empty, so
        /// the overlay (keyed by file hash) is reachable from SQL search. No-op if a hash already exists.
        /// </summary>
        public void SetImageHashByPath(string path, string hash)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(hash)) return;

            using var db = OpenConnection();

            var command = db.CreateCommand(
                $"UPDATE {nameof(Image)} SET Hash = ? WHERE Path = ? AND (Hash IS NULL OR Hash = '')", hash, path);

            lock (_lock)
            {
                command.ExecuteNonQuery();
            }

            db.Close();
        }

        /// <summary>
        /// Links existing CivitAI-sourced overlay entries to their images by writing the overlay's
        /// file hash into <see cref="Image.Hash"/>, matching the image's <c>CIV_ID__{id}</c> filename
        /// marker against the overlay's SourceUrl (<c>/images/{id}</c>). Pure database work — no file
        /// hashing. Only fills images that currently have no hash. Returns the number of images linked.
        /// </summary>
        public int IndexUserMetadataForSearch()
        {
            // civId -> overlay file hash (from CivitAI-sourced rows that carry a SourceUrl)
            var byCivId = new Dictionary<string, string>();

            {
                var db = OpenReadonlyConnection();

                var overlayRows = db.Query<UserMetadata>(
                    $"SELECT DISTINCT FileHash, SourceUrl FROM {nameof(UserMetadata)} " +
                    "WHERE SourceUrl IS NOT NULL AND SourceUrl <> '' AND FileHash IS NOT NULL AND FileHash <> ''");

                foreach (var row in overlayRows)
                {
                    var m = CivIdInUrlRegex.Match(row.SourceUrl ?? string.Empty);
                    if (m.Success)
                    {
                        byCivId[m.Groups["id"].Value] = row.FileHash;
                    }
                }
            }

            if (byCivId.Count == 0) return 0;

            // Images that still have no hash but carry the CIV_ID marker
            var updates = new List<(int Id, string Hash)>();

            {
                var db = OpenReadonlyConnection();

                var images = db.Query<Image>(
                    $"SELECT Id, Path FROM {nameof(Image)} " +
                    "WHERE (Hash IS NULL OR Hash = '') AND instr(Path, 'CIV_ID__') > 0");

                foreach (var image in images)
                {
                    var m = CivIdInPathRegex.Match(image.Path ?? string.Empty);
                    if (m.Success && byCivId.TryGetValue(m.Groups["id"].Value, out var hash))
                    {
                        updates.Add((image.Id, hash));
                    }
                }
            }

            if (updates.Count == 0) return 0;

            using (var db = OpenConnection())
            {
                lock (_lock)
                {
                    db.BeginTransaction();

                    foreach (var (id, hash) in updates)
                    {
                        var command = db.CreateCommand($"UPDATE {nameof(Image)} SET Hash = ? WHERE Id = ?", hash, id);
                        command.ExecuteNonQuery();
                    }

                    db.Commit();
                }

                db.Close();
            }

            return updates.Count;
        }

        /// <summary>
        /// Returns all user-supplied overlay metadata for a given file hash, ordered by key.
        /// </summary>
        public List<UserMetadata> GetUserMetadata(string fileHash)
        {
            if (string.IsNullOrEmpty(fileHash))
            {
                return new List<UserMetadata>();
            }

            var db = OpenReadonlyConnection();

            return db.Query<UserMetadata>(
                $"SELECT Id, FileHash, Key, Value, Source, SourceUrl, CreatedDate FROM {nameof(UserMetadata)} WHERE FileHash = ? ORDER BY Key",
                fileHash);
        }

        /// <summary>
        /// Returns available images that were downloaded from CivitAI (filename contains the
        /// <c>CIV_ID__</c> marker) and have an empty prompt — candidates for fetching generation
        /// data from CivitAI into the overlay.
        /// </summary>
        public List<Image> GetCivitaiBackfillCandidates()
        {
            var db = OpenReadonlyConnection();

            return db.Query<Image>(
                $"SELECT Id, Path, Prompt FROM {nameof(Image)} " +
                "WHERE (Prompt IS NULL OR TRIM(Prompt) = '') " +
                "AND instr(Path, 'CIV_ID__') > 0 " +
                "AND Unavailable = 0");
        }

        /// <summary>
        /// Inserts or updates a single overlay entry for (fileHash, key).
        /// </summary>
        public void UpsertUserMetadata(string fileHash, string key, string? value, string source, string? sourceUrl)
        {
            using var db = OpenConnection();

            var query = $"INSERT INTO {nameof(UserMetadata)} (FileHash, Key, Value, Source, SourceUrl, CreatedDate) " +
                        "VALUES (@FileHash, @Key, @Value, @Source, @SourceUrl, @CreatedDate) " +
                        "ON CONFLICT (FileHash, Key) DO UPDATE SET Value = @Value, Source = @Source, SourceUrl = @SourceUrl";

            var command = db.CreateCommand(query);

            command.Bind("@FileHash", fileHash);
            command.Bind("@Key", key);
            command.Bind("@Value", value);
            command.Bind("@Source", source);
            command.Bind("@SourceUrl", sourceUrl);
            command.Bind("@CreatedDate", DateTime.Now);

            lock (_lock)
            {
                command.ExecuteNonQuery();
            }

            db.Close();
        }

        /// <summary>
        /// Removes a single overlay entry identified by (fileHash, key).
        /// </summary>
        public void DeleteUserMetadata(string fileHash, string key)
        {
            using var db = OpenConnection();

            var query = $"DELETE FROM {nameof(UserMetadata)} WHERE FileHash = @FileHash AND Key = @Key";

            var command = db.CreateCommand(query);

            command.Bind("@FileHash", fileHash);
            command.Bind("@Key", key);

            lock (_lock)
            {
                command.ExecuteNonQuery();
            }

            db.Close();
        }

        /// <summary>
        /// Removes all overlay entries for a given file hash.
        /// </summary>
        public void DeleteAllUserMetadata(string fileHash)
        {
            using var db = OpenConnection();

            var query = $"DELETE FROM {nameof(UserMetadata)} WHERE FileHash = @FileHash";

            var command = db.CreateCommand(query);

            command.Bind("@FileHash", fileHash);

            lock (_lock)
            {
                command.ExecuteNonQuery();
            }

            db.Close();
        }

        /// <summary>
        /// Returns every overlay entry in the database (used for export).
        /// </summary>
        public List<UserMetadata> GetAllUserMetadata()
        {
            var db = OpenReadonlyConnection();

            return db.Query<UserMetadata>(
                $"SELECT Id, FileHash, Key, Value, Source, SourceUrl, CreatedDate FROM {nameof(UserMetadata)} ORDER BY FileHash, Key");
        }

        /// <summary>
        /// Bulk-imports overlay entries, upserting on (FileHash, Key). Wrapped in a single transaction.
        /// </summary>
        public void ImportUserMetadata(IEnumerable<UserMetadata> entries)
        {
            using var db = OpenConnection();

            var query = $"INSERT INTO {nameof(UserMetadata)} (FileHash, Key, Value, Source, SourceUrl, CreatedDate) " +
                        "VALUES (@FileHash, @Key, @Value, @Source, @SourceUrl, @CreatedDate) " +
                        "ON CONFLICT (FileHash, Key) DO UPDATE SET Value = @Value, Source = @Source, SourceUrl = @SourceUrl, CreatedDate = @CreatedDate";

            lock (_lock)
            {
                db.BeginTransaction();

                foreach (var entry in entries)
                {
                    if (string.IsNullOrEmpty(entry.FileHash) || string.IsNullOrEmpty(entry.Key))
                    {
                        continue;
                    }

                    var command = db.CreateCommand(query);

                    command.Bind("@FileHash", entry.FileHash);
                    command.Bind("@Key", entry.Key);
                    command.Bind("@Value", entry.Value);
                    command.Bind("@Source", string.IsNullOrEmpty(entry.Source) ? "manual" : entry.Source);
                    command.Bind("@SourceUrl", entry.SourceUrl);
                    command.Bind("@CreatedDate", entry.CreatedDate == default ? DateTime.Now : entry.CreatedDate);

                    command.ExecuteNonQuery();
                }

                db.Commit();
            }

            db.Close();
        }
    }
}
