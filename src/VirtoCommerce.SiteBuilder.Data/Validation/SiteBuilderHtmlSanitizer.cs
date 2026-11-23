using AngleSharp.Dom;
using Ganss.Xss;
using VirtoCommerce.SiteBuilder.Core.Services;

namespace VirtoCommerce.SiteBuilder.Data.Validation;

/// <summary>The storefront's sanitizer profile, with fewer elements and an allowlist of URL schemes.</summary>
public class SiteBuilderHtmlSanitizer : ISiteBuilderHtmlSanitizer
{
    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abbr", "acronym", "address", "area", "article", "aside", "audio", "b", "bdi", "bdo", "big", "blink", "blockquote", "br",
        "button", "canvas", "caption", "center", "cite", "code", "col", "colgroup", "data", "datalist", "dd", "del", "details", "dfn",
        "dialog", "dir", "div", "dl", "dt", "em", "fieldset", "figcaption", "figure", "font", "footer", "h1", "h2", "h3", "h4", "h5", "h6",
        "header", "hgroup", "hr", "i", "img", "input", "ins", "kbd", "label", "legend", "li", "main", "map", "mark", "marquee", "menu",
        "menuitem", "meter", "nav", "nobr", "ol", "optgroup", "option", "output", "p", "picture", "pre", "progress", "q", "rp", "rt",
        "ruby", "s", "samp", "section", "select", "small", "source", "spacer", "span", "strike", "strong", "sub", "summary", "sup",
        "table", "tbody", "td", "textarea", "tfoot", "th", "thead", "time", "tr", "track", "tt", "u", "ul", "var", "video", "wbr",
    };

    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "accept", "action", "align", "alt", "autocapitalize", "autocomplete", "autopictureinpicture", "autoplay", "background", "bgcolor",
        "border", "capture", "cellpadding", "cellspacing", "checked", "cite", "class", "clear", "color", "cols", "colspan", "controls",
        "controlslist", "coords", "crossorigin", "datetime", "decoding", "default", "dir", "disabled", "disablepictureinpicture",
        "disableremoteplayback", "download", "draggable", "enctype", "enterkeyhint", "face", "for", "headers", "height", "hidden", "high",
        "href", "hreflang", "id", "inputmode", "integrity", "ismap", "kind", "label", "lang", "list", "loading", "loop", "low", "max",
        "maxlength", "media", "method", "min", "minlength", "multiple", "muted", "name", "noshade", "novalidate", "nowrap", "open",
        "optimum", "pattern", "placeholder", "playsinline", "poster", "preload", "pubdate", "radiogroup", "readonly", "rel", "required",
        "rev", "reversed", "role", "rows", "rowspan", "spellcheck", "scope", "selected", "shape", "size", "sizes", "span", "srclang",
        "start", "src", "srcset", "step", "style", "summary", "tabindex", "target", "title", "translate", "type", "usemap", "valign",
        "value", "width", "wrap",
    };

    private static readonly HashSet<string> UriAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "action", "background", "cite", "dynsrc", "href", "lowsrc", "poster", "src",
    };

    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto", "tel", "sms" };

    // DOMPurify drops what these hold along with them; any other element removed leaves its content.
    private static readonly HashSet<string> DropContentTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "template", "iframe", "frame", "frameset", "noframes", "noembed", "noscript", "object", "embed", "applet",
        "svg", "math", "title", "xmp", "plaintext", "head", "form",
    };

    private static readonly HashSet<string> SameContextTargets = new(StringComparer.OrdinalIgnoreCase) { string.Empty, "_self", "_parent", "_top" };

    private static readonly string[] NewWindowRelTokens = ["noopener", "noreferrer"];

    private const string ImageDataUrlPrefix = "data:image/";

    public virtual string Sanitize(string html)
    {
        // without a tag there is nothing that can run
        if (string.IsNullOrEmpty(html) || !html.Contains('<'))
        {
            return html;
        }

        // the serialized result, never the input: an input the parser cut short (an unclosed tag) runs once more markup follows it
        return CreateSanitizer().Sanitize(html);
    }

    protected virtual HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer(new HtmlSanitizerOptions
        {
            AllowedTags = AllowedTags,
            AllowedAttributes = AllowedAttributes,
            UriAttributes = UriAttributes,
            AllowedSchemes = AllowedSchemes,
            AllowedCssProperties = HtmlSanitizerDefaults.AllowedCssProperties,
            AllowedAtRules = HtmlSanitizerDefaults.AllowedAtRules,
        })
        {
            AllowDataAttributes = true,
            KeepChildNodes = true,
        };

        sanitizer.RemovingTag += (_, e) =>
        {
            if (DropContentTags.Contains(e.Tag.LocalName))
            {
                foreach (var child in e.Tag.ChildNodes.ToList())
                {
                    e.Tag.RemoveChild(child);
                }
            }
        };
        sanitizer.RemovingAttribute += (_, e) =>
        {
            // ARIA attributes are allowed by prefix, as DOMPurify does
            if (e.Reason == RemoveReason.NotAllowedAttribute && e.Attribute.Name.StartsWith("aria-", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
            }
        };
        sanitizer.FilterUrl += (_, e) =>
        {
            // an image cannot run what it shows, so it may be inline
            if (e.SanitizedUrl == null && e.Tag.LocalName == "img" && e.OriginalUrl.TrimStart().StartsWith(ImageDataUrlPrefix, StringComparison.OrdinalIgnoreCase))
            {
                e.SanitizedUrl = e.OriginalUrl;
            }
        };
        sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is IElement element)
            {
                AddNewWindowRel(element);
            }
        };

        return sanitizer;
    }

    /// <summary>A link that opens a new window gets no handle to this one, as the storefront's sanitizer sees to.</summary>
    protected virtual void AddNewWindowRel(IElement element)
    {
        var target = element.GetAttribute("target");
        if (target == null || SameContextTargets.Contains(target))
        {
            return;
        }

        var rel = (element.GetAttribute("rel") ?? string.Empty).Split(' ', '\t', '\n', '\f', '\r').Where(x => x.Length > 0).ToList();
        var missing = NewWindowRelTokens.Where(x => !rel.Contains(x, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count > 0)
        {
            element.SetAttribute("rel", string.Join(' ', rel.Concat(missing)));
        }
    }
}
