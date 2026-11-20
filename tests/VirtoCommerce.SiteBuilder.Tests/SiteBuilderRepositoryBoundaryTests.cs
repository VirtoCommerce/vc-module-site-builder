using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.ContentProviders;
using VirtoCommerce.SiteBuilder.Data.Repositories;
using VirtoCommerce.SiteBuilder.Data.Services;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

public class SiteBuilderRepositoryBoundaryTests
{
    [Fact]
    public void ContentStreamRepository_HasSingleRepositoryContract()
    {
        var repositoryContracts = typeof(ContentStreamRepository).GetInterfaces()
            .Where(type => type != typeof(IAsyncDisposable))
            .ToArray();

        Assert.Equal([typeof(IContentStreamRepository)], repositoryContracts);
    }

    [Fact]
    public void ContentStreamRepository_DoesNotExposeRawWriteOperations()
    {
        var methodNames = typeof(IContentStreamRepository).GetMethods()
            .Select(method => method.Name)
            .ToArray();

        Assert.DoesNotContain("SaveBinaryAsync", methodNames);
        Assert.DoesNotContain("SaveRawContentAsync", methodNames);
        Assert.DoesNotContain("CopyContentAsync", methodNames);
    }

    [Fact]
    public void ServiceCompatibilityMethods_DoNotHaveDefaultImplementations()
    {
        var methods = new[]
        {
            typeof(IGroupedPageService).GetMethod(nameof(IGroupedPageService.TryDeleteEmptyDraftAsync)),
            typeof(ISiteBuilderSharedComponentContentService).GetMethod(nameof(ISiteBuilderSharedComponentContentService.TryLoadContentAsync)),
            typeof(ISiteBuilderSharedComponentContentService).GetMethod(nameof(ISiteBuilderSharedComponentContentService.TrySaveContentAsync)),
            typeof(ISiteBuilderSharedComponentService).GetMethod(nameof(ISiteBuilderSharedComponentService.UpdateMetadataAsync)),
            typeof(ISiteBuilderSharedComponentService).GetMethod(nameof(ISiteBuilderSharedComponentService.TryDeleteAsync)),
        };

        Assert.All(methods, method => Assert.True(method?.IsAbstract));
    }

    [Fact]
    public void NonRepositoryTypes_DoNotExposeSiteBuilderDbContext()
    {
        var dataAssembly = typeof(GroupedPageService).Assembly;
        var repositoryNamespace = typeof(SiteBuilderRepository).Namespace;

        var leakingMembers = dataAssembly.GetTypes()
            .Where(type => type.Namespace != repositoryNamespace)
            .SelectMany(GetDeclaredMembers)
            .Where(ReferencesSiteBuilderDbContext)
            .Select(member => $"{member.DeclaringType?.FullName}.{member.Name}")
            .Order()
            .ToArray();

        Assert.Empty(leakingMembers);
    }

    [Fact]
    public void ContentProviders_DoNotDependOnSiteBuilderRepositories()
    {
        var dataAssembly = typeof(SiteBuilderContentProvider).Assembly;
        var contentProviderNamespace = typeof(SiteBuilderContentProvider).Namespace;

        var leakingParameters = dataAssembly.GetTypes()
            .Where(type => type.Namespace == contentProviderNamespace)
            .SelectMany(type => type.GetConstructors())
            .SelectMany(constructor => constructor.GetParameters())
            .Where(parameter => ReferencesSiteBuilderRepository(parameter.ParameterType))
            .Select(parameter => $"{parameter.Member.DeclaringType?.FullName}.{parameter.Name}")
            .Order()
            .ToArray();

        Assert.Empty(leakingParameters);
    }

    [Fact]
    public void SiteBuilderRepository_DisposesItsDbContext()
    {
        var options = new DbContextOptionsBuilder<SiteBuilderDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var dbContext = new SiteBuilderDbContext(options);
        var repository = new SiteBuilderRepository(dbContext);

        repository.Dispose();

        Assert.Throws<ObjectDisposedException>(() => dbContext.Database.EnsureCreated());
    }

    [Fact]
    public async Task ContentStreamRepository_DisposesItsDbContext()
    {
        var options = new DbContextOptionsBuilder<SiteBuilderDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var dbContext = new SiteBuilderDbContext(options);
        var repository = new ContentStreamRepositoryProbe(dbContext);

        await repository.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => dbContext.Database.EnsureCreated());
    }

    [Fact]
    public void ServiceTypes_ArePublic()
    {
        var assemblies = new[]
        {
            typeof(GroupedPageService).Assembly,
            typeof(SiteBuilderPageContentService).Assembly,
        };

        var nonPublicServices = assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.Namespace?.Contains(".Services", StringComparison.Ordinal) == true)
            .Where(type => type.Name.EndsWith("Service", StringComparison.Ordinal))
            .Where(type => !type.IsPublic)
            .Select(type => type.FullName)
            .Order()
            .ToArray();

        Assert.Empty(nonPublicServices);
    }

    private static IEnumerable<MemberInfo> GetDeclaredMembers(Type type)
    {
        const BindingFlags flags = BindingFlags.DeclaredOnly |
                                   BindingFlags.Instance |
                                   BindingFlags.Static |
                                   BindingFlags.Public |
                                   BindingFlags.NonPublic;

        return type.GetMembers(flags)
            .Where(member => member is MethodBase or FieldInfo or PropertyInfo);
    }

    private static bool ReferencesSiteBuilderDbContext(MemberInfo member)
    {
        return member switch
        {
            MethodInfo method => ReferencesSiteBuilderDbContext(method.ReturnType) ||
                                 method.GetParameters().Any(parameter =>
                                     ReferencesSiteBuilderDbContext(parameter.ParameterType)),
            ConstructorInfo constructor => constructor.GetParameters().Any(parameter =>
                ReferencesSiteBuilderDbContext(parameter.ParameterType)),
            FieldInfo field => ReferencesSiteBuilderDbContext(field.FieldType),
            PropertyInfo property => ReferencesSiteBuilderDbContext(property.PropertyType),
            _ => false,
        };
    }

    private static bool ReferencesSiteBuilderDbContext(Type type)
    {
        return type == typeof(SiteBuilderDbContext) ||
               type.HasElementType && ReferencesSiteBuilderDbContext(type.GetElementType()!) ||
               type.IsGenericType && type.GetGenericArguments().Any(ReferencesSiteBuilderDbContext);
    }

    private static bool ReferencesSiteBuilderRepository(Type type)
    {
        return type == typeof(ISiteBuilderRepository) ||
               type == typeof(IContentStreamRepository) ||
               type.HasElementType && ReferencesSiteBuilderRepository(type.GetElementType()!) ||
               type.IsGenericType && type.GetGenericArguments().Any(ReferencesSiteBuilderRepository);
    }

    private sealed class ContentStreamRepositoryProbe(SiteBuilderDbContext dbContext)
        : ContentStreamRepository(dbContext)
    {
        protected override string QuoteOpen => "\"";
        protected override string QuoteClose => "\"";
        protected override string AppendContentChunkSql => string.Empty;

        protected override void SetIdParameter(DbCommand cmd, string value)
        {
        }

        protected override void SetContentChunk(DbCommand cmd, string chunk)
        {
        }
    }

}
