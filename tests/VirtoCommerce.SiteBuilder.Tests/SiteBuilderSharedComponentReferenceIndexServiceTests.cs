using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using VirtoCommerce.SiteBuilder.Data.Models;
using VirtoCommerce.SiteBuilder.Data.Repositories;
using VirtoCommerce.SiteBuilder.Data.Services;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

public class SiteBuilderSharedComponentReferenceIndexServiceTests
{
    [Fact]
    public void ValidateComponentReferences_RejectsMetadataOrphanWithoutContentRow()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            SiteBuilderSharedComponentReferenceValidator.Validate(
                ["component"],
                "store",
                new Dictionary<string, string> { ["component"] = "store" },
                new HashSet<string>()));

        Assert.Contains("has no content", exception.Message);
    }

    [Fact]
    public async Task ValidateReferencesForStoreAsync_ValidatesVariantUnionWithoutNPlusOneQueries()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var counter = new CommandCounter();
        var options = new DbContextOptionsBuilder<SiteBuilderDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(counter)
            .Options;
        await using (var setupContext = new SiteBuilderDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            setupContext.AddRange(
                CreateComponent("component-a"),
                CreateComponent("component-b"));
            await setupContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        counter.Reset();
        var service = new SiteBuilderSharedComponentReferenceIndexService(
            () => new SiteBuilderRepository(new SiteBuilderDbContext(options)));
        var contents = Enumerable.Range(0, 100)
            .Select(index =>
                $"{{ \"settings\": {{}}, \"content\": [" +
                $"{{ \"id\": \"placement-a-{index}\", \"type\": \"componentRef\", \"componentRef\": \"component-a\" }}," +
                $"{{ \"id\": \"placement-b-{index}\", \"type\": \"componentRef\", \"componentRef\": \"component-b\" }}] }}")
            .ToArray();

        await service.ValidateReferencesForStoreAsync(
            "store",
            contents,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, counter.ReaderCommandCount);
    }

    private static SiteBuilderSharedComponentEntity CreateComponent(string id)
    {
        return new SiteBuilderSharedComponentEntity
        {
            Id = id,
            StoreId = "store",
            Name = id,
            CreatedDate = DateTime.UtcNow,
            Content = new SiteBuilderSharedComponentContentEntity
            {
                Id = id,
                ComponentContent = "{ \"settings\": {}, \"content\": [] }",
            },
        };
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int ReaderCommandCount { get; private set; }

        public void Reset() => ReaderCommandCount = 0;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            ReaderCommandCount++;
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ReaderCommandCount++;
            return ValueTask.FromResult(result);
        }
    }
}
