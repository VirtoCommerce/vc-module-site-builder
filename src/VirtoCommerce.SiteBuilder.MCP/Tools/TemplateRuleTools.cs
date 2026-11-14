using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Theme;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;
using static VirtoCommerce.SiteBuilder.Data.Services.SiteBuilderPageItems;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

// The theme's template rules, edited as the designer's rules panel edits them: rules go in the draft of the rules
// file and reach customers with the theme's publication. A rule may give pages only a template the storefront has.
[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class TemplateRuleTools(
    IAuthorizationService authorizationService,
    ISiteBuilderContentQueryService contentQueryService,
    IDesignerOperationService designerOperationService,
    IThemeStorageFactory themeStorageFactory,
    ISchemaCatalogService schemaCatalogService)
{
    private const int MaxOperations = 50;
    private const string IdCharacters = "abcdefghijklmnopqrstuvwxyz0123456789";
    private const int IdLength = 8;

    [McpServerTool(Name = "edit_template_rules", ReadOnly = false, Destructive = true, Idempotent = false)]
    [McpPermission(Read)]
    [Authorize(Update)]
    [Authorize(Templates)]
    [Description("Change the template rules of a store's theme, which pick the template of each product, category and page: " +
        "add a rule, change its template or conditions, move it, enable or disable it, remove it. The storefront tries the rules " +
        "of a page type in order and the first enabled rule whose conditions all hold picks the template; without one the page " +
        "gets the type's default template. Read the rules first with search_templates and pass rules_version_token. A rule may " +
        "only pick a template the theme or the storefront has. Conditions: product rules inCategory (category ids), productType, " +
        "property ({ name, equals }); category rules inCategory; page rules path (* matches any characters), language, source " +
        "('Pages' or 'ContentFile'), setting ({ name, equals }). Customers see the change only when the theme is published " +
        "(publish_theme). Returns the rule list before and after. Call it with dryRun first to show the change.")]
    public async Task<ThemeFileChange> EditTemplateRules(
        RequestContext<CallToolRequestParams> context,
        [Description("Store id.")] string store_id,
        [Description("rules_version_token as search_templates returned it. An edit of older rules is refused.")] string version_token,
        [Description("The changes, applied in order.")] RuleEditOperation[] operations,
        [Description("true to check the edit and return the rules before and after, without saving them.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(store_id))
        {
            throw new McpException("Pass store_id: template rules belong to a store's theme.");
        }

        await authorizationService.EnsureAllowedAsync(context.User, new SiteBuilderPageSearchCriteria { StoreId = store_id }, store_id);

        if (operations == null || operations.Length == 0)
        {
            throw new McpException("Pass operations: the changes to make.");
        }

        if (operations.Length > MaxOperations)
        {
            throw new McpException($"Pass at most {MaxOperations} operations in one call.");
        }

        var (rules, file) = await ThemeTemplates.ReadRulesAsync(designerOperationService, themeStorageFactory, context.User, store_id, cancellationToken);
        if (string.IsNullOrWhiteSpace(version_token) || version_token.Trim() != file.ETag)
        {
            throw StaleVersion();
        }

        var templates = (await ThemeTemplates.ListAsync(contentQueryService, context.User, store_id, cancellationToken))
            .Select(x => x.Key)
            .Concat(ThemeTemplates.BuiltIn)
            .ToHashSet(StringComparer.Ordinal);

        var change = new ThemeFileChange
        {
            Action = "edit_template_rules",
            ObjectType = "template-rules",
            ObjectId = file.Path,
            ObjectName = "The template rules",
            StoreId = store_id,
        };

        for (var i = 0; i < operations.Length; i++)
        {
            var operation = operations[i] ?? throw new McpException($"Operation {i + 1} is empty.");
            try
            {
                Apply(rules, operation, templates, change);
            }
            catch (RuleEditException ex)
            {
                throw new McpException($"Operation {i + 1} ({operation.Op}): {ex.Message} Nothing was changed.");
            }
        }

        if (rules.Unreadable > 0)
        {
            change.AddWarning($"The rules file held {rules.Unreadable} rule(s) the storefront cannot read; the save leaves them out, as the designer does.");
        }

        var catalogHash = await ThemeFiles.RequireCatalogHashAsync(schemaCatalogService, store_id, cancellationToken);
        await ThemeFileEdits.SaveAsync(change, designerOperationService, context.User, store_id, catalogHash, [new ThemeFileEdit(file.Path, file, rules.Write())],
            dryRun, Templates, "Read the rules again with search_templates and repeat the edit with the new rules_version_token.", cancellationToken);

        change.Rules = TemplateRuleRow.From(rules);
        WarnAboutShadowedRules(rules, change);

        return change;
    }

    private static void Apply(TemplateRulesDocument rules, RuleEditOperation operation, ISet<string> templates, ThemeFileChange change)
    {
        switch (operation.Op?.Trim().ToLowerInvariant())
        {
            case "add":
                Add(rules, operation, templates);
                break;
            case "change":
                Change(rules, operation, templates);
                break;
            case "move":
                Move(rules, operation);
                break;
            case "enable":
            case "disable":
                SetDisabled(rules, operation, operation.Op.Trim().ToLowerInvariant() == "disable");
                break;
            case "remove":
                var (type, list, rule) = Find(rules, operation);
                list.Remove(rule);
                rules.SetRules(type, list);
                break;
            default:
                throw new RuleEditException($"'{operation.Op}' is not an operation. Use add, change, move, enable, disable or remove.");
        }

        if (operation.Op?.Trim().ToLowerInvariant() is "add" or "change" && operation.When?.Keys.Any(TemplateRuleConditions.IsPluginCondition) == true)
        {
            change.AddWarning("A condition of a storefront plugin is kept as given: the server does not check it, and a storefront without that plugin never matches the rule.");
        }
    }

    private static void Add(TemplateRulesDocument rules, RuleEditOperation operation, ISet<string> templates)
    {
        var template = CheckTemplate(operation.Template, null, templates);
        var type = TemplateRulesDocument.PageTypeOf(template);
        var list = rules.RulesOf(type);
        var rule = new JsonObject { ["id"] = NewId(rules), ["template"] = template };
        var when = SetConditions(new JsonObject(), operation.When, type);
        if (when.Count > 0)
        {
            rule["when"] = when;
        }

        if (operation.Disabled == true)
        {
            rule["disabled"] = true;
        }

        list.Insert(GetIndex(list, operation, list.Count), rule);
        rules.SetRules(type, list);
    }

    private static void Change(TemplateRulesDocument rules, RuleEditOperation operation, ISet<string> templates)
    {
        var (type, list, rule) = Find(rules, operation);
        if (operation.Template == null && operation.When == null)
        {
            throw new RuleEditException("Pass template, when or both: what to change.");
        }

        if (operation.Template != null)
        {
            rule["template"] = CheckTemplate(operation.Template, type, templates);
        }

        if (operation.When != null)
        {
            var when = SetConditions(rule["when"] as JsonObject ?? [], operation.When, type);
            if (when.Count > 0)
            {
                rule["when"] = when;
            }
            else
            {
                rule.Remove("when");
            }
        }

        rules.SetRules(type, list);
    }

    private static void Move(TemplateRulesDocument rules, RuleEditOperation operation)
    {
        var (type, list, rule) = Find(rules, operation);
        if (operation.Position == null && string.IsNullOrWhiteSpace(operation.After) && string.IsNullOrWhiteSpace(operation.Before))
        {
            throw new RuleEditException("Pass position, after or before: where the rule goes.");
        }

        if (operation.After == GetId(rule) || operation.Before == GetId(rule))
        {
            throw new RuleEditException("A rule cannot go next to itself; name another one in after or before.");
        }

        list.Remove(rule);
        list.Insert(GetIndex(list, operation, list.Count), rule);
        rules.SetRules(type, list);
    }

    private static void SetDisabled(TemplateRulesDocument rules, RuleEditOperation operation, bool disabled)
    {
        var (type, list, rule) = Find(rules, operation);
        if (disabled)
        {
            rule["disabled"] = true;
        }
        else
        {
            rule.Remove("disabled");
        }

        rules.SetRules(type, list);
    }

    // A rule by its id among the rules of every page type, or of page_type when the id is taken in more than one.
    private static (string Type, List<JsonObject> List, JsonObject Rule) Find(TemplateRulesDocument rules, RuleEditOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.RuleId))
        {
            throw new RuleEditException("Pass rule_id; search_templates lists the rules with their ids.");
        }

        var found = TemplateRulesDocument.PageTypes
            .Where(x => operation.PageType == null || x == operation.PageType)
            .Select(type => (Type: type, List: rules.RulesOf(type)))
            .SelectMany(x => x.List.Where(rule => GetId(rule) == operation.RuleId).Select(rule => (x.Type, x.List, Rule: rule)))
            .ToList();

        return found.Count switch
        {
            0 => throw new RuleEditException($"There is no rule '{operation.RuleId}'. Call search_templates for the rules and their ids."),
            > 1 => throw new RuleEditException($"Rules of more than one page type have the id '{operation.RuleId}'; pass page_type."),
            _ => found[0],
        };
    }

    private static string CheckTemplate(string template, string pageType, ISet<string> templates)
    {
        var key = template?.Trim();
        var type = TemplateRulesDocument.PageTypeOf(key);
        if (string.IsNullOrEmpty(key) || type == null || !ThemeStorage.IsTemplateKey(key))
        {
            throw new RuleEditException("Pass template: the key of the template the rule picks, such as 'product.bicycles'.");
        }

        if (pageType != null && type != pageType)
        {
            throw new RuleEditException($"A {pageType} rule picks a {pageType} template, not '{key}'.");
        }

        if (!templates.Contains(key))
        {
            throw new RuleEditException($"The theme has no template '{key}'. Create it first with edit_template, or pick one search_templates lists.");
        }

        return key;
    }

    // The conditions of the call over the rule's own: null removes one, an empty value too, as the rules panel drops it.
    private static JsonObject SetConditions(JsonObject when, Dictionary<string, JsonNode> conditions, string pageType)
    {
        var result = (JsonObject)when.DeepClone();
        foreach (var (name, value) in conditions ?? [])
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new RuleEditException("A condition needs a name.");
            }

            if (value == null || (GetString(value) is { } text && string.IsNullOrWhiteSpace(text)))
            {
                result.Remove(name);
                continue;
            }

            var normalized = TemplateRuleConditions.Normalize(name, pageType, value, out var error);
            if (error != null)
            {
                throw new RuleEditException(error);
            }

            result[name] = normalized;
        }

        return result;
    }

    private static int GetIndex(List<JsonObject> list, RuleEditOperation operation, int end)
    {
        var named = new[] { operation.Position != null, !string.IsNullOrWhiteSpace(operation.After), !string.IsNullOrWhiteSpace(operation.Before) }.Count(x => x);
        if (named > 1)
        {
            throw new RuleEditException("Pass only one of position, after and before.");
        }

        if (operation.Position is { } position)
        {
            return position < 0 || position > list.Count
                ? throw new RuleEditException($"position {position} is outside 0..{list.Count}.")
                : position;
        }

        var anchor = operation.After ?? operation.Before;
        if (string.IsNullOrWhiteSpace(anchor))
        {
            return end;
        }

        var index = list.FindIndex(x => GetId(x) == anchor);
        if (index < 0)
        {
            throw new RuleEditException($"There is no rule '{anchor}' of that page type to place it next to.");
        }

        return string.IsNullOrWhiteSpace(operation.After) ? index : index + 1;
    }

    // As the designer's newRuleId: random, and unique among the rules of every page type.
    private static string NewId(TemplateRulesDocument rules)
    {
        var taken = rules.AllIds().ToHashSet(StringComparer.Ordinal);
        while (true)
        {
            var id = string.Concat(Enumerable.Range(0, IdLength).Select(_ => IdCharacters[RandomNumberGenerator.GetInt32(IdCharacters.Length)]));
            if (!taken.Contains(id))
            {
                return id;
            }
        }
    }

    // A rule after an enabled rule without conditions never picks a template: the earlier one always wins.
    private static void WarnAboutShadowedRules(TemplateRulesDocument rules, ThemeFileChange change)
    {
        foreach (var type in TemplateRulesDocument.PageTypes)
        {
            var list = rules.RulesOf(type);
            var catchAll = list.FindIndex(x => !TemplateRulesDocument.IsDisabled(x) && (x["when"] as JsonObject)?.Count is null or 0);
            if (catchAll >= 0 && catchAll < list.Count - 1)
            {
                change.AddWarning($"{type} rule '{GetId(list[catchAll])}' has no conditions, so the {type} rules after it " +
                    $"({string.Join(", ", list.Skip(catchAll + 1).Select(GetId))}) never pick a template.");
            }
        }
    }

    private static McpException StaleVersion() =>
        new("The template rules changed since you read them, or version_token is not their version. Read them again with " +
            "search_templates and make the edit with its rules_version_token.");

    private sealed class RuleEditException(string message) : Exception(message);
}

// One change of edit_template_rules. Which fields an operation reads is listed in the description of "op".
public sealed class RuleEditOperation
{
    [JsonPropertyName("op")]
    [Description("What to do. add (template, when, optional disabled and place; without a place the rule goes last), change " +
        "(rule_id, template and/or when), move (rule_id, place), enable / disable (rule_id), remove (rule_id). A place is " +
        "position, after or before, among the rules of the rule's page type.")]
    public string Op { get; set; }

    [JsonPropertyName("rule_id")]
    [Description("Id of a rule, as search_templates lists it.")]
    public string RuleId { get; set; }

    [JsonPropertyName("page_type")]
    [Description("product, category or page: only when rules of two page types have the same id.")]
    public string PageType { get; set; }

    [JsonPropertyName("template")]
    [Description("The template the rule picks, for example 'product.bicycles'; its page type is the rule's.")]
    public string Template { get; set; }

    [JsonPropertyName("when")]
    [Description("Conditions by name, all of which must hold, for example { \"inCategory\": [\"cat-1\"] } or " +
        "{ \"path\": \"/blog/*\" }. With change, the named conditions are set and the others kept; null removes one.")]
    public Dictionary<string, JsonNode> When { get; set; }

    [JsonPropertyName("disabled")]
    [Description("add: true to add the rule disabled.")]
    public bool? Disabled { get; set; }

    [JsonPropertyName("position")]
    [Description("0-based place among the rules of the page type; 0 is tried first.")]
    public int? Position { get; set; }

    [JsonPropertyName("after")]
    [Description("Id of the rule this one goes right after.")]
    public string After { get; set; }

    [JsonPropertyName("before")]
    [Description("Id of the rule this one goes right before.")]
    public string Before { get; set; }
}
