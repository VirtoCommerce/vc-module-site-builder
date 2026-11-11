using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using VirtoCommerce.SiteBuilder.Data.Repositories;

namespace VirtoCommerce.SiteBuilder.Data.PostgreSql;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SiteBuilderDbContext>
{
    public SiteBuilderDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<SiteBuilderDbContext>();
        var connectionString = args.Length != 0 ? args[0] : "Server=localhost;Username=virto;Password=virto;Database=VirtoCommerce3;";

        builder.UseNpgsql(
            connectionString,
            options => options.MigrationsAssembly(typeof(PostgreSqlDataAssemblyMarker).Assembly.GetName().Name));

        return new SiteBuilderDbContext(builder.Options);
    }
}
