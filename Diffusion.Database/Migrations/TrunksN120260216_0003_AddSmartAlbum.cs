namespace Diffusion.Database
{
    public partial class Migrations
    {
        [Migrate(MigrationType.Pre)]
        private string TrunksN120260216_0003_AddSmartAlbum()
        {
            return @"
                CREATE TABLE IF NOT EXISTS SmartAlbum (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    RulesJson TEXT NOT NULL,
                    MatchAll INTEGER DEFAULT 1,
                    CreatedDate TEXT,
                    LastEvaluatedDate TEXT
                )";
        }
    }
}
