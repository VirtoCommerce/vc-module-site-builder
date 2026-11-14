using VirtoCommerce.SiteBuilder.Data.Validation;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

public class SiteBuilderHtmlSanitizerTests
{
    private readonly SiteBuilderHtmlSanitizer _sanitizer = new();

    [Theory]
    [InlineData("<p>Plain <strong>text</strong> &amp; more</p>")]
    [InlineData("<h2 id=\"shipping-and-returns\">Shipping</h2>")]
    [InlineData("<a href=\"/catalog/bolts\">Bolts</a> <a href=\"#faq\">FAQ</a> <a href=\"?page=2\">Next</a> <a href=\"../up\">Up</a>")]
    [InlineData("<a href=\"https://example.com\">Site</a> <a href=\"http://example.com\">Site</a>")]
    [InlineData("<a href=\"mailto:sales@example.com\">Mail</a> <a href=\"tel:+15550100\">Call</a> <a href=\"sms:+15550100\">Text</a>")]
    [InlineData("<a href=\"https://example.com\" target=\"_blank\" rel=\"noopener noreferrer\">New window</a>")]
    [InlineData("<a href=\"/x\" target=\"_self\">Same window</a>")]
    [InlineData("<img src=\"/assets/a.png\" alt=\"A\" width=\"10\"><img src=\"data:image/png;base64,iVBORw0KGgo=\" alt=\"inline\">")]
    [InlineData("<ul><li>One</li><li>Two</li></ul><ol start=\"3\"><li>Three</li></ol><blockquote>Quote</blockquote><hr>")]
    [InlineData("<table><thead><tr><th scope=\"col\">A</th></tr></thead><tbody><tr><td colspan=\"2\">1</td></tr></tbody></table>")]
    [InlineData("<pre><code class=\"language-js\">if (a &lt; b) { run(); }</code></pre>")]
    [InlineData("<p class=\"lead\" data-track=\"hero\" aria-label=\"Lead\" title=\"T\">Lead</p>")]
    [InlineData("<p><del>old</del> <em>new</em><br>line</p>")]
    [InlineData("Tom &amp; Jerry, \"quoted\"")]
    [InlineData("A text with no markup: Tom & Jerry")]
    public void Sanitize_SafeHtml_ReturnsItAsSaved(string html)
    {
        Assert.Equal(html, _sanitizer.Sanitize(html));
    }

    [Theory]
    [InlineData("By clicking &quot;Submit&quot;, I agree to the <a href='/terms'>Terms</a>.", "By clicking \"Submit\", I agree to the <a href=\"/terms\">Terms</a>.")]
    [InlineData("a < b <b>bold</b>", "a &lt; b <b>bold</b>")]
    [InlineData("<P CLASS=x>Upper</P>", "<p class=\"x\">Upper</p>")]
    [InlineData("<p style=\"color: red; font-weight: bold\">Styled</p>", "<p style=\"color: rgba(255, 0, 0, 1); font-weight: bold\">Styled</p>")]
    public void Sanitize_SafeHtmlWrittenOtherwise_ReturnsItSerializedOnce(string html, string expected)
    {
        var sanitized = _sanitizer.Sanitize(html);

        Assert.Equal(expected, sanitized);
        Assert.Equal(sanitized, _sanitizer.Sanitize(sanitized));
    }

    [Theory]
    [InlineData("<p>a</p><script>alert(1)</script>", "<p>a</p>")]
    [InlineData("<img src=x onerror=alert(1)>", "<img src=\"x\">")]
    [InlineData("<svg onload=alert(1)><circle r=\"1\"></circle></svg>ok", "ok")]
    [InlineData("<svg><script>alert(1)</script></svg>", "")]
    [InlineData("<iframe srcdoc=\"<script>alert(1)</script>\"></iframe>x", "x")]
    [InlineData("<iframe src=\"javascript:alert(1)\"></iframe>", "")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>", "<a>x</a>")]
    [InlineData("<a href=\"JaVaScRiPt:alert(1)\">x</a>", "<a>x</a>")]
    [InlineData("<a href=\" \tjava\nscript:alert(1)\">x</a>", "<a>x</a>")]
    [InlineData("<a href=\"&#106;avascript:alert(1)\">x</a>", "<a>x</a>")]
    [InlineData("<a href=\"&#x6A;avascript&colon;alert(1)\">x</a>", "<a>x</a>")]
    [InlineData("<a href=\"vbscript:msgbox(1)\">x</a>", "<a>x</a>")]
    [InlineData("<a href=\"data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==\">x</a>", "<a>x</a>")]
    [InlineData("<img src=\"data:text/html,<script>alert(1)</script>\">", "<img>")]
    [InlineData("<object data=\"x.swf\"></object><embed src=\"x.swf\">y", "y")]
    [InlineData("<form action=\"/x\"><input name=\"q\"></form>z", "z")]
    [InlineData("<style>body{display:none}</style>t", "t")]
    [InlineData("<template><img src=x onerror=alert(1)></template>t", "t")]
    [InlineData("<math><mi xlink:href=\"javascript:alert(1)\">x</mi></math>t", "t")]
    [InlineData("<div onclick=\"alert(1)\" onmouseover=\"alert(2)\">d</div>", "<div>d</div>")]
    [InlineData("<a href=\"/x\" target=\"_blank\">n</a>", "<a href=\"/x\" target=\"_blank\" rel=\"noopener noreferrer\">n</a>")]
    [InlineData("<a href=\"/x\" target=\"other\" rel=\"nofollow\">n</a>", "<a href=\"/x\" target=\"other\" rel=\"nofollow noopener noreferrer\">n</a>")]
    [InlineData("<p style=\"background-image: url(javascript:alert(1))\">s</p>", "<p>s</p>")]
    [InlineData("<p style=\"width: expression(alert(1))\">s</p>", "<p>s</p>")]
    [InlineData("<!-- c --><p>p</p>", "<p>p</p>")]
    [InlineData("<custom-tag>kept text</custom-tag>", "kept text")]
    [InlineData("<p>cut <img src=x onerror=alert(1)//", "<p>cut </p>")]
    public void Sanitize_ScriptAndUnsafeMarkup_RemovesIt(string html, string expected)
    {
        Assert.Equal(expected, _sanitizer.Sanitize(html));
    }

    [Theory]
    [InlineData("<noscript><p title=\"</noscript><img src=x onerror=alert(1)>\"></noscript>")]
    [InlineData("<svg></p><style><a id=\"</style><img src=1 onerror=alert(1)>\">")]
    [InlineData("<math><mtext><table><mglyph><style><img src=x onerror=alert(1)></style></mglyph></table></mtext></math>")]
    [InlineData("<form><math><mtext></form><form><mglyph><style></math><img src onerror=alert(1)>")]
    [InlineData("<a href=\"x\" <img src=x onerror=alert(1)>")]
    [InlineData("<scr<script>ipt>alert(1)</scr</script>ipt>")]
    [InlineData("<img src=x onerror=alert(1)//")]
    [InlineData("<details open ontoggle=alert(1)>")]
    [InlineData("<a href=\"javascript&#x3A;alert(1)\">x</a>")]
    [InlineData("<a href=\"\u0001javascript:alert(1)\">x</a>")]
    public void Sanitize_MutationAndParserTricks_LeaveNothingThatRuns(string html)
    {
        var sanitized = _sanitizer.Sanitize(html);

        Assert.DoesNotContain("onerror", sanitized, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ontoggle", sanitized, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", sanitized, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"javascript", sanitized, System.StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sanitized, _sanitizer.Sanitize(sanitized));
    }
}
