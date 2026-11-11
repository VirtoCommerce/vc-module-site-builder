using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using VirtoCommerce.SiteBuilder.Data.Repositories;

namespace VirtoCommerce.SiteBuilder.Data.SqlServer;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SiteBuilderDbContext>
{
    public SiteBuilderDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<SiteBuilderDbContext>();
        var connectionString = args.Length != 0 ? args[0] : "Server=(local);User=virto;Password=virto;Database=VirtoCommerce3;";

        builder.UseSqlServer(
            connectionString,
            options => options.MigrationsAssembly(typeof(SqlServerDataAssemblyMarker).Assembly.GetName().Name));

        return new SiteBuilderDbContext(builder.Options);
    }
}
