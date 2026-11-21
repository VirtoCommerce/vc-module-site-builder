using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Dom;
using AngleSharp.Html.Dom.Events;
using AngleSharp.Html.Parser;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VirtoCommerce.AssetsModule.Core.Assets;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Validation;
using VirtoCommerce.Platform.Core.Common;

namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <summary>The rules of <c>docs/contracts/custom-sections.md</c>: what the storefront would leave out of a type is refused here, with the rule it breaks.</summary>
public class CustomSectionValidator(IBlobUrlResolver blobUrlResolver) : ICustomSectionValidator
{
    private const int MaxPartSize = 64 * 1024;
    private const int MaxTotalSize = 256 * 1024;
    private const int MaxParseErrors = 3;
    private const int MaxSamples = 3;
    private const int MaxQuoteLength = 60;

    private const string DocumentStart = "<!DOCTYPE html><html><head></head><body>";
    private const string AssetProbe = "file";

    // the CSS parser writes 'transparent' so
    private const string TransparentColor = "rgba(0, 0, 0, 0)";

    private const string FormatKey = "format";
    private const string NameKey = "name";
    private const string SchemaKey = "schema";
    private const string TemplateKey = "template";
    private const string CssKey = "css";
    private const string SettingsKey = "settings";

    private const string StringFieldType = "string";
    private const string TextFieldType = "text";
    private const string ImagesFieldType = "images";
    private const string SelectFieldType = "select";
    private const string ListFieldType = "list";
    private const string ObjectFieldType = "object";
    private const string UrlFormat = "url";

    private const string EachAttribute = "data-each";
    private const string IfAttribute = "data-if";
    private const string HtmlAttribute = "data-html";
    private const string DataPrefix = "data-";

    private const string KitKey = "kit";
    private const string KitTagPrefix = "vc-";
    private const string LinkKitType = "link";
    private const string NumberKitType = "number";
    private const string BooleanKitType = "boolean";
    private const string SelectKitType = "select";

    private static readonly HashSet<string> TypeKeys = new(StringComparer.Ordinal) { FormatKey, NameKey, SchemaKey, TemplateKey, CssKey };

    private static readonly HashSet<string> SchemaKeys = new(StringComparer.Ordinal) { SettingsKey, "targetTemplates", "limit", "icon" };

    // no 'visibility' and no 'request': the designer runs the one and sends the other
    private static readonly HashSet<string> FieldKeys = new(StringComparer.Ordinal)
    {
        "id", "type", "label", "default", "translatable", "tab", "info", "placeholder", "required", "multiline", "multiple", "format", "options",
        "element", "min", "max", "step",
    };

    // the fields the designer shows as text to read, which hold no value
    private static readonly HashSet<string> LabelFieldTypes = new(StringComparer.Ordinal) { "header", "paragraph" };

    private static readonly HashSet<string> ValueFieldTypes = new(StringComparer.Ordinal)
    {
        StringFieldType, TextFieldType, "number", "slider", "checkbox", SelectFieldType, "color-token", ImagesFieldType, "calendar",
    };

    private static readonly HashSet<string> FormControls = new(StringComparer.Ordinal)
    {
        "form", "input", "select", "textarea", "button", "option", "optgroup", "datalist", "fieldset", "legend", "label", "output",
    };

    private static readonly HashSet<string> AllowedElements =
        SiteBuilderHtmlSanitizer.AllowedTags.Where(x => !FormControls.Contains(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.Ordinal)
    {
        "class", "role", "alt", "title", "lang", "dir", "hidden", "href", "target", "rel", "src", "srcset", "sizes", "loading", "decoding",
        "width", "height", "datetime", "colspan", "rowspan", "scope",
    };

    // people read what these hold, as they read the template's text
    private static readonly HashSet<string> TextAttributes = new(StringComparer.Ordinal) { "alt", "title", "aria-label", "label" };

    private static readonly string[] LinkSchemes = ["https:", "mailto:", "tel:"];

    private static readonly char[] Whitespace = [' ', '\t', '\n', '\r', '\f'];

    private static readonly string[] AllowedAtRules = ["media", "supports", "container", "keyframes"];

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex SettingIdPattern = new("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.None, RegexTimeout);
    private static readonly Regex BindingPattern = new(@"\{\{\s*([^{}]*?)\s*\}\}", RegexOptions.None, RegexTimeout);
    private static readonly Regex SingleBindingPattern = new(@"^\s*\{\{\s*([^{}]*?)\s*\}\}\s*$", RegexOptions.None, RegexTimeout);
    private static readonly Regex PathPattern = new(@"^(settings|item)\.([A-Za-z][A-Za-z0-9_]*)(?:\.([A-Za-z][A-Za-z0-9_]*))?$", RegexOptions.None, RegexTimeout);

    private static readonly Regex CssCommentPattern = new(@"/\*.*?(?:\*/|$)", RegexOptions.Singleline, RegexTimeout);
    private static readonly Regex CssEscapePattern = new(@"\\(?:([0-9a-fA-F]{1,6})[ \t\r\n\f]?|(.))", RegexOptions.Singleline, RegexTimeout);
    private static readonly Regex CssExpressionPattern = new(@"expression\s*\(", RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex CssScriptPropertyPattern = new(@"(?<![\w-])(behavior|-moz-binding)\s*:", RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex CssFixedPattern = new(@"(?<![\w-])position\s*:\s*fixed(?![\w-])", RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex CssUrlPattern = new(@"url\(\s*(?:""([^""]*)""|'([^']*)'|([^)""'\s]*))\s*\)", RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex CssImageSetPattern = new(@"image-set\(", RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex CssStringPattern = new(@"""([^""]*)""|'([^']*)'", RegexOptions.None, RegexTimeout);
    private static readonly Regex CssAtRulePattern = new(@"^\s*@(-?[A-Za-z][\w-]*)", RegexOptions.None, RegexTimeout);
    private static readonly Regex CssDocumentSelectorPattern = new(@"(?:^|[\s,>+~(])(?:html|body)(?=$|[\s,>+~.#:\[)])|:root(?![\w-])", RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex CssLiteralColorPattern = new(@"#[0-9a-fA-F]{3,8}\b|\b(?:rgba?|hsla?|hwb|lab|lch|oklab|oklch|color)\(", RegexOptions.IgnoreCase, RegexTimeout);

    public CustomSectionValidator()
        : this(blobUrlResolver: null)
    {
    }

    public virtual CustomSectionValidationResult Validate(string type, JToken content, JObject catalog)
    {
        var result = AbstractTypeFactory<CustomSectionValidationResult>.TryCreateInstance();
        var check = new CustomSectionCheck(type, result) { Kit = ReadKit(catalog) };
        if (!ThemeStorage.IsCustomSectionType(type))
        {
            check.AddError($"'{type}' is not a custom section type, which is 'custom-' and lowercase words joined by '-', at most 64 characters");
            return result;
        }

        if (content is not JObject file)
        {
            check.AddError("not an object");
            return result;
        }

        ValidateShape(check, file);

        if (GetPart(check, file, TemplateKey) is { } template && check.Settings != null)
        {
            ValidateTemplate(check, template);
        }

        if (GetPart(check, file, CssKey) is { } css)
        {
            ValidateCss(check, css);
        }

        AddWarnings(check);

        return result;
    }

    public virtual string GetSizeError(IDictionary<string, JToken> types)
    {
        var size = types.Values.Where(x => x != null).Sum(x => (long)Encoding.UTF8.GetByteCount(x.ToString(Formatting.None)));
        if (size <= MaxTotalSize)
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"{ThemeStorage.CustomSectionsFolder}: the custom section types are {FormatSize(size)} of JSON together, more than {FormatSize(MaxTotalSize)}: the snapshot carries every type to every page");
    }

    protected virtual void ValidateShape(CustomSectionCheck check, JObject file)
    {
        foreach (var key in file.Properties().Select(x => x.Name).Where(x => !TypeKeys.Contains(x)))
        {
            check.AddError($"'{key}' is not a key of a custom section type, which has format, name, schema, template and css");
        }

        if (file[FormatKey] is not JValue { Type: JTokenType.Integer } format || format.Value<long>() != 1)
        {
            check.AddError("'format' is not 1");
        }

        if (!IsName(file[NameKey]))
        {
            check.AddError("'name' is not a text, or a text per language");
        }

        if (file[SchemaKey] is JObject schema)
        {
            ValidateSchema(check, schema);
        }
        else
        {
            check.AddError("'schema' is not an object with the list of 'settings'");
        }
    }

    protected virtual void ValidateSchema(CustomSectionCheck check, JObject schema)
    {
        foreach (var key in schema.Properties().Select(x => x.Name).Where(x => !SchemaKeys.Contains(x)))
        {
            check.AddError($"'schema.{key}' is not a key of a custom section's schema, which has settings, targetTemplates, limit and icon");
        }

        if (schema[SettingsKey] is JArray settings)
        {
            check.Settings = ReadSettings(check, settings, parent: null);
        }
        else
        {
            check.AddError("'schema.settings' is not a list of settings");
        }

        if (schema["targetTemplates"] is { } targets && !(targets is JArray list && list.All(x => x is JValue { Type: JTokenType.String } key && !string.IsNullOrWhiteSpace((string)key))))
        {
            check.AddError("'schema.targetTemplates' is not a list of template names");
        }

        if (schema["limit"] is { } limit && !(limit is JValue { Type: JTokenType.Integer } count && count.Value<long>() >= 1))
        {
            check.AddError("'schema.limit' is not a whole number from 1");
        }

        if (schema["icon"] is { } icon && icon.Type != JTokenType.String)
        {
            check.AddError("'schema.icon' is not a text");
        }
    }

    protected virtual Dictionary<string, CustomSectionSetting> ReadSettings(CustomSectionCheck check, JArray fields, string parent)
    {
        var result = new Dictionary<string, CustomSectionSetting>(StringComparer.Ordinal);
        for (var i = 0; i < fields.Count; i++)
        {
            if (ReadSetting(check, fields[i], parent, i, result) is { } setting)
            {
                result[setting.Id] = setting;
            }
        }

        return result;
    }

    /// <returns><c>null</c> for a setting that is refused or holds no value.</returns>
    protected virtual CustomSectionSetting ReadSetting(CustomSectionCheck check, JToken token, string parent, int index,
        IDictionary<string, CustomSectionSetting> declared)
    {
        if (token is not JObject field)
        {
            check.AddError($"{DescribeSetting(parent, id: null, index)} is not an object");
            return null;
        }

        var id = GetString(field, "id");
        var type = GetString(field, "type");
        var setting = DescribeSetting(parent, id, index);
        foreach (var key in field.Properties().Select(x => x.Name).Where(x => !FieldKeys.Contains(x)))
        {
            check.AddError($"{setting} has a key a custom section's setting does not take, '{key}'");
        }

        var isGroup = type is ListFieldType or ObjectFieldType;
        if (type == null || !(LabelFieldTypes.Contains(type) || ValueFieldTypes.Contains(type) || isGroup))
        {
            check.AddError($"{setting} has a type a custom section does not take, '{type}'");
            return null;
        }

        if (isGroup && parent != null)
        {
            check.AddError($"{setting} is a {type} inside '{parent}', one level deeper than a custom section's settings go");
            return null;
        }

        if (LabelFieldTypes.Contains(type))
        {
            return null;
        }

        if (id == null || !SettingIdPattern.IsMatch(id))
        {
            check.AddError($"{setting} has no id of letters, digits and '_' that starts with a letter");
            return null;
        }

        if (declared.ContainsKey(id))
        {
            check.AddError($"{setting} has the id of another setting");
            return null;
        }

        ValidateSettingKeys(check, field, type, setting);

        var element = isGroup ? ReadElement(check, field, id, type, setting) : null;
        var isLink = type == StringFieldType && GetString(field, FormatKey) == UrlFormat;

        return new CustomSectionSetting(id, type, isLink, element);
    }

    protected virtual void ValidateSettingKeys(CustomSectionCheck check, JObject field, string type, string setting)
    {
        if (IsSet(field, "translatable") && type is not (StringFieldType or TextFieldType))
        {
            check.AddError($"{setting} is translatable, which only a string or a text is");
        }

        if (field[FormatKey] is { } format && !(type == StringFieldType && format is JValue { Type: JTokenType.String } url && (string)url == UrlFormat))
        {
            check.AddError($"{setting} has a format other than the \"url\" of a string that holds a link");
        }

        if (IsSet(field, "multiple"))
        {
            check.AddError($"{setting} is multiple, and a custom section's setting holds one value");
        }

        if (type == SelectFieldType)
        {
            if (field["options"] is not JArray { Count: > 0 } options || !options.All(IsOption))
            {
                check.AddError($"{setting} is a select without a list of options, each with a value");
            }
        }
        else if (field["options"] != null)
        {
            check.AddError($"{setting} has options, which only a select has");
        }

        if (type is not (ListFieldType or ObjectFieldType) && field["element"] != null)
        {
            check.AddError($"{setting} has an element, which only a list or an object has");
        }
    }

    protected virtual Dictionary<string, CustomSectionSetting> ReadElement(CustomSectionCheck check, JObject field, string id, string type, string setting)
    {
        if (field["element"] is JArray { Count: > 0 } fields)
        {
            return ReadSettings(check, fields, id);
        }

        check.AddError($"{setting} is {(type == ListFieldType ? "a list without the fields of its rows" : "an object without its fields")} under 'element'");

        return new Dictionary<string, CustomSectionSetting>(StringComparer.Ordinal);
    }

    protected virtual void ValidateTemplate(CustomSectionCheck check, string template)
    {
        var parser = new HtmlParser(new HtmlParserOptions { IsKeepingSourceReferences = true });
        var parseErrors = new List<HtmlErrorEvent>();
        parser.Error += (_, error) => parseErrors.Add((HtmlErrorEvent)error);

        using var document = parser.ParseDocument(DocumentStart + template);
        check.Template = template;
        if (parseErrors.Count > 0)
        {
            // the parser has mended what it reports, so the elements are not what was written
            foreach (var error in parseErrors.Take(MaxParseErrors))
            {
                var (line, column) = GetLocation(check, GetIndex(check, error.Position.Position));
                check.AddError(string.Create(CultureInfo.InvariantCulture, $"the template does not read as HTML at line {line}, column {column}: {error.Message}"));
            }

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

        ValidateElement(check, roots[0], new TemplateScope(Each: null, IsRoot: true));
    }

    protected virtual void ValidateElement(CustomSectionCheck check, IElement element, TemplateScope scope)
    {
        var tag = element.LocalName;
        var where = string.Create(CultureInfo.InvariantCulture, $"<{tag}> at line {GetLine(check, element)}");
        IReadOnlyDictionary<string, KitField> kitAttributes = null;
        if (tag.StartsWith(KitTagPrefix, StringComparison.Ordinal))
        {
            if (GetKitTagError(check, tag) is { } error)
            {
                check.AddError($"{where} {error}");
                return;
            }

            kitAttributes = check.Kit.Tags[tag];
        }
        else if (!AllowedElements.Contains(tag))
        {
            check.AddError($"{where} is not an element a custom section can hold");
            return;
        }

        var each = element.GetAttribute(EachAttribute) is { } eachPath ? ValidateEach(check, eachPath, where, scope) : scope.Each;
        var elementScope = scope with { Each = each };
        foreach (var attribute in element.Attributes)
        {
            ValidateAttribute(check, element, attribute.Name, attribute.Value, kitAttributes, where, elementScope);
        }

        var childScope = new TemplateScope(each, IsRoot: false);
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
                    AddText(check, ValidateBindings(check, text.Data, where, childScope));
                }
            }
        }

        if (holdsNothing && hasContent)
        {
            check.AddError(kitAttributes != null ? $"{where} has content, which a kit tag does not have" : $"{where} has data-html and content of its own");
        }
    }

    /// <summary>A kit tag is one the storefront names in its catalog, as a module of the storefront may add its own.</summary>
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

        return check.Kit.Tags.ContainsKey(tag) ? null : "is not a kit tag the storefront offers";
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

        if (kitAttributes != null)
        {
            if (kitAttributes.TryGetValue(name, out var field))
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
        else if (name is "src" or "srcset")
        {
            ValidateImage(check, name, value, where, scope);
        }
        else if (AllowedAttributes.Contains(name) || name.StartsWith("aria-", StringComparison.Ordinal))
        {
            var text = ValidateBindings(check, value, where, scope);
            if (TextAttributes.Contains(name))
            {
                AddText(check, text);
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

        if (setting.Type != ListFieldType)
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
        if (ResolvePath(check, path, where, scope) is { } setting && setting.Type != TextFieldType)
        {
            check.AddError($"{where} has data-html=\"{Quote(path)}\", a {setting.Type}; it names a text setting, whose value is HTML");
        }
    }

    /// <summary>A value of a kit tag's attribute or of a behavior's option, checked by the type the storefront declares for it.</summary>
    /// <param name="owner">The kit tag or the behavior attribute, which names what takes the value.</param>
    protected virtual void ValidateKitValue(CustomSectionCheck check, string owner, KitField field, string name, string value, string where,
        TemplateScope scope)
    {
        if (field.Type == LinkKitType)
        {
            ValidateLink(check, name, value, where, scope);
            return;
        }

        if (field.Type is not (NumberKitType or BooleanKitType or SelectKitType))
        {
            var text = ValidateBindings(check, value, where, scope);
            if (TextAttributes.Contains(name))
            {
                AddText(check, text);
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
        else if (GetKitValueRule(field, value) is { } rule)
        {
            check.AddError($"{where} has {name}=\"{Quote(value)}\", which {owner} does not take: it takes {rule}");
        }
    }

    /// <returns>What the field takes, when the value is not one of it; <c>null</c> for a value it takes.</returns>
    protected virtual string GetKitValueRule(KitField field, string value)
    {
        switch (field.Type)
        {
            case BooleanKitType:
                return value is "" or "true" or "false" ? null : "true, false or no value";
            case SelectKitType:
                return field.Options == null || field.Options.Contains(value, StringComparer.Ordinal) ? null : string.Join(", ", field.Options);
            default:
                var isNumber = decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number);
                return isNumber && !(number < field.Min) && !(number > field.Max) ? null : DescribeNumber(field);
        }
    }

    /// <summary>
    /// A behavior's option is an attribute of its own, data-{behavior}-{option}, on the element with the behavior or inside it: the countdown's
    /// parts are elements inside the countdown. The behavior's own value is the storefront's to read.
    /// </summary>
    protected virtual void ValidateBehavior(CustomSectionCheck check, IElement element, string behavior, string option, string name, string value,
        string where, TemplateScope scope)
    {
        if (option == null)
        {
            ValidateBindings(check, value, where, scope);
            return;
        }

        var attribute = DataPrefix + behavior;
        if (!check.Kit.Behaviors[behavior].TryGetValue(option, out var field))
        {
            check.AddError($"{where} has {name}, which is not an option {attribute} takes");
            return;
        }

        if (!HasBehavior(element, attribute))
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

        var behavior = check.Kit.Behaviors.Keys.Where(x => name.StartsWith(x + "-", StringComparison.Ordinal)).MaxBy(x => x.Length);

        return behavior == null ? null : (behavior, name[(behavior.Length + 1)..]);
    }

    /// <summary>The kit of the storefront's catalog: its kit tags, which start with vc-, and its behaviors, each with the fields it takes.</summary>
    protected virtual StorefrontKit ReadKit(JObject catalog)
    {
        if (catalog?[KitKey] is not JObject kit)
        {
            return new StorefrontKit(catalog != null, Tags: null, Behaviors: null);
        }

        return new StorefrontKit(true, ReadKitEntries(kit["tags"], "attributes", KitTagPrefix), ReadKitEntries(kit["behaviors"], "options", string.Empty));
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
        else if (!IsLink(value))
        {
            check.AddError($"{where} has {name}=\"{Quote(value)}\", which is not a path starting with '/', or an https:, mailto: or tel: URL");
        }
    }

    protected virtual void ValidateImage(CustomSectionCheck check, string name, string value, string where, TemplateScope scope)
    {
        if (GetSingleBinding(value) is { } path)
        {
            if (ResolveValue(check, path, where, scope) is { } setting && setting.Type != ImagesFieldType)
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

        // a srcset is a list of URLs, each with its width or density after it
        var urls = name == "srcset"
            ? value.Split(',').Select(x => x.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty)
            : [value];
        foreach (var url in urls.Where(x => !IsAssetUrl(x)))
        {
            check.AddError($"{where} has {name} with '{Quote(url)}', which is not a URL of the store's asset library");
        }
    }

    /// <summary>Checks the bindings in a text or an attribute value; returns the text without them.</summary>
    protected virtual string ValidateBindings(CustomSectionCheck check, string text, string where, TemplateScope scope)
    {
        var literal = BindingPattern.Replace(text, match =>
        {
            ResolveValue(check, match.Groups[1].Value, where, scope);
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
            check.AddError(setting.Type == ListFieldType
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

        if (setting.Type != ObjectFieldType)
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

    protected virtual void ValidateCss(CustomSectionCheck check, string css)
    {
        if (string.IsNullOrWhiteSpace(css))
        {
            return;
        }

        // the storefront wraps the CSS in its own blocks, which a stray brace would close
        if (!HasBalancedBraces(css))
        {
            check.AddError("the CSS has a '{' or a '}' without its pair");
            return;
        }

        // as a browser reads it: escapes such as u\72l( are the same as url(
        var code = CssEscapePattern.Replace(CssCommentPattern.Replace(css, " "), UnescapeCss);
        if (CssExpressionPattern.IsMatch(code))
        {
            check.AddError("the CSS has expression(), which runs script");
        }

        foreach (var property in CssScriptPropertyPattern.Matches(code).Select(x => x.Groups[1].Value.ToLowerInvariant()).Distinct())
        {
            check.AddError($"the CSS sets {property}, which runs script");
        }

        if (CssFixedPattern.IsMatch(code))
        {
            check.AddError("the CSS has position: fixed, which puts the section over the page");
        }

        foreach (var url in GetCssUrls(code).Where(x => !IsAssetUrl(x)).Distinct(StringComparer.Ordinal))
        {
            check.AddError($"the CSS loads '{Quote(url)}', which is not a URL of the store's asset library");
        }

        ICssStyleSheet sheet;
        try
        {
            sheet = new CssParser(new CssParserOptions { IsIncludingUnknownDeclarations = true, IsIncludingUnknownRules = true, IsToleratingInvalidSelectors = true })
                .ParseStyleSheet(css);
        }
        catch (DomException ex)
        {
            check.AddError($"the CSS does not read: {ex.Message}");
            return;
        }

        ValidateCssRules(check, sheet.Rules);
    }

    protected virtual void ValidateCssRules(CustomSectionCheck check, ICssRuleList rules)
    {
        foreach (var rule in rules ?? Enumerable.Empty<ICssRule>())
        {
            switch (rule)
            {
                case ICssStyleRule style:
                    if (CssDocumentSelectorPattern.IsMatch(style.SelectorText ?? string.Empty))
                    {
                        check.AddError($"the CSS selector '{Quote(style.SelectorText)}' reaches :root, html or body, outside the section");
                    }

                    CollectColors(check, style.Style);
                    ValidateCssRules(check, style.Rules);
                    break;
                case ICssKeyframesRule keyframes:
                    if (keyframes.Name != check.Type && !keyframes.Name.StartsWith(check.Type + "-", StringComparison.Ordinal))
                    {
                        check.AddError($"the CSS names @keyframes '{keyframes.Name}', which does not start with the type, '{check.Type}': names are not scoped");
                    }

                    foreach (var keyframe in keyframes.Rules.OfType<ICssKeyframeRule>())
                    {
                        CollectColors(check, keyframe.Style);
                    }

                    break;
                case ICssMediaRule or ICssSupportsRule or ICssContainerRule:
                    ValidateCssRules(check, ((ICssGroupingRule)rule).Rules);
                    break;
                default:
                    if (CssAtRulePattern.Match(rule.CssText) is { Success: true } atRule && !AllowedAtRules.Contains(atRule.Groups[1].Value.ToLowerInvariant()))
                    {
                        check.AddError($"the CSS has @{atRule.Groups[1].Value}, and a custom section's CSS has only @media, @supports, @container and @keyframes");
                    }

                    break;
            }
        }
    }

    protected virtual void CollectColors(CustomSectionCheck check, ICssStyleDeclaration style)
    {
        foreach (var property in style ?? Enumerable.Empty<ICssProperty>())
        {
            if (property.Value != TransparentColor && CssLiteralColorPattern.IsMatch(property.Value ?? string.Empty))
            {
                check.ColorProperties.Add(property.Name);
            }
        }
    }

    protected virtual void AddWarnings(CustomSectionCheck check)
    {
        if (check.Texts.Count > 0)
        {
            var texts = string.Join(", ", check.Texts.Distinct(StringComparer.Ordinal).Take(MaxSamples).Select(x => $"'{Quote(x)}'"));
            check.AddWarning($"the template holds text of its own ({texts}), which is the same in every language: bind it to a translatable setting");
        }

        if (check.ColorProperties.Count > 0)
        {
            var properties = string.Join(", ", check.ColorProperties.Distinct(StringComparer.Ordinal).Take(MaxSamples));
            check.AddWarning($"the CSS sets literal colors ({properties}), which the palette does not change: use var(--color-…) or a color-token setting's variable");
        }
    }

    /// <summary>A URL of the asset library starts with its public address, as the link of each of its files does.</summary>
    protected virtual bool IsAssetUrl(string url) =>
        GetAssetsBaseUrl() is { } baseUrl &&
        !url.Any(char.IsControl) &&
        url.StartsWith(baseUrl, StringComparison.OrdinalIgnoreCase) &&
        Uri.TryCreate(url, UriKind.Absolute, out _);

    /// <summary>The public link of a file at the library's root without the file; <c>null</c> when the library gives none.</summary>
    protected virtual string GetAssetsBaseUrl()
    {
        string url;
        try
        {
            url = blobUrlResolver?.GetAbsoluteUrl(AssetProbe);
        }
        catch (UriFormatException)
        {
            return null;
        }

        return url != null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && url.EndsWith("/" + AssetProbe, StringComparison.Ordinal)
            ? url[..^AssetProbe.Length]
            : null;
    }

    /// <summary>A URL parser drops tabs and line breaks, and reads '\' as '/', so either could make a path start with '//', another site.</summary>
    protected virtual bool IsLink(string url)
    {
        if (url.Length == 0 || url.Any(char.IsControl))
        {
            return false;
        }

        if (url.StartsWith('/'))
        {
            return url.Length == 1 || url[1] is not ('/' or '\\');
        }

        return LinkSchemes.Any(x => url.StartsWith(x, StringComparison.OrdinalIgnoreCase)) && Uri.TryCreate(url, UriKind.Absolute, out _);
    }

    private static string GetPart(CustomSectionCheck check, JObject file, string key)
    {
        if (file[key] is not JValue { Type: JTokenType.String } value)
        {
            check.AddError($"'{key}' is not a text");
            return null;
        }

        var text = (string)value;
        var size = Encoding.UTF8.GetByteCount(text);
        if (size > MaxPartSize)
        {
            check.AddError(string.Create(CultureInfo.InvariantCulture, $"'{key}' is {FormatSize(size)}, more than {FormatSize(MaxPartSize)}"));
            return null;
        }

        return text;
    }

    private static IEnumerable<string> GetCssUrls(string code)
    {
        foreach (Match match in CssUrlPattern.Matches(code))
        {
            yield return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
        }

        // image-set() takes a URL as a string too
        foreach (Match match in CssImageSetPattern.Matches(code))
        {
            var end = FindClosingParenthesis(code, match.Index + match.Length);
            foreach (Match text in CssStringPattern.Matches(code[(match.Index + match.Length)..end]))
            {
                yield return text.Groups[1].Success ? text.Groups[1].Value : text.Groups[2].Value;
            }
        }
    }

    private static int FindClosingParenthesis(string code, int start)
    {
        var depth = 1;
        for (var i = start; i < code.Length; i++)
        {
            depth += code[i] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0)
            {
                return i;
            }
        }

        return code.Length;
    }

    private static bool HasBalancedBraces(string css)
    {
        var depth = 0;
        for (var i = 0; i < css.Length; i++)
        {
            var character = css[i];
            if (character == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? css.Length : end + 1;
            }
            else if (character is '"' or '\'')
            {
                i = SkipString(css, i);
            }
            else if (character == '\\')
            {
                i++;
            }
            else if (character == '{')
            {
                depth++;
            }
            else if (character == '}' && --depth < 0)
            {
                return false;
            }
        }

        return depth == 0;
    }

    // a string ends at its quote, or unclosed at a line break
    private static int SkipString(string css, int start)
    {
        var quote = css[start];
        for (var i = start + 1; i < css.Length; i++)
        {
            if (css[i] == '\\')
            {
                i++;
            }
            else if (css[i] == quote || css[i] == '\n')
            {
                return i;
            }
        }

        return css.Length;
    }

    private static string UnescapeCss(Match match)
    {
        if (!match.Groups[1].Success)
        {
            return match.Groups[2].Value;
        }

        var code = int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        return code is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32(code) : "�";
    }

    private static void AddText(CustomSectionCheck check, string text)
    {
        // text without a letter, such as '+' after a number, reads the same in every language
        if (text.Any(char.IsLetter))
        {
            check.Texts.Add(string.Join(' ', text.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries)));
        }
    }

    private static string GetSingleBinding(string value)
    {
        var match = SingleBindingPattern.Match(value);

        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool HasBinding(string value) => value.Contains("{{", StringComparison.Ordinal);

    // an element of SVG keeps no place of its own, so it is looked for after its parent's
    private static int GetLine(CustomSectionCheck check, IElement element)
    {
        var placed = element;
        while (placed.SourceReference == null && placed.ParentElement != null)
        {
            placed = placed.ParentElement;
        }

        var index = GetIndex(check, placed.SourceReference?.Position.Position ?? 0);
        if (placed != element && check.Template.IndexOf("<" + element.LocalName, index, StringComparison.OrdinalIgnoreCase) is >= 0 and var found)
        {
            index = found;
        }

        return GetLocation(check, index).Line;
    }

    // the parser counts lines without the line breaks of indentation, while its place in the text is right
    private static int GetIndex(CustomSectionCheck check, int position) => Math.Clamp(position - 1 - DocumentStart.Length, 0, check.Template.Length);

    private static (int Line, int Column) GetLocation(CustomSectionCheck check, int index)
    {
        var lineStart = index == 0 ? 0 : check.Template.LastIndexOf('\n', index - 1) + 1;

        return (check.Template.AsSpan(0, index).Count('\n') + 1, index - lineStart + 1);
    }

    private static bool IsName(JToken name) => name switch
    {
        JValue { Type: JTokenType.String } text => !string.IsNullOrWhiteSpace((string)text),
        JObject translations => translations.Count > 0 && translations.Properties().All(x => x.Value is JValue { Type: JTokenType.String } text && !string.IsNullOrWhiteSpace((string)text)),
        _ => false,
    };

    private static bool IsOption(JToken option) =>
        option is JObject { } item && item["value"] is JValue { Type: JTokenType.String or JTokenType.Integer or JTokenType.Float };

    private static bool IsSet(JObject field, string name) => field[name] is JValue { Type: JTokenType.Boolean } flag && (bool)flag;

    private static string GetString(JObject item, string name) => item[name] is JValue { Type: JTokenType.String } value ? (string)value : null;

    private static string DescribeSetting(string parent, string id, int index) => (parent, id) switch
    {
        (null, null) => string.Create(CultureInfo.InvariantCulture, $"setting {index + 1}"),
        (null, _) => $"setting '{id}'",
        (_, null) => string.Create(CultureInfo.InvariantCulture, $"setting {index + 1} of '{parent}'"),
        _ => $"setting '{parent}.{id}'",
    };

    private static bool HasBehavior(IElement element, string attribute)
    {
        for (var current = element; current != null; current = current.ParentElement)
        {
            if (current.HasAttribute(attribute))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, IReadOnlyDictionary<string, KitField>> ReadKitEntries(JToken entries, string fieldsKey, string prefix) =>
        (entries as JObject)?.Properties()
            .Where(x => x.Name.StartsWith(prefix, StringComparison.Ordinal) && x.Name.Length > prefix.Length && x.Value is JObject)
            .ToDictionary(x => x.Name, x => ReadKitFields(x.Value[fieldsKey]), StringComparer.Ordinal)
        ?? new Dictionary<string, IReadOnlyDictionary<string, KitField>>(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, KitField> ReadKitFields(JToken fields) =>
        (fields as JObject)?.Properties()
            .Where(x => x.Value is JObject)
            .ToDictionary(x => x.Name, x => ReadKitField((JObject)x.Value), StringComparer.Ordinal)
        ?? new Dictionary<string, KitField>(StringComparer.Ordinal);

    // an option is a value, or an object with its value and its label
    private static KitField ReadKitField(JObject field) => new(
        GetString(field, "type"),
        (field["options"] as JArray)?
            .Select(x => x is JObject option ? option["value"] : x)
            .OfType<JValue>()
            .Select(x => Convert.ToString(x.Value, CultureInfo.InvariantCulture))
            .ToList(),
        GetNumber(field, "min"),
        GetNumber(field, "max"));

    private static decimal? GetNumber(JObject item, string name) =>
        item[name] is JValue { Type: JTokenType.Integer or JTokenType.Float } value ? value.Value<decimal>() : null;

    private static string DescribeNumber(KitField field) => (field.Min, field.Max) switch
    {
        ({ } min, { } max) => string.Create(CultureInfo.InvariantCulture, $"a number from {FormatNumber(min)} to {FormatNumber(max)}"),
        ({ } min, null) => string.Create(CultureInfo.InvariantCulture, $"a number from {FormatNumber(min)}"),
        (null, { } max) => string.Create(CultureInfo.InvariantCulture, $"a number up to {FormatNumber(max)}"),
        _ => "a number",
    };

    private static string FormatNumber(decimal number) => number.ToString("0.##########", CultureInfo.InvariantCulture);

    private static string FormatSize(long size) => string.Create(CultureInfo.InvariantCulture, $"{size / 1024.0:0.#} KB");

    // a value goes into an error shortened and on one line
    private static string Quote(string value)
    {
        var line = string.Join(' ', value.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries));

        return line.Length > MaxQuoteLength ? line[..MaxQuoteLength] + "…" : line;
    }

    /// <summary>A setting a binding can name; <see cref="Element"/> holds the fields of a list's rows or of an object.</summary>
    protected sealed record CustomSectionSetting(string Id, string Type, bool IsLink, IReadOnlyDictionary<string, CustomSectionSetting> Element)
    {
        /// <summary>The list of a data-each that names none: the items of its rows are not checked.</summary>
        public static CustomSectionSetting UnknownList { get; } = new(string.Empty, ListFieldType, IsLink: false, Element: null);
    }

    /// <param name="Each">The list the element repeats for, or one of its ancestors does.</param>
    protected sealed record TemplateScope(CustomSectionSetting Each, bool IsRoot);

    /// <param name="Type">string, number, boolean, select or link; any other is read as a string.</param>
    /// <param name="Options">The values a select takes.</param>
    protected sealed record KitField(string Type, IReadOnlyList<string> Options, decimal? Min, decimal? Max);

    /// <param name="HasCatalog">A catalog was given: without one, nothing is known of the kit.</param>
    /// <param name="Tags">The attributes of each kit tag, by its name; <c>null</c> for a catalog without a kit.</param>
    /// <param name="Behaviors">The options of each behavior, by its name; <c>null</c> for a catalog without a kit.</param>
    protected sealed record StorefrontKit(bool HasCatalog, IReadOnlyDictionary<string, IReadOnlyDictionary<string, KitField>> Tags,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, KitField>> Behaviors);

    protected sealed class CustomSectionCheck(string type, CustomSectionValidationResult result)
    {
        public string Type { get; } = type;

        public string File { get; } = ThemeStorage.GetCustomSectionFile(type);

        public StorefrontKit Kit { get; init; }

        /// <summary>The settings a binding can name, by id; <c>null</c> when the schema cannot be read.</summary>
        public IDictionary<string, CustomSectionSetting> Settings { get; set; }

        /// <summary>The template as written, which names the line of an element.</summary>
        public string Template { get; set; } = string.Empty;

        public IList<string> Texts { get; } = [];

        public IList<string> ColorProperties { get; } = [];

        public void AddError(string error) => result.Errors.Add($"{File}: {error}");

        public void AddWarning(string warning) => result.Warnings.Add($"{File}: {warning}");
    }
}
