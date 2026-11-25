namespace VirtoCommerce.SiteBuilder.MCP.Services;

public static class SettingValues
{
    public const string Description = "A value takes the form its setting's type asks for in the section schema " +
        "(site-builder://stores/{store_id}/schemas/sections/{type}): a 'text' or 'html' setting holds HTML; a 'markdown' setting " +
        "holds Markdown when its resultType is markdown, HTML when it is html, and otherwise " +
        "{ \"markdown\": \"...\", \"html\": \"...\" } with the same text in both, but HTML as a language's text in a text per " +
        "language. The storefront shows the HTML, so Markdown given where HTML is expected appears as written.";

    public const string TextDescription = "The translated text, in the form its setting holds: HTML for a 'text' or 'html' " +
        "setting and for a 'markdown' setting, Markdown only when the markdown setting's resultType is markdown.";
}
