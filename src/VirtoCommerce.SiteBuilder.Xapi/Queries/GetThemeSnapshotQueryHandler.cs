using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VirtoCommerce.ContentModule.Core.Model;
using VirtoCommerce.ContentModule.Core.Services;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Xapi.Models;
using VirtoCommerce.StoreModule.Core.Model;
using VirtoCommerce.Xapi.Core.Infrastructure;
using VirtoCommerce.Xapi.Core.Models;
using VirtoCommerce.Xapi.Core.Queries;
using VirtoCommerce.Xapi.Core.Services;
using VirtoCommerce.XCMS.Core.Models;
using VirtoCommerce.XCMS.Core.Queries;
using static VirtoCommerce.ContentModule.Core.ContentConstants;

namespace VirtoCommerce.SiteBuilder.Xapi.Queries;

/// <summary>
/// Menus are read on every request rather than published with the snapshot, so a menu edited in Content shows without a
/// new publication.
/// </summary>
public class GetThemeSnapshotQueryHandler(
    IStoreDomainResolverService storeDomainResolverService,
    IMenuLinkListSearchService menuLinkListSearchService,
    IThemeSnapshotService themeSnapshotService,
    IMediator mediator,
    IContentService contentService,
    ILogger<GetThemeSnapshotQueryHandler> logger)
    : IQueryHandler<GetThemeSnapshotQuery, ThemeSnapshotResult>
{
    public const string PagesObjectType = "Pages";

    public const string ContentFileObjectType = "ContentFile";

    protected const string SettingConditionName = "setting";

    protected static readonly IReadOnlyList<string> TemplateSettingNames = ["layout", "template"];

    public virtual async Task<ThemeSnapshotResult> Handle(GetThemeSnapshotQuery request, CancellationToken cancellationToken)
    {
        var storeRequest = AbstractTypeFactory<StoreDomainRequest>.TryCreateInstance();
        storeRequest.StoreId = request.StoreId;
        storeRequest.Domain = request.Domain;
        var store = await storeDomainResolverService.GetStoreAsync(storeRequest);
        if (store == null || !await themeSnapshotService.IsNewSiteBuilderEnabledAsync(store.Id, cancellationToken))
        {
            return null;
        }

        var snapshot = await themeSnapshotService.GetPublishedAsync(store.Id, request.CultureName, cancellationToken);
        if (snapshot == null)
        {
            return null;
        }

        var result = AbstractTypeFactory<ThemeSnapshotResult>.TryCreateInstance();
        result.StoreId = snapshot.StoreId;
        result.CultureName = snapshot.CultureName;
        result.Publication = snapshot.Publication;
        result.Content = snapshot.Content;
        result.Menus = await GetMenusAsync(snapshot);
        result.Slug = await GetSlugAsync(store, request, snapshot, cancellationToken);

        return result;
    }

    protected virtual async Task<IList<Menu>> GetMenusAsync(ThemeSnapshot snapshot)
    {
        if (snapshot.Menus.Count == 0)
        {
            return [];
        }

        var criteria = AbstractTypeFactory<MenuLinkListSearchCriteria>.TryCreateInstance();
        criteria.StoreId = snapshot.StoreId;
        var menuLinkLists = await menuLinkListSearchService.SearchAllAsync(criteria);

        return snapshot.Menus
            .Select(name => (Name: name, Menu: ToMenu(menuLinkLists, snapshot.CultureName, name)))
            .Where(x => x.Menu != null)
            .Select(x =>
            {
                // the storefront looks a menu up by the name the theme holds, which may differ in case from the list's
                x.Menu.Name = x.Name;
                return x.Menu;
            })
            .ToList();
    }

    /// <summary>Builds a menu as the x-cms <c>menu</c> query does, so both give the storefront the same links.</summary>
    protected static Menu ToMenu(IList<MenuLinkList> menuLinkLists, string cultureName, string name)
    {
        var menuLinkList = GetMenuLinkList(menuLinkLists, cultureName, name);
        if (menuLinkList == null)
        {
            return null;
        }

        var menu = new Menu
        {
            Name = menuLinkList.Name,
            OuterId = menuLinkList.OuterId,
        };

        foreach (var menuLink in menuLinkList.MenuLinks)
        {
            var childMenuLinkList = GetMenuLinkList(menuLinkLists, cultureName, menuLink.Title);
            menu.Items.Add(GetMenuItem(menuLink, childMenuLinkList));
        }

        return menu;
    }

    private static MenuLinkList GetMenuLinkList(IEnumerable<MenuLinkList> lists, string cultureName, string name)
    {
        return lists.FirstOrDefault(x =>
            x.Language?.EqualsIgnoreCase(cultureName) == true &&
            x.Name.EqualsIgnoreCase(name));
    }

    private static MenuItem GetMenuItem(MenuLink link, MenuLinkList list)
    {
        return new MenuItem
        {
            Link = link,
            ChildItems = list?.MenuLinks?.Select(x => GetMenuItem(x, null)).ToList() ?? [],
        };
    }

    /// <summary>
    /// Anonymous, as the snapshot is the same for every visitor, and in the language served rather than the one asked for:
    /// a URL may name only its language part.
    /// </summary>
    protected virtual async Task<ThemeSnapshotSlug> GetSlugAsync(Store store, GetThemeSnapshotQuery request, ThemeSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.Permalink))
        {
            return null;
        }

        try
        {
            var slugQuery = AbstractTypeFactory<SlugInfoQuery>.TryCreateInstance();
            slugQuery.Permalink = request.Permalink;
            slugQuery.StoreId = store.Id;
            slugQuery.CultureName = snapshot.CultureName;
            var slugInfo = await mediator.Send(slugQuery, cancellationToken);

            var entity = slugInfo?.EntityInfo;
            if (string.IsNullOrEmpty(entity?.ObjectType) || string.IsNullOrEmpty(entity.ObjectId))
            {
                return null;
            }

            var slug = AbstractTypeFactory<ThemeSnapshotSlug>.TryCreateInstance();
            slug.ObjectType = entity.ObjectType;
            slug.ObjectId = entity.ObjectId;
            slug.Page = await GetSlugPageAsync(store, slug, snapshot, cancellationToken);

            return slug;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The theme snapshot could not look up the permalink {Permalink} of the store {StoreId}", request.Permalink, store.Id);
            return null;
        }
    }

    protected virtual async Task<ThemeSnapshotSlugPage> GetSlugPageAsync(Store store, ThemeSnapshotSlug slug, ThemeSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (slug.ObjectType is not (PagesObjectType or ContentFileObjectType))
        {
            return null;
        }

        var page = AbstractTypeFactory<ThemeSnapshotSlugPage>.TryCreateInstance();
        JObject settings;

        switch (slug.ObjectType)
        {
            case PagesObjectType:
                {
                    var document = await mediator.Send(new GetSinglePageDocumentQuery { Id = slug.ObjectId }, cancellationToken);
                    if (document == null)
                    {
                        return null;
                    }

                    page.Source = document.Source;
                    page.Permalink = document.Permalink;
                    using var reader = new StringReader(document.Content ?? string.Empty);
                    settings = ReadSettings(reader);
                    break;
                }
            case ContentFileObjectType:
                {
                    var file = await mediator.Send(new GetSinglePageQuery { StoreId = store.Id, CultureName = snapshot.CultureName, Id = slug.ObjectId }, cancellationToken);
                    if (file == null)
                    {
                        return null;
                    }

                    page.Permalink = file.Permalink;
                    await using var stream = await contentService.GetItemStreamAsync(ContentTypes.Pages, store.Id, file.RelativeUrl);
                    using var reader = new StreamReader(stream);
                    settings = ReadSettings(reader);
                    break;
                }
            default:
                return null;
        }

        if (settings != null)
        {
            var names = GetTemplateSettingNames(snapshot);
            page.Settings = new JObject(settings.Properties().Where(x => names.Contains(x.Name))).ToString(Formatting.None);
        }

        return page;
    }

    /// <summary>The page rules are read only for the settings they name, never applied: the storefront applies them.</summary>
    protected virtual ISet<string> GetTemplateSettingNames(ThemeSnapshot snapshot)
    {
        var names = new HashSet<string>(TemplateSettingNames, StringComparer.Ordinal);

        if (string.IsNullOrEmpty(snapshot?.Content))
        {
            return names;
        }

        using var reader = new StringReader(snapshot.Content);

        if (ReadProperty(reader, "rules") is JObject rules && rules["page"] is JArray pageRules)
        {
            foreach (var rule in pageRules.OfType<JObject>())
            {
                if (rule["when"] is JObject when && when[SettingConditionName] is JObject condition && condition["name"] is JValue { Type: JTokenType.String } name)
                {
                    names.Add((string)name);
                }
            }
        }

        return names;
    }

    protected static JObject ReadSettings(TextReader content)
    {
        return ReadProperty(content, "settings") as JObject;
    }

    /// <summary>Reads one property without loading the rest of the JSON.</summary>
    [SuppressMessage("Major Code Smell", "S1168:Empty arrays and collections should be returned instead of null",
        Justification = "null means the document has no such property; any token, an empty one too, is the property's value.")]
    protected static JToken ReadProperty(TextReader content, string name)
    {
        using var reader = new JsonTextReader(content) { CloseInput = false };

        try
        {
            if (!reader.Read() || reader.TokenType != JsonToken.StartObject)
            {
                return null;
            }

            while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
            {
                var propertyName = (string)reader.Value;

                if (!reader.Read())
                {
                    return null;
                }

                if (propertyName == name)
                {
                    return JToken.ReadFrom(reader);
                }

                reader.Skip();
            }
        }
        catch (JsonReaderException)
        {
            // markdown, or another file that is not JSON
        }

        return null;
    }
}
