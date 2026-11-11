using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VirtoCommerce.Platform.Caching;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Jobs;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.Platform.Data.Model;
using VirtoCommerce.Platform.Data.Repositories;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.BackgroundJobs;
using VirtoCommerce.SiteBuilder.Data.GitContent;
using VirtoCommerce.StoreModule.Core.Model;
using VirtoCommerce.StoreModule.Core.Model.Search;
using VirtoCommerce.StoreModule.Core.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Settings;

namespace VirtoCommerce.SiteBuilder.Data.Services;

/// <summary>
/// Copies Page Builder's settings to their Site Builder names once, where Site Builder has none of its own.
/// The Page Builder names are not registered once that module is gone, and the settings manager refuses
/// unregistered names, so they are read from the platform's settings rows.
/// </summary>
public class PageBuilderSettingsMigrationService(
    Func<IPlatformRepository> platformRepositoryFactory,
    ISettingsManager settingsManager,
    IStoreSearchService storeSearchService,
    IDataProtectionProvider dataProtectionProvider,
    IBackgroundJob backgroundJob,
    ILogger<PageBuilderSettingsMigrationService> logger)
    : IPageBuilderSettingsMigrationService
{
    internal const string PageBuilderAppConnectionPurpose = "VirtoCommerce.PageBuilderModule.GitHubAppConnection";
    internal const int StorePageSize = 50;

    internal static IReadOnlyList<SettingMapping> GlobalMappings { get; } =
    [
        new("VirtoCommerce.PageBuilderModule.General.StoreUrl", General.StoreUrl),
        new("VirtoCommerce.PageBuilderModule.General.StorePreviewPath", General.StorePreviewPath),
        new("VirtoCommerce.PageBuilderModule.General.OzAgentUrl", General.OzAgentUrl),
    ];

    internal static SettingMapping AppConnectionMapping { get; } =
        new("VirtoCommerce.PageBuilderModule.GitHub.AppConnection", GitHub.AppConnection);

    internal static IReadOnlyList<SettingMapping> StoreMappings { get; } =
    [
        new("VirtoCommerce.PageBuilderModule.Store.PreviewUserIds", StoreLevelSettings.PreviewUserIds),
        new("VirtoCommerce.PageBuilderModule.Store.GitContentEnabled", StoreLevelSettings.GitContentEnabled),
        new("VirtoCommerce.PageBuilderModule.Store.GitContentRepository", StoreLevelSettings.GitContentRepository),
        new("VirtoCommerce.PageBuilderModule.Store.NewPageBuilder", StoreLevelSettings.NewSiteBuilder),
    ];

    private static readonly object LockObject = new();

    public void StartMigration()
    {
        lock (LockObject)
        {
            var copied = settingsManager.GetValue<bool>(Migration.SettingsCopiedFromPageBuilder);
            if (!copied)
            {
                // Waits for the enqueue, so that a failed one surfaces at startup.
#pragma warning disable S4462 // Calls to "async" methods should not be blocking - void by contract, called from the synchronous module startup
                backgroundJob.Enqueue<PageBuilderSettingsCopyJob>(new PageBuilderSettingsCopyJobPayload()).GetAwaiter().GetResult();
#pragma warning restore S4462
            }
        }
    }

    public virtual async Task CopySettingsAsync(CancellationToken cancellationToken = default)
    {
        if (await settingsManager.GetValueAsync<bool>(Migration.SettingsCopiedFromPageBuilder))
        {
            return;
        }

        var counts = GlobalMappings.Append(AppConnectionMapping).Concat(StoreMappings)
            .ToDictionary(x => x.Descriptor.Name, _ => 0, StringComparer.OrdinalIgnoreCase);

        await CopyGlobalSettingsAsync(counts);
        await CopyStoreSettingsAsync(counts, cancellationToken);

        // Only after everything is copied: a failure before this point retries on the next start.
        await settingsManager.SetValueAsync(Migration.SettingsCopiedFromPageBuilder.Name, true);

        logger.LogInformation("Copied Page Builder settings to Site Builder: {Counts}",
            string.Join(", ", counts.Select(x => $"{x.Key}={x.Value}")));
    }

    private async Task CopyGlobalSettingsAsync(IDictionary<string, int> counts)
    {
        var mappings = GlobalMappings.Append(AppConnectionMapping).ToList();
        var rows = await LoadRowsAsync(mappings, objectType: null, objectIds: null);

        var entries = new List<ObjectSettingEntry>();
        foreach (var mapping in mappings)
        {
            var entry = mapping == AppConnectionMapping
                ? GetAppConnectionToCopy(rows)
                : GetEntryToCopy(mapping, rows);

            if (entry != null)
            {
                entries.Add(entry);
                counts[mapping.Descriptor.Name]++;
            }
        }

        if (entries.Count > 0)
        {
            await settingsManager.SaveObjectSettingsAsync(entries);
        }
    }

    private async Task CopyStoreSettingsAsync(IDictionary<string, int> counts, CancellationToken cancellationToken)
    {
        var criteria = AbstractTypeFactory<StoreSearchCriteria>.TryCreateInstance();
        criteria.Sort = nameof(Store.Id);
        criteria.Take = StorePageSize;
        var touched = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var searchResult = await storeSearchService.SearchAsync(criteria, clone: false);
            var storeIds = searchResult.Results.Select(x => x.Id).ToList();
            if (storeIds.Count > 0)
            {
                var rows = await LoadRowsAsync(StoreMappings, StoreSettingsObjectType, storeIds);
                var entries = new List<ObjectSettingEntry>();

                foreach (var storeRows in rows.GroupBy(x => x.ObjectId, StringComparer.OrdinalIgnoreCase))
                {
                    var rowsOfStore = storeRows.ToList();
                    foreach (var mapping in StoreMappings)
                    {
                        var entry = GetEntryToCopy(mapping, rowsOfStore);
                        if (entry != null)
                        {
                            entries.Add(entry);
                            counts[mapping.Descriptor.Name]++;
                        }
                    }
                }

                if (entries.Count > 0)
                {
                    await settingsManager.SaveObjectSettingsAsync(entries);

                    // Stores are cached with their settings.
                    foreach (var storeId in entries.Select(x => x.ObjectId).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        GenericCachingRegion<Store>.ExpireTokenForKey(storeId);
                    }

                    touched = true;
                }
            }

            criteria.Skip += criteria.Take;
            if (searchResult.Results.Count < criteria.Take || criteria.Skip >= searchResult.TotalCount)
            {
                break;
            }
        }

        if (touched)
        {
            GenericSearchCachingRegion<Store>.ExpireRegion();
        }
    }

    private async Task<IList<SettingEntity>> LoadRowsAsync(IReadOnlyCollection<SettingMapping> mappings, string objectType, IList<string> objectIds)
    {
        var names = mappings.SelectMany(x => new[] { x.PageBuilderName, x.Descriptor.Name }).ToArray();

        using var repository = platformRepositoryFactory();

        if (objectIds == null)
        {
            return await repository.GetObjectSettingsByNamesAsync(names, objectType, objectId: null);
        }

        return await repository.Settings
            .Include(x => x.SettingValues)
            .Where(x => x.ObjectType == objectType && objectIds.Contains(x.ObjectId) && names.Contains(x.Name))
            .AsNoTracking()
            .ToListAsync();
    }

    /// <summary>
    /// A row is a stored value, whatever it holds: an empty one was saved on purpose, and the default value
    /// cannot tell "never set" from "set to the default".
    /// </summary>
    private static ObjectSettingEntry GetEntryToCopy(SettingMapping mapping, IList<SettingEntity> rows)
    {
        if (HasRow(rows, mapping.Descriptor.Name))
        {
            return null;
        }

        var source = FindRow(rows, mapping.PageBuilderName);
        if (source == null)
        {
            return null;
        }

        var entry = source.ToModel(new ObjectSettingEntry(mapping.Descriptor));
        // a new row under the new name, not an update of the Page Builder one
        entry.Id = null;

        return entry.ItHasValues ? entry : null;
    }

    private ObjectSettingEntry GetAppConnectionToCopy(IList<SettingEntity> rows)
    {
        var entry = GetEntryToCopy(AppConnectionMapping, rows);
        if (entry?.Value is not string protectedValue || string.IsNullOrWhiteSpace(protectedValue))
        {
            return null;
        }

        try
        {
            var value = dataProtectionProvider.CreateProtector(PageBuilderAppConnectionPurpose).Unprotect(protectedValue);
            entry.Value = dataProtectionProvider.CreateProtector(GitHubConnectionService.ProtectionPurpose).Protect(value);
            return entry;
        }
        catch (CryptographicException ex)
        {
            logger.LogWarning(ex, "{SettingName} was not copied to {NewSettingName}: it cannot be decrypted with this server's data protection keys. Set up the GitHub connection again.",
                AppConnectionMapping.PageBuilderName, AppConnectionMapping.Descriptor.Name);
            return null;
        }
    }

    private static bool HasRow(IEnumerable<SettingEntity> rows, string name) => FindRow(rows, name) != null;

    private static SettingEntity FindRow(IEnumerable<SettingEntity> rows, string name) =>
        rows.FirstOrDefault(x => x.Name.EqualsIgnoreCase(name));

    internal sealed record SettingMapping(string PageBuilderName, SettingDescriptor Descriptor);
}
