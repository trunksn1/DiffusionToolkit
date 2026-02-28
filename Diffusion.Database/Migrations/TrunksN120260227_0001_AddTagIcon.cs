namespace Diffusion.Database
{
    public partial class Migrations
    {
        [Migrate(MigrationType.Pre)]
        private string TrunksN120260227_0001_AddTagIcon()
        {
            return "ALTER TABLE Tag ADD COLUMN Icon TEXT";
        }
    }
}
