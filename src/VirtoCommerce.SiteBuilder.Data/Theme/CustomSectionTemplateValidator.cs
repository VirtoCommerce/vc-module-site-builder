using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using VirtoCommerce.SiteBuilder.Data.Validation;
using static VirtoCommerce.SiteBuilder.Data.Theme.CustomSectionFields;
using static VirtoCommerce.SiteBuilder.Data.Theme.CustomSectionText;

namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <summary>A custom section type's template: its elements and attributes, its bindings to the settings, the kit it uses, and what a screen reader needs.</summary>
public class CustomSectionTemplateValidator(CustomSectionUrlPolicy urlPolicy)
{
    private const int MaxSamples = 3;

    // quirks mode, where a table may stay in a p, as the storefront's sanitizer reads a document without a doctype; a strict parse refuses a
    // missing doctype, hence a legacy one that asks for quirks mode
    private const string DocumentStart = "<!DOCTYPE html PUBLIC \"-//W3C//DTD HTML 3.2 Final//EN\"><html><head></head><body>";

    private const string EachAttribute = "data-each";
    private const string IfAttribute = "data-if";
    private const string HtmlAttribute = "data-html";
    private const string AltAttribute = "alt";
    private const string DataPrefix = "data-";

    private static readonly HashSet<string> FormControls = new(StringComparer.Ordinal)
    {
        "form", "input", "select", "textarea", "button", "option", "optgroup", "datalist", "fieldset", "legend", "label", "output",
    };

    /// <summary>The root counts as 1.</summary>
    public const int MaxDepth = 32;

    /// <summary>
    /// Each '&lt;' followed by a letter, counted in the template as written, in comments and attribute values too: the HTML parser takes
    /// seconds over thousands of nested elements, so a template over this count is not parsed at all.
    /// </summary>
    public const int MaxTags = 1500;

    /// <summary>Besides the kit tags the catalog names.</summary>
    public static IReadOnlySet<string> Elements { get; } =
        SiteBuilderHtmlSanitizer.AllowedTags.Where(x => !FormControls.Contains(x)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Of any element, besides aria-*, the binding attributes and the catalog's behaviors.</summary>
    public static IReadOnlySet<string> Attributes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "class", "role", "alt", "title", "lang", "dir", "hidden", "href", "target", "rel", "src", "srcset", "sizes", "loading", "decoding",
        "width", "height", "datetime", "colspan", "rowspan", "scope",
    };

    /// <summary>By element: the attributes only it takes.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> ElementAttributes { get; } = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
    {
        ["video"] = new HashSet<string>(StringComparer.Ordinal) { "autoplay", "muted", "loop", "playsinline", "controls", "preload", "poster" },
        ["source"] = new HashSet<string>(StringComparer.Ordinal) { "type", "media" },
        ["details"] = new HashSet<string>(StringComparer.Ordinal) { "open" },
        ["blockquote"] = new HashSet<string>(StringComparer.Ordinal) { "cite" },
        ["q"] = new HashSet<string>(StringComparer.Ordinal) { "cite" },
    };

    public static IReadOnlyList<string> BindingAttributes { get; } = [EachAttribute, IfAttribute, HtmlAttribute];

    /// <summary>Read by people, so a literal value is untranslated text.</summary>
    public static IReadOnlySet<string> TextAttributes { get; } = new HashSet<string>(StringComparer.Ordinal) { "alt", "aria-label", "label", "title" };

    /// <summary>Besides a path starting with '/'.</summary>
    public static IReadOnlyList<string> LinkSchemes { get; } = ["https:", "mailto:", "tel:"];

    public static IReadOnlyList<string> LinkTargets { get; } = ["_self", "_blank"];

    public static IReadOnlyList<string> RefusedRels { get; } = ["opener"];

    /// <summary>What follows the type in a class of the type's own; the type itself is one too.</summary>
    public static IReadOnlyList<string> ClassSeparators { get; } = ["-", "__"];

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    // the path is trimmed after the match: a pattern that trims it backtracks over a long run of spaces
    private static readonly Regex BindingPattern = new(@"\{\{([^{}]*)\}\}", RegexOptions.None, RegexTimeout);
    private static readonly Regex SingleBindingPattern = new(@"^\s*\{\{([^{}]*)\}\}\s*$", RegexOptions.None, RegexTimeout);
    private static readonly Regex PathPattern = new(@"^(settings|item)\.([A-Za-z][A-Za-z0-9_]*)(?:\.([A-Za-z][A-Za-z0-9_]*))?$", RegexOptions.None, RegexTimeout);
    private static readonly Regex IsoDatePattern = new(@"^\d{4}-\d{2}-\d{2}(?:T\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]\d{2}:\d{2})?)?$", RegexOptions.None, RegexTimeout);

    public virtual void Validate(CustomSectionCheck check, string template)
    {
        if (CountTags(template) > MaxTags)
        {
            check.AddError(string.Create(CultureInfo.InvariantCulture, $"the template has more than {MaxTags} tags, counting each '<' followed by a letter as written"));
            return;
        }

        var context = new TemplateContext(template);
        using var document = Parse(check, context);
        if (document == null)
        {
            return;
        }

        var body = document.Body;
        if (body.ChildNodes.OfType<IText>().Any(x => !string.IsNullOrWhiteSpace(x.Data)))
        {
            check.AddError("the template has text outside its root element");
        }

        var roots = body.Children.ToList();
        if (roots.Count != 1)
        {
            check.AddError(string.Create(CultureInfo.InvariantCulture,
                $"the template has {roots.Count} root elements, and has one: the section's root"));
            return;
        }

        if (roots[0].LocalName.StartsWith(CustomSectionKit.TagPrefix, StringComparison.Ordinal))
        {
            check.AddError($"the template's root is <{roots[0].LocalName}>, a kit tag, which renders a component and not the section: the root is an element of HTML");
            return;
        }

        // before the recursive walk, so a deep template cannot exhaust the stack
        if (GetDepth(roots[0]) > MaxDepth)
        {
            check.AddError(string.Create(CultureInfo.InvariantCulture, $"the template nests elements deeper than {MaxDepth}"));
            return;
        }

        ValidateElement(check, roots[0], new TemplateScope(Each: null, IsRoot: true, context, Repeating: null, Behaviors: ImmutableHashSet<string>.Empty));
        ValidateHeadings(check, roots[0]);
        AddTextWarning(check, context);
    }

    protected virtual void ValidateElement(CustomSectionCheck check, IElement element, TemplateScope scope)
    {
        var tag = element.LocalName;
        var where = string.Create(CultureInfo.InvariantCulture, $"<{tag}> at line {GetLine(scope.Context, element)}");
        IReadOnlyDictionary<string, KitField> kitAttributes = null;
        if (tag.StartsWith(CustomSectionKit.TagPrefix, StringComparison.Ordinal))
        {
            if (GetKitTagError(check, tag) is { } error)
            {
                check.AddError($"{where} {error}");
                return;
            }

            kitAttributes = check.Kit.Tags[tag];
        }
        else if (!Elements.Contains(tag))
        {
            check.AddError($"{where} is not an element a custom section can hold");
            return;
        }

        ValidateAccessibility(check, element, where);

        var each = element.GetAttribute(EachAttribute) is { } eachPath ? ValidateEach(check, eachPath, where, scope) : scope.Each;
        var behaviors = element.Attributes.Select(x => FindBehavior(check, x.Name)).Where(x => x is { Option: null }).Select(x => x.Value.Name).ToList();
        var elementScope = scope with { Each = each, Behaviors = scope.Behaviors.Union(behaviors) };
        foreach (var attribute in element.Attributes)
        {
            ValidateAttribute(check, element, attribute.Name, attribute.Value, kitAttributes, where, elementScope);
        }

        var repeating = behaviors.Find(x => check.Kit.Behaviors[x].RepeatsChildren);
        var childScope = elementScope with { IsRoot = false, Repeating = repeating == null ? scope.Repeating : DataPrefix + repeating };
        var holdsNothing = kitAttributes != null || element.HasAttribute(HtmlAttribute);
        var hasContent = false;
        foreach (var node in element.ChildNodes)
        {
            if (node is IElement child)
            {
                hasContent = true;
                if (!holdsNothing)
                {
                    ValidateElement(check, child, childScope);
                }
            }
            else if (node is IText text && !string.IsNullOrWhiteSpace(text.Data))
            {
                hasContent = true;
                if (!holdsNothing)
                {
                    AddText(childScope.Context, ValidateBindings(check, text.Data, where, childScope));
                }
            }
        }

        if (holdsNothing && hasContent)
        {
            check.AddError(kitAttributes != null ? $"{where} has content, which a kit tag does not have" : $"{where} has data-html and content of its own");
        }
    }

    /// <summary>A screen reader lists the headings by level, so a level skipped on the way down reads as a part missing.</summary>
    protected virtual void ValidateHeadings(CustomSectionCheck check, IElement root)
    {
        var levels = root.QuerySelectorAll("*").Prepend(root)
            .Where(x => x.LocalName is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
            .Select(x => x.LocalName[1] - '0')
            .ToList();
        var skip = levels.Zip(levels.Skip(1)).FirstOrDefault(x => x.Second > x.First + 1);
        if (skip != default)
        {
            check.AddWarning(string.Create(CultureInfo.InvariantCulture,
                $"the template's headings go from h{skip.First} to h{skip.Second}, skipping a level, which a screen reader lists as a part missing"));
        }
    }

    /// <summary>Elements a screen reader cannot name.</summary>
    protected virtual void ValidateAccessibility(CustomSectionCheck check, IElement element, string where)
    {
        switch (element.LocalName)
        {
            case "h1":
                check.AddError($"{where} is an h1, which the page holds: a section's headings start at h2");
                break;
            case "img" when !element.HasAttribute(AltAttribute):
                check.AddError($"{where} has no alt: bind it to a string setting, or write alt=\"\" for an image that only decorates");
                break;
            case "a" when !HasName(element):
                check.AddError($"{where} is a link with nothing to read: give it text or a binding, an aria-label, or an image with an alt");
                break;
        }
    }

    protected virtual string GetKitTagError(CustomSectionCheck check, string tag)
    {
        if (!check.Kit.HasCatalog)
        {
            return "is a kit tag, and no schema catalog names the storefront's kit tags: save it with the catalog of the storefront's preview";
        }

        if (check.Kit.Tags == null)
        {
            return "is a kit tag, and the storefront offers none: its catalog has no kit";
        }

        if (!check.Kit.Tags.ContainsKey(tag))
        {
            return "is not a kit tag the storefront offers";
        }

        return check.Kit.TagFaults.TryGetValue(tag, out var fault) ? $"is a kit tag the catalog declares with the attribute {fault}" : null;
    }

    /// <param name="kitAttributes">The attributes of the kit tag the element is; <c>null</c> for an element of HTML.</param>
    protected virtual void ValidateAttribute(CustomSectionCheck check, IElement element, string name, string value,
        IReadOnlyDictionary<string, KitField> kitAttributes, string where, TemplateScope scope)
    {
        switch (name)
        {
            case EachAttribute:
                return;
            case IfAttribute:
                ValidateIf(check, value, where, scope);
                return;
            case HtmlAttribute:
                ValidateHtml(check, value, kitAttributes != null, where, scope);
                return;
        }

        // before the kit's names: a catalog may name a behavior or an attribute as the storefront's own
        if (CustomSectionKit.IsReservedDataAttribute(name))
        {
            check.AddError($"{where} has an attribute a custom section cannot use, '{name}': the storefront writes " +
                $"{JoinAnd(CustomSectionKit.ReservedBehaviorPrefixes.Select(x => DataPrefix + x))} attributes itself");
            return;
        }

        if (kitAttributes != null)
        {
            if (CustomSectionKit.IsDeniedAttribute(name))
            {
                check.AddError($"{where} has '{name}', which no kit tag takes, whatever the catalog declares");
            }
            else if (kitAttributes.TryGetValue(name, out var field))
            {
                ValidateKitValue(check, element.LocalName, field, name, value, where, scope);
            }
            else
            {
                check.AddError($"{where} has an attribute {element.LocalName} does not take, '{name}'");
            }

            return;
        }

        if (FindBehavior(check, name) is { } behavior)
        {
            ValidateBehavior(check, element, behavior.Name, behavior.Option, name, value, where, scope);
        }
        else if (name is "href")
        {
            ValidateLink(check, name, value, where, scope);
        }
        else if (ElementAttributes.TryGetValue(element.LocalName, out var own) && own.Contains(name))
        {
            switch (name)
            {
                case "cite":
                    ValidateLink(check, name, value, where, scope);
                    break;
                case "poster":
                    ValidateImage(check, name, value, where, scope);
                    break;
                default:
                    ValidateBindings(check, value, where, scope);
                    break;
            }
        }
        else if (name is "class")
        {
            ValidateClass(check, value, where, scope);
        }
        else if (name is "target" or "rel")
        {
            ValidateLinkTarget(check, name, value, where);
        }
        else if (name is "src" or "srcset")
        {
            ValidateImage(check, name, value, where, scope);
        }
        else if (name == AltAttribute)
        {
            ValidateAlt(check, value, where, scope);
        }
        else if (Attributes.Contains(name) || name.StartsWith("aria-", StringComparison.Ordinal))
        {
            var text = ValidateBindings(check, value, where, scope);
            if (TextAttributes.Contains(name))
            {
                AddText(scope.Context, text);
            }
        }
        else
        {
            check.AddError($"{where} has an attribute a custom section cannot use, '{name}'{GetAttributeReason(check, name)}");
        }
    }

    protected virtual string GetAttributeReason(CustomSectionCheck check, string name) => name switch
    {
        "id" => ": two sections on a page would share it",
        "style" => ": the section's CSS styles it",
        _ when name.StartsWith("on", StringComparison.Ordinal) => ": it runs script",
        _ when !name.StartsWith(DataPrefix, StringComparison.Ordinal) => string.Empty,
        _ when !check.Kit.HasCatalog => ": it is no binding, and no schema catalog names the storefront's behaviors",
        _ when check.Kit.Behaviors == null => ": it is no binding, and the storefront offers no behaviors, as its catalog has no kit",
        _ => ": it is no binding and no behavior the storefront offers",
    };

    /// <returns>The list the element repeats for, as far as it is known.</returns>
    protected virtual CustomSectionSetting ValidateEach(CustomSectionCheck check, string value, string where, TemplateScope scope)
    {
        if (scope.IsRoot)
        {
            check.AddError($"{where} is the root and has data-each, which repeats an element inside the root");
        }
        else if (scope.Each != null)
        {
            check.AddError($"{where} has data-each inside an element that has one");
        }

        var match = PathPattern.Match(value.Trim());
        if (!match.Success || match.Groups[1].Value != SettingsKey || match.Groups[3].Success)
        {
            check.AddError($"{where} has data-each=\"{Quote(value)}\", which is not settings.<id> of a list setting");
            return CustomSectionSetting.UnknownList;
        }

        var id = match.Groups[2].Value;
        if (!check.Settings.TryGetValue(id, out var setting))
        {
            check.AddError($"{where} repeats for '{id}', a setting the schema does not declare");
            return CustomSectionSetting.UnknownList;
        }

        if (setting.Type != ListType)
        {
            check.AddError($"{where} repeats for '{id}', a {setting.Type} and not a list");
            return CustomSectionSetting.UnknownList;
        }

        return setting;
    }

    protected virtual void ValidateIf(CustomSectionCheck check, string value, string where, TemplateScope scope)
    {
        var path = value.Trim();
        ResolvePath(check, path.StartsWith('!') ? path[1..].TrimStart() : path, where, scope);
    }

    protected virtual void ValidateHtml(CustomSectionCheck check, string value, bool isKitTag, string where, TemplateScope scope)
    {
        if (isKitTag)
        {
            check.AddError($"{where} has data-html, and a kit tag holds nothing");
            return;
        }

        var path = value.Trim();
        if (ResolvePath(check, path, where, scope) is { } setting && setting.Type != TextType)
        {
            check.AddError($"{where} has data-html=\"{Quote(path)}\", a {setting.Type}; it names a text setting, whose value is HTML");
        }
    }

    /// <param name="owner">The kit tag or behavior attribute the error names.</param>
    protected virtual void ValidateKitValue(CustomSectionCheck check, string owner, KitField field, string name, string value, string where,
        TemplateScope scope)
    {
        if (field.Type == CustomSectionKit.LinkType || CustomSectionKit.LinkAttributes.Contains(name.ToLowerInvariant()))
        {
            ValidateLink(check, name, value, where, scope);
            return;
        }

        if (field.Type is not (CustomSectionKit.NumberType or CustomSectionKit.BooleanType or CustomSectionKit.SelectType or CustomSectionKit.DateType))
        {
            var text = ValidateBindings(check, value, where, scope);
            if (TextAttributes.Contains(name))
            {
                AddText(scope.Context, text);
            }

            return;
        }

        if (GetSingleBinding(value) is { } path)
        {
            ResolveValue(check, path, where, scope);
        }
        else if (HasBinding(value))
        {
            check.AddError($"{where} has {name}=\"{Quote(value)}\", which is a value or one binding");
        }
        else if (GetKitValueRule(check, field, value) is { } rule)
        {
            check.AddError($"{where} has {name}=\"{Quote(value)}\", which {owner} does not take: it takes {rule}");
        }
    }

    /// <returns>What the field takes, when the value is not one of it; <c>null</c> for a value it takes.</returns>
    protected virtual string GetKitValueRule(CustomSectionCheck check, KitField field, string value)
    {
        switch (field.Type)
        {
            case CustomSectionKit.BooleanType:
                return value is "" or "true" or "false" ? null : "true, false or no value";
            case CustomSectionKit.SelectType when field.OptionsFrom == CustomSectionKit.IconsKey:
                return check.Kit.Icons == null || check.Kit.Icons.Contains(value) ? null : "an icon name of the storefront, as the kit's icons list them";
            case CustomSectionKit.SelectType:
                return field.Options.Contains(value, StringComparer.Ordinal) ? null : string.Join(", ", field.Options);
            case CustomSectionKit.DateType:
                return IsIsoDate(value) ? null : "an ISO 8601 date, or a date and a time, such as 2026-12-31 or 2026-12-31T23:59:59Z";
            default:
                var isNumber = decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number);
                return isNumber && !(number < field.Min) && !(number > field.Max) ? null : DescribeNumber(field);
        }
    }

    /// <summary>
    /// An option is data-{behavior}-{option}, on the element with the behavior or inside it, as the countdown's parts are; a value with no declared
    /// type is the storefront's to read.
    /// </summary>
    protected virtual void ValidateBehavior(CustomSectionCheck check, IElement element, string behavior, string option, string name, string value,
        string where, TemplateScope scope)
    {
        var attribute = DataPrefix + behavior;
        var kitBehavior = check.Kit.Behaviors[behavior];
        if (kitBehavior.Fault != null)
        {
            check.AddError($"{where} has {name}, and the catalog declares {attribute} with {kitBehavior.Fault}");
            return;
        }

        if (option == null)
        {
            if (kitBehavior.RepeatsChildren && scope.Repeating is { } outer)
            {
                check.AddError($"{where} has {name}, which repeats what it holds, inside an element with {outer}, which repeats it too: " +
                    "each level would multiply the page");
            }

            if (kitBehavior.Value is { } valueField)
            {
                ValidateKitValue(check, attribute, valueField, name, value, where, scope);
            }
            else
            {
                ValidateBindings(check, value, where, scope);
            }

            return;
        }

        if (!kitBehavior.Options.TryGetValue(option, out var field))
        {
            check.AddError($"{where} has {name}, which is not an option {attribute} takes");
            return;
        }

        if (!scope.Behaviors.Contains(behavior))
        {
            check.AddError($"{where} has {name}, an option of {attribute}, outside an element with {attribute}");
        }

        ValidateKitValue(check, attribute, field, name, value, where, scope);
    }

    /// <summary>The behavior a data- attribute switches on, with the option it sets; the longest name wins, so count-up-duration is an option of count-up.</summary>
    protected virtual (string Name, string Option)? FindBehavior(CustomSectionCheck check, string attribute)
    {
        if (check.Kit.Behaviors == null || !attribute.StartsWith(DataPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var name = attribute[DataPrefix.Length..];
        if (check.Kit.Behaviors.ContainsKey(name))
        {
            return (name, null);
        }

        // from the right, so the longest behavior wins; no prefix longer than the longest behavior can name one
        for (var end = name.LastIndexOf('-', Math.Min(name.Length - 1, check.Kit.MaxBehaviorLength)); end > 0; end = name.LastIndexOf('-', end - 1))
        {
            if (check.Kit.Behaviors.ContainsKey(name[..end]))
            {
                return (name[..end], name[(end + 1)..]);
            }
        }

        return null;
    }

    /// <summary>
    /// A class is the type's own: the storefront's utility classes would style the section from outside its CSS, such as fixed over the
    /// page. A binding names a select whose options are the type's own classes.
    /// </summary>
    protected virtual void ValidateClass(CustomSectionCheck check, string value, string where, TemplateScope scope)
    {
        var literal = ValidateBindings(check, value, where, scope, (setting, path) =>
        {
            if (setting.Type != SelectType || setting.Options == null || !setting.Options.All(x => IsOwnClasses(check, x)))
            {
                check.AddError($"{where} binds class to '{path}', which is not a select whose options are the type's own classes");
            }
        });

        foreach (var name in literal.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries).Where(x => !IsOwnClasses(check, x)).Distinct(StringComparer.Ordinal))
        {
            var own = JoinOr(ClassSeparators.Select(x => $"'{check.Type}{x}'"));
            check.AddError($"{where} has the class '{Quote(name)}', which is not the type's own: '{check.Type}', or a name starting with {own}");
        }
    }

    /// <summary>An image's alt is read in the page's language, so it binds a string setting; a literal alt is saved with the text warning.</summary>
    protected virtual void ValidateAlt(CustomSectionCheck check, string value, string where, TemplateScope scope)
    {
        var literal = ValidateBindings(check, value, where, scope, (setting, path) =>
        {
            if (setting.Type != StringType)
            {
                check.AddError($"{where} has alt bound to '{path}', a {setting.Type} and not a string setting");
            }
        });

        AddText(scope.Context, literal);
    }

    /// <summary>target and rel are written as they are, with no binding; rel never holds opener.</summary>
    protected virtual void ValidateLinkTarget(CustomSectionCheck check, string name, string value, string where)
    {
        if (HasBinding(value))
        {
            check.AddError($"{where} has {name}=\"{Quote(value)}\", which is written as it is, without a binding");
        }
        else if (name == "target" && !LinkTargets.Contains(value.Trim(), StringComparer.Ordinal))
        {
            check.AddError($"{where} has target=\"{Quote(value)}\", which is not {JoinOr(LinkTargets)}");
        }
        else if (name == "rel" && value.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(x => RefusedRels.Contains(x, StringComparer.OrdinalIgnoreCase)) is { } refused)
        {
            check.AddError($"{where} has rel=\"{Quote(value)}\" with {refused.ToLowerInvariant()}, which gives the page it opens a hold on the storefront's window");
        }
    }

    protected virtual void ValidateLink(CustomSectionCheck check, string name, string value, string where, TemplateScope scope)
    {
        if (GetSingleBinding(value) is { } path)
        {
            if (ResolveValue(check, path, where, scope) is { } setting && !setting.IsLink)
            {
                check.AddError($"{where} has {name} bound to '{path}', which is not a link setting: a string with \"format\": \"url\"");
            }
        }
        else if (HasBinding(value))
        {
            check.AddError($"{where} has {name}=\"{Quote(value)}\", which is a link or one binding of a link setting");
        }
        else if (!urlPolicy.IsLink(value, LinkSchemes))
        {
            check.AddError($"{where} has {name}=\"{Quote(value)}\", which is not a path starting with '/', or an {JoinOr(LinkSchemes)} URL");
        }
    }

    protected virtual void ValidateImage(CustomSectionCheck check, string name, string value, string where, TemplateScope scope)
    {
        if (GetSingleBinding(value) is { } path)
        {
            if (ResolveValue(check, path, where, scope) is { } setting && setting.Type != ImagesType)
            {
                check.AddError($"{where} has {name} bound to '{path}', a {setting.Type} and not an images setting");
            }

            return;
        }

        if (HasBinding(value))
        {
            check.AddError($"{where} has {name}=\"{Quote(value)}\", which is a URL or one binding of an images setting");
            return;
        }

        var urls = name == "srcset"
            ? value.Split(',').Select(x => x.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty)
            : [value];
        foreach (var url in urls.Where(x => !urlPolicy.IsAssetUrl(x)))
        {
            check.AddError($"{where} has {name} with '{Quote(url)}', which is not a URL of the store's asset library");
        }
    }

    /// <summary>Checks the bindings in a text or an attribute value; returns the text without them.</summary>
    /// <param name="checkSetting">Checks the setting a binding names, with its path, when the setting is known.</param>
    protected virtual string ValidateBindings(CustomSectionCheck check, string text, string where, TemplateScope scope,
        Action<CustomSectionSetting, string> checkSetting = null)
    {
        var literal = BindingPattern.Replace(text, match =>
        {
            var path = match.Groups[1].Value.Trim();
            if (ResolveValue(check, path, where, scope) is { } setting)
            {
                checkSetting?.Invoke(setting, path);
            }

            return " ";
        });

        if (HasBinding(literal) || literal.Contains("}}", StringComparison.Ordinal))
        {
            check.AddError($"{where} has a '{{{{' or a '}}}}' that is no binding, such as {{{{ settings.title }}}}");
        }

        return literal;
    }

    /// <summary>A path whose value is inserted: a list is repeated with data-each, and an object is bound by its fields.</summary>
    protected virtual CustomSectionSetting ResolveValue(CustomSectionCheck check, string path, string where, TemplateScope scope)
    {
        var setting = ResolvePath(check, path, where, scope);
        if (setting?.Element != null)
        {
            check.AddError(setting.Type == ListType
                ? $"{where} binds '{path}', a list, which data-each repeats an element for"
                : $"{where} binds '{path}', an object, whose fields are bound one by one");
        }

        return setting;
    }

    /// <returns>The setting the path names; <c>null</c> when it names none, or one of a list that is not known.</returns>
    protected virtual CustomSectionSetting ResolvePath(CustomSectionCheck check, string path, string where, TemplateScope scope)
    {
        var match = PathPattern.Match(path);
        if (!match.Success)
        {
            check.AddError($"{where} binds '{Quote(path)}', which is not settings.<id>, settings.<id>.<field> or item.<field>");
            return null;
        }

        var id = match.Groups[2].Value;
        var field = match.Groups[3];
        if (match.Groups[1].Value != SettingsKey)
        {
            if (scope.Each == null)
            {
                check.AddError($"{where} binds '{path}' outside an element with data-each, where there is no item");
                return null;
            }

            if (field.Success)
            {
                check.AddError($"{where} binds '{path}', deeper than the fields of a row");
                return null;
            }

            if (scope.Each.Element == null)
            {
                return null;
            }

            if (!scope.Each.Element.TryGetValue(id, out var rowField))
            {
                check.AddError($"{where} binds '{path}', a field the rows of '{scope.Each.Id}' do not have");
            }

            return rowField;
        }

        if (!check.Settings.TryGetValue(id, out var setting))
        {
            check.AddError($"{where} binds '{path}', a setting the schema does not declare");
            return null;
        }

        if (!field.Success)
        {
            return setting;
        }

        if (setting.Type != ObjectType)
        {
            check.AddError($"{where} binds '{path}', but '{id}' is a {setting.Type} and not an object");
            return null;
        }

        if (!setting.Element.TryGetValue(field.Value, out var objectField))
        {
            check.AddError($"{where} binds '{path}', a field '{id}' does not have");
        }

        return objectField;
    }

    protected virtual void AddTextWarning(CustomSectionCheck check, TemplateContext context)
    {
        if (context.Texts.Count > 0)
        {
            var texts = string.Join(", ", context.Texts.Distinct(StringComparer.Ordinal).Take(MaxSamples).Select(x => $"'{Quote(x)}'"));
            check.AddWarning($"the template holds text of its own ({texts}), which is the same in every language: bind it to a translatable setting");
        }
    }

    private static void AddText(TemplateContext context, string text)
    {
        // text without a letter, such as '+' after a number, reads the same in every language
        if (text.Any(char.IsLetter))
        {
            context.Texts.Add(string.Join(' ', text.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries)));
        }
    }

    private static int GetDepth(IElement root)
    {
        var deepest = 0;
        var elements = new Stack<(IElement Element, int Depth)>([(root, 1)]);
        while (elements.TryPop(out var current))
        {
            deepest = Math.Max(deepest, current.Depth);
            foreach (var child in current.Element.Children)
            {
                elements.Push((child, current.Depth + 1));
            }
        }

        return deepest;
    }

    private static int CountTags(string template)
    {
        var count = 0;
        for (var i = template.IndexOf('<'); i >= 0 && i < template.Length - 1; i = template.IndexOf('<', i + 1))
        {
            count += char.IsAsciiLetter(template[i + 1]) ? 1 : 0;
        }

        return count;
    }

    private static bool IsOwnClasses(CustomSectionCheck check, string classes) =>
        classes.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries)
            .All(x => x == check.Type || ClassSeparators.Any(separator => x.StartsWith(check.Type + separator, StringComparison.Ordinal)));

    private static string GetSingleBinding(string value)
    {
        var match = SingleBindingPattern.Match(value);

        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static bool HasBinding(string value) => value.Contains("{{", StringComparison.Ordinal);

    // an element of SVG keeps no place of its own, so it is looked for after its parent's
    private static int GetLine(TemplateContext context, IElement element)
    {
        var placed = element;
        while (placed.SourceReference == null && placed.ParentElement != null)
        {
            placed = placed.ParentElement;
        }

        var index = GetIndex(context, placed.SourceReference?.Position.Position ?? 0);
        if (placed != element && context.Template.IndexOf("<" + element.LocalName, index, StringComparison.OrdinalIgnoreCase) is >= 0 and var found)
        {
            index = found;
        }

        return GetLocation(context, index).Line;
    }

    /// <summary>
    /// Stops at the first parse error: the parser mends what it reports, so the elements would not be what was written, and mending misnested
    /// formatting rebuilds every open formatting element on each paragraph, which makes elements by the square of the tags.
    /// </summary>
    /// <returns>The document; <c>null</c> when the template does not read as HTML.</returns>
    private static IHtmlDocument Parse(CustomSectionCheck check, TemplateContext context)
    {
        var parser = new HtmlParser(new HtmlParserOptions { IsKeepingSourceReferences = true, IsStrictMode = true });
        try
        {
            return parser.ParseDocument(DocumentStart + context.Template);
        }
        catch (HtmlParseException error)
        {
            var (line, column) = GetLocation(context, GetIndex(context, error.Position.Position));
            var message = ((HtmlParseError)error.Code).GetMessage();
            check.AddError(string.Create(CultureInfo.InvariantCulture, $"the template does not read as HTML at line {line}, column {column}: {message}"));

            return null;
        }
    }

    // the parser counts lines without the line breaks of indentation, while its place in the text is right
    private static int GetIndex(TemplateContext context, int position) => Math.Clamp(position - 1 - DocumentStart.Length, 0, context.Template.Length);

    private static (int Line, int Column) GetLocation(TemplateContext context, int index)
    {
        var lineStarts = context.LineStarts;
        var found = lineStarts.BinarySearch(index);
        var line = found >= 0 ? found : ~found - 1;

        return (line + 1, index - lineStarts[line] + 1);
    }

    // text or a binding, which the storefront fills with text, an aria-label, rich text, or an image whose alt names it
    private static bool HasName(IElement link) =>
        !string.IsNullOrWhiteSpace(link.TextContent) ||
        !string.IsNullOrWhiteSpace(link.GetAttribute("aria-label")) ||
        link.HasAttribute(HtmlAttribute) ||
        link.QuerySelectorAll("[data-html]").Length > 0 ||
        link.QuerySelectorAll("img").Any(x => !string.IsNullOrWhiteSpace(x.GetAttribute(AltAttribute)));

    private static bool IsIsoDate(string value) =>
        IsoDatePattern.IsMatch(value) && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);

    private static string DescribeNumber(KitField field) => (field.Min, field.Max) switch
    {
        ({ } min, { } max) => string.Create(CultureInfo.InvariantCulture, $"a number from {FormatNumber(min)} to {FormatNumber(max)}"),
        ({ } min, null) => string.Create(CultureInfo.InvariantCulture, $"a number from {FormatNumber(min)}"),
        (null, { } max) => string.Create(CultureInfo.InvariantCulture, $"a number up to {FormatNumber(max)}"),
        _ => "a number",
    };

    private static string FormatNumber(decimal number) => number.ToString("0.##########", CultureInfo.InvariantCulture);

    /// <param name="Each">The list the element repeats for, or one of its ancestors does.</param>
    /// <param name="Repeating">The attribute of the nearest ancestor whose behavior repeats what it holds; <c>null</c> for none.</param>
    /// <param name="Behaviors">The behaviors the element and its ancestors switch on, whose options it may take.</param>
    protected sealed record TemplateScope(CustomSectionSetting Each, bool IsRoot, TemplateContext Context, string Repeating, IImmutableSet<string> Behaviors);

    /// <summary>The template as written, for the line numbers of errors, and the text it holds of its own, for the warning.</summary>
    protected sealed class TemplateContext(string template)
    {
        public string Template { get; } = template;

        public List<int> LineStarts { get; } = [0, .. template.Select((x, i) => (x, i)).Where(x => x.x == '\n').Select(x => x.i + 1)];

        public IList<string> Texts { get; } = [];
    }
}
