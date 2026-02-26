using System.Numerics;
using Diffusion.Database.Models;

namespace Diffusion.Database
{
    public class PerceptualHashEntry
    {
        public int Id { get; set; }
        public long PerceptualHash { get; set; }
    }

    public class DuplicateResult
    {
        public int Id { get; set; }
        public string Path { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public long FileSize { get; set; }
        public long PerceptualHash { get; set; }
        public DateTime CreatedDate { get; set; }
        public int? Rating { get; set; }
        public bool Favorite { get; set; }
    }

    public partial class DataStore
    {
        public List<PerceptualHashEntry> GetAllPerceptualHashes()
        {
            using var db = OpenConnection();
            return db.Query<PerceptualHashEntry>(
                "SELECT Id, PerceptualHash FROM Image WHERE PerceptualHash IS NOT NULL AND Unavailable = 0 AND ForDeletion = 0");
        }

        /// <summary>
        /// Two-pass find: first loads only Id+Hash (lightweight), filters by Hamming distance,
        /// then loads full details only for matching IDs.
        /// </summary>
        public List<DuplicateResult> FindSimilarImages(long hash, int maxDistance, int excludeId = 0)
        {
            // Pass 1: lightweight hash comparison
            var allHashes = GetAllPerceptualHashes();
            var matchingIds = new List<int>();

            foreach (var entry in allHashes)
            {
                if (entry.Id == excludeId)
                    continue;

                var distance = HammingDistance(hash, entry.PerceptualHash);
                if (distance <= maxDistance)
                {
                    matchingIds.Add(entry.Id);
                }
            }

            if (matchingIds.Count == 0)
                return new List<DuplicateResult>();

            // Pass 2: load full details only for matches
            return GetImageDetails(matchingIds);
        }

        /// <summary>
        /// Loads full DuplicateResult records for a set of image IDs.
        /// Batches queries to stay within SQLite's variable limit.
        /// </summary>
        public List<DuplicateResult> GetImageDetails(List<int> ids, Action<int, int>? onProgress = null)
        {
            if (ids.Count == 0)
                return new List<DuplicateResult>();

            using var db = OpenConnection();
            var results = new List<DuplicateResult>();

            // SQLite has a default variable limit of 999, batch to stay safe
            const int batchSize = 900;
            int totalBatches = (ids.Count + batchSize - 1) / batchSize;
            int currentBatch = 0;

            for (int i = 0; i < ids.Count; i += batchSize)
            {
                var batch = ids.Skip(i).Take(batchSize).ToList();
                var placeholders = string.Join(",", batch.Select(_ => "?"));
                var sql = $"SELECT Id, Path, Width, Height, FileSize, PerceptualHash, CreatedDate, Rating, Favorite FROM Image WHERE Id IN ({placeholders})";
                results.AddRange(db.Query<DuplicateResult>(sql, batch.Cast<object>().ToArray()));

                currentBatch++;
                onProgress?.Invoke(currentBatch, totalBatches);
            }

            return results;
        }

        public List<int> GetImagesWithoutPerceptualHash(int batchSize)
        {
            using var db = OpenConnection();
            return db.Query<ImageIdOnly>(
                "SELECT Id FROM Image WHERE PerceptualHash IS NULL AND Unavailable = 0 AND Type = 0 LIMIT ?",
                batchSize).Select(x => x.Id).ToList();
        }

        public int CountImagesWithoutPerceptualHash()
        {
            using var db = OpenConnection();
            var cmd = db.CreateCommand("SELECT COUNT(1) FROM Image WHERE PerceptualHash IS NULL AND Unavailable = 0 AND Type = 0");
            return cmd.ExecuteScalar<int>();
        }

        public string? GetImagePath(int id)
        {
            using var db = OpenConnection();
            var cmd = db.CreateCommand("SELECT Path FROM Image WHERE Id = ?", id);
            return cmd.ExecuteScalar<string>();
        }

        public void UpdatePerceptualHash(int imageId, long hash)
        {
            using var db = OpenConnection();
            lock (_lock)
            {
                var cmd = db.CreateCommand("UPDATE Image SET PerceptualHash = ? WHERE Id = ?", hash, imageId);
                cmd.ExecuteNonQuery();
            }
        }

        public void UpdatePerceptualHashes(IEnumerable<(int Id, long Hash)> hashes)
        {
            using var db = OpenConnection();
            lock (_lock)
            {
                db.BeginTransaction();
                foreach (var (id, hash) in hashes)
                {
                    var cmd = db.CreateCommand("UPDATE Image SET PerceptualHash = ? WHERE Id = ?", hash, id);
                    cmd.ExecuteNonQuery();
                }
                db.Commit();
            }
        }

        private static int HammingDistance(long hash1, long hash2)
        {
            return BitOperations.PopCount(unchecked((ulong)(hash1 ^ hash2)));
        }
    }

    public class ImageIdOnly
    {
        public int Id { get; set; }
    }
}
