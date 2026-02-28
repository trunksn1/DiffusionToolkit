namespace Diffusion.Database
{
    public partial class Migrations
    {
        [Migrate(MigrationType.Pre)]
        private string TrunksN120260216_0002_AddPromptTemplate()
        {
            return @"
                CREATE TABLE IF NOT EXISTS PromptTemplate (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    Prompt TEXT,
                    NegativePrompt TEXT,
                    Category TEXT,
                    Notes TEXT,
                    Model TEXT,
                    Sampler TEXT,
                    CFGScale REAL,
                    Steps INTEGER,
                    CreatedDate TEXT,
                    LastUsedDate TEXT,
                    UseCount INTEGER DEFAULT 0
                )";
        }
    }
}
