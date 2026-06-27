using Diffusion.Database.Models;

namespace Diffusion.Database
{
    public partial class DataStore
    {
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
