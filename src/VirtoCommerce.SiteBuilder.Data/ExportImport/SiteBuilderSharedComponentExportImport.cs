using Newtonsoft.Json;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.ExportImport;

namespace VirtoCommerce.SiteBuilder.Data.ExportImport;

public sealed class SiteBuilderSharedComponentExportImport(
    ISiteBuilderSharedComponentReferenceIndexService referenceIndexService,
    ISiteBuilderSharedComponentService componentService,
    ISiteBuilderSharedComponentSearchService componentSearchService,
    ISiteBuilderSharedComponentContentService componentContentService,
    JsonSerializer jsonSerializer)
{
    private const int BatchSize = 50;

    public async Task ExportAsync(
        JsonTextWriter writer,
        ExportImportProgressInfo progressInfo,
        Action<ExportImportProgressInfo> progressCallback,
        CancellationToken cancellationToken)
    {
        progressInfo.Description = "Site Builder Shared Components are started to export";
        progressCallback(progressInfo);

        await writer.WritePropertyNameAsync("SiteBuilderSharedComponents", cancellationToken);
        await writer.WriteStartArrayAsync(cancellationToken);

        var criteria = AbstractTypeFactory<SiteBuilderSharedComponentSearchCriteria>.TryCreateInstance();
        criteria.Take = BatchSize;
        var processedCount = 0;

        for (criteria.Skip = 0; ; criteria.Skip += BatchSize)
        {
            var searchResult = await componentSearchService.SearchAsync(criteria);

            foreach (var component in searchResult.Results)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var exportComponent = new SiteBuilderExportSharedComponent
                {
                    Id = component.Id,
                    StoreId = component.StoreId,
                    Name = component.Name,
                    Content = await componentContentService.LoadContentAsync(component.Id, cancellationToken),
                };
                jsonSerializer.Serialize(writer, exportComponent);
                processedCount++;
            }

            await writer.FlushAsync(cancellationToken);
            progressInfo.Description = $"{processedCount} of {searchResult.TotalCount} Site Builder Shared Components have been exported";
            progressInfo.ProcessedCount = processedCount;
            progressInfo.TotalCount = searchResult.TotalCount;
            progressCallback(progressInfo);

            if (criteria.Skip + BatchSize >= searchResult.TotalCount)
            {
                break;
            }
        }

        await writer.WriteEndArrayAsync(cancellationToken);
    }

    public async Task ImportAsync(
        JsonTextReader reader,
        ExportImportProgressInfo progressInfo,
        Action<ExportImportProgressInfo> progressCallback,
        CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken) || reader.TokenType != JsonToken.StartArray)
        {
            return;
        }

        var processedCount = 0;
        while (await reader.ReadAsync(cancellationToken) && reader.TokenType != JsonToken.EndArray)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var exportComponent = jsonSerializer.Deserialize<SiteBuilderExportSharedComponent>(reader);
            if (exportComponent == null)
            {
                continue;
            }

            SiteBuilderSharedComponentReferenceMatcher.ValidateComponentContent(exportComponent.Content);

            var component = string.IsNullOrWhiteSpace(exportComponent.Id)
                ? null
                : await componentService.GetByIdAsync(exportComponent.Id);
            component ??= AbstractTypeFactory<SiteBuilderSharedComponent>.TryCreateInstance();
            component.Id = exportComponent.Id;
            component.StoreId = exportComponent.StoreId;
            component.Name = exportComponent.Name;

            await componentService.SaveWithContentAsync(
                component,
                exportComponent.Content,
                cancellationToken);

            processedCount++;
            progressInfo.Description = $"{processedCount} Site Builder Shared Components have been imported";
            progressInfo.ProcessedCount = processedCount;
            progressCallback(progressInfo);
        }
    }

    public Task ValidatePageReferencesAsync(
        string storeId,
        IEnumerable<string> contents,
        CancellationToken cancellationToken = default)
    {
        return referenceIndexService.ValidateReferencesForStoreAsync(
            storeId,
            contents,
            cancellationToken);
    }
}
