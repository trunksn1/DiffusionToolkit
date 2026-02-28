using Diffusion.Database.Models;

namespace Diffusion.Database
{
    public partial class DataStore
    {
        public SmartAlbum CreateSmartAlbum(SmartAlbum smartAlbum)
        {
            using var db = OpenConnection();
            smartAlbum.CreatedDate = DateTime.Now;
            db.Insert(smartAlbum);
            smartAlbum.Id = db.CreateCommand("SELECT last_insert_rowid()").ExecuteScalar<int>();
            return smartAlbum;
        }

        public void UpdateSmartAlbum(SmartAlbum smartAlbum)
        {
            using var db = OpenConnection();
            lock (_lock)
            {
                db.Update(smartAlbum);
            }
        }

        public void DeleteSmartAlbum(int id)
        {
            using var db = OpenConnection();
            lock (_lock)
            {
                db.Delete<SmartAlbum>(id);
            }
        }

        public SmartAlbum? GetSmartAlbum(int id)
        {
            using var db = OpenConnection();
            return db.Find<SmartAlbum>(id);
        }

        public List<SmartAlbum> GetAllSmartAlbums()
        {
            using var db = OpenConnection();
            return db.Query<SmartAlbum>("SELECT * FROM SmartAlbum ORDER BY Name");
        }

        public List<ImageView> EvaluateSmartAlbum(string whereClause, Dictionary<string, object> parameters,
            int pageSize, int offset)
        {
            using var db = OpenConnection();
            var sql =
                $"SELECT Id, Favorite, ForDeletion, Rating, AestheticScore, Path, CreatedDate, NSFW, HasError, (SELECT COUNT(1) FROM AlbumImage AI WHERE AI.ImageId = Image.Id) AS AlbumCount, (SELECT GROUP_CONCAT(TagId) FROM ImageTag WHERE ImageId = Image.Id) AS TagIconIds FROM Image WHERE Unavailable = 0 AND {whereClause} ORDER BY CreatedDate DESC LIMIT @PageSize OFFSET @Offset";

            var cmd = db.CreateCommand(sql);
            foreach (var p in parameters)
            {
                cmd.Bind(p.Key, p.Value);
            }
            cmd.Bind("@PageSize", pageSize);
            cmd.Bind("@Offset", offset);

            return cmd.ExecuteQuery<ImageView>();
        }

        public int CountSmartAlbumResults(string whereClause, Dictionary<string, object> parameters)
        {
            using var db = OpenConnection();
            var sql = $"SELECT COUNT(1) FROM Image WHERE Unavailable = 0 AND {whereClause}";

            var cmd = db.CreateCommand(sql);
            foreach (var p in parameters)
            {
                cmd.Bind(p.Key, p.Value);
            }

            return cmd.ExecuteScalar<int>();
        }

        public void UpdateSmartAlbumLastEvaluated(int id)
        {
            using var db = OpenConnection();
            lock (_lock)
            {
                var cmd = db.CreateCommand("UPDATE SmartAlbum SET LastEvaluatedDate = ? WHERE Id = ?",
                    DateTime.Now, id);
                cmd.ExecuteNonQuery();
            }
        }
    }
}
