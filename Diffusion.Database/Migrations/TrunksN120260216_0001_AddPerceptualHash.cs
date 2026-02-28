namespace Diffusion.Database
{
    public partial class Migrations
    {
        [Migrate(MigrationType.Pre)]
        private string TrunksN120260216_0001_AddPerceptualHash()
        {
            return "ALTER TABLE Image ADD COLUMN PerceptualHash INTEGER";
        }
    }
}
