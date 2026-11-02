using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Bloom.Catalog.Domain;

/// <summary>
/// Product Markdown to HTML (08-catalog §9, Review Focus 4): raw HTML is disabled, and a link or image whose URL has a scheme other
/// than http, https or mailto loses its URL. Staff author the text, but the storefront still renders it into a page.
/// </summary>
public sealed class MarkdownRenderer
{
    private static readonly string[] Schemes = ["http", "https", "mailto"];
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

    public string ToHtml(string markdown)
    {
        var document = Markdown.Parse(markdown, _pipeline);
        foreach (var link in document.Descendants<LinkInline>().Where(l => l.Url is not null && !IsSafe(l.Url)))
            link.Url = "";
        return Markdown.ToHtml(document, _pipeline);
    }

    /// <summary>
    /// Relative URLs pass. Anything with a colon before the first <c>/ ? #</c> is read as having a scheme, however it is spelled
    /// (case, leading spaces, tabs the browser would drop), and must be on the list.
    /// </summary>
    private static bool IsSafe(string url)
    {
        var colon = url.IndexOf(':');
        if (colon < 0) return true;
        var slash = url.AsSpan().IndexOfAny('/', '?', '#');
        return (slash >= 0 && slash < colon) || Schemes.Contains(url[..colon], StringComparer.OrdinalIgnoreCase);
    }
}
