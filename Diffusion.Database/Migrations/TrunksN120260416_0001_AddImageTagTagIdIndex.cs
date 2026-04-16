namespace Diffusion.Database
{
    public partial class Migrations
    {
        [Migrate(MigrationType.Pre)]
        private string TrunksN120260416_0001_AddImageTagTagIdIndex()
        {
            return "CREATE INDEX IF NOT EXISTS IX_ImageTag_TagId ON ImageTag(TagId)";
        }
    }
}
