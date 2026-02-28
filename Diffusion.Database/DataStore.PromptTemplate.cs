using Diffusion.Database.Models;

namespace Diffusion.Database
{
    public partial class DataStore
    {
        public PromptTemplate CreatePromptTemplate(PromptTemplate template)
        {
            using var db = OpenConnection();
            template.CreatedDate = DateTime.Now;
            db.Insert(template);
            template.Id = db.CreateCommand("SELECT last_insert_rowid()").ExecuteScalar<int>();
            return template;
        }

        public void UpdatePromptTemplate(PromptTemplate template)
        {
            using var db = OpenConnection();
            lock (_lock)
            {
                db.Update(template);
            }
        }

        public void DeletePromptTemplate(int id)
        {
            using var db = OpenConnection();
            lock (_lock)
            {
                db.Delete<PromptTemplate>(id);
            }
        }

        public PromptTemplate? GetPromptTemplate(int id)
        {
            using var db = OpenConnection();
            return db.Find<PromptTemplate>(id);
        }

        public List<PromptTemplate> GetAllPromptTemplates()
        {
            using var db = OpenConnection();
            return db.Query<PromptTemplate>("SELECT * FROM PromptTemplate ORDER BY Name");
        }

        public List<PromptTemplate> SearchPromptTemplates(string query)
        {
            using var db = OpenConnection();
            var search = $"%{query}%";
            return db.Query<PromptTemplate>(
                "SELECT * FROM PromptTemplate WHERE Name LIKE ? OR Prompt LIKE ? OR Category LIKE ? ORDER BY Name",
                search, search, search);
        }

        public List<PromptTemplate> GetPromptTemplatesByCategory(string category)
        {
            using var db = OpenConnection();
            return db.Query<PromptTemplate>(
                "SELECT * FROM PromptTemplate WHERE Category = ? ORDER BY Name", category);
        }

        public List<string> GetPromptTemplateCategories()
        {
            using var db = OpenConnection();
            var results = db.Query<CategoryResult>(
                "SELECT DISTINCT Category FROM PromptTemplate WHERE Category IS NOT NULL AND Category != '' ORDER BY Category");
            return results.Select(r => r.Category).ToList();
        }

        public void IncrementTemplateUseCount(int id)
        {
            using var db = OpenConnection();
            lock (_lock)
            {
                var cmd = db.CreateCommand(
                    "UPDATE PromptTemplate SET UseCount = UseCount + 1, LastUsedDate = ? WHERE Id = ?",
                    DateTime.Now, id);
                cmd.ExecuteNonQuery();
            }
        }
    }

    public class CategoryResult
    {
        public string Category { get; set; }
    }
}
