using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VirtoCommerce.SiteBuilder.Data.MySql;
using VirtoCommerce.SiteBuilder.Data.PostgreSql;
using VirtoCommerce.SiteBuilder.Data.SqlServer;
using VirtoCommerce.Platform.Core.Modularity;
using VirtoCommerce.Platform.Data.MySql.Extensions;
using VirtoCommerce.Platform.Data.PostgreSql.Extensions;
using VirtoCommerce.Platform.Data.SqlServer.Extensions;

namespace VirtoCommerce.SiteBuilder.Data.Repositories;

internal static class SiteBuilderRepositoryRegistration
{
    internal static void AddSiteBuilderRepositories(
        this IServiceCollection serviceCollection,
        IConfiguration configuration,
        ManifestModuleInfo moduleInfo)
    {
        var databaseProvider = configuration.GetValue("DatabaseProvider", "SqlServer");
        serviceCollection.AddDbContextFactory<SiteBuilderDbContext>(options =>
        {
            var connectionString = configuration.GetConnectionString(moduleInfo.Id) ??
                                   configuration.GetConnectionString("VirtoCommerce");

            switch (databaseProvider)
            {
                case "MySql":
                    options.UseMySqlDatabase(connectionString, typeof(MySqlDataAssemblyMarker), configuration);
                    break;
                case "PostgreSql":
                    options.UsePostgreSqlDatabase(connectionString, typeof(PostgreSqlDataAssemblyMarker), configuration);
                    break;
                default:
                    options.UseSqlServerDatabase(connectionString, typeof(SqlServerDataAssemblyMarker), configuration);
                    break;
            }
        });

        serviceCollection.AddTransient<ISiteBuilderRepository, SiteBuilderRepository>();
        serviceCollection.AddSingleton<Func<ISiteBuilderRepository>>(provider =>
        {
            var dbContextFactory = provider.GetRequiredService<IDbContextFactory<SiteBuilderDbContext>>();
            return () => new SiteBuilderRepository(dbContextFactory.CreateDbContext());
        });

        serviceCollection.AddSingleton<Func<IContentStreamRepository>>(provider =>
        {
            var dbContextFactory = provider.GetRequiredService<IDbContextFactory<SiteBuilderDbContext>>();
            return () =>
            {
                var dbContext = dbContextFactory.CreateDbContext();
                return databaseProvider switch
                {
                    "MySql" => new MySqlContentStreamRepository(dbContext),
                    "PostgreSql" => new PostgreSqlContentStreamRepository(dbContext),
                    _ => new SqlServerContentStreamRepository(dbContext),
                };
            };
        });
    }

    internal static void MigrateSiteBuilderDatabase(this IServiceProvider serviceProvider)
    {
        using var dbContext = serviceProvider
            .GetRequiredService<IDbContextFactory<SiteBuilderDbContext>>()
            .CreateDbContext();
        dbContext.Database.Migrate();
    }
}
