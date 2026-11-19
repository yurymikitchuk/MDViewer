using System.Net;
using System.Text.RegularExpressions;
using Ganss.Xss;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MDViewer.Services;

public sealed class MarkdownRenderResult
{
    public required string HtmlFragment { get; init; }
    public required bool HasMermaid { get; init; }
}

public sealed class MarkdownService
{
    public const string DocumentHost = "mdviewer-doc.localhost";

    private readonly MarkdownPipeline _pipeline;
    private readonly HtmlSanitizer _sanitizer;

    private static readonly Regex MermaidFence = new(
        @"```mermaid\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public MarkdownService()
    {
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .DisableHtml()
            .Build();
        _sanitizer = CreateSanitizer();
    }

    public MarkdownRenderResult Render(string markdown, string? documentDirectory = null)
    {
        var hasMermaid = MermaidFence.IsMatch(markdown);
        var prepared = hasMermaid ? MermaidErNormalizer.NormalizeMarkdown(markdown) : markdown;

        var document = Markdown.Parse(prepared, _pipeline);
        StripDangerousImages(document);
        RewriteRelativeMedia(document, documentDirectory);

        var html = _sanitizer.Sanitize(document.ToHtml(_pipeline));
        return new MarkdownRenderResult
        {
            HtmlFragment = html,
            HasMermaid = hasMermaid
        };
    }

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedAttributes.Add("class");
        sanitizer.AllowedAttributes.Add("id");
        sanitizer.AllowedAttributes.Add("open");
        sanitizer.AllowedCssProperties.Add("text-align");
        sanitizer.AllowedSchemes.Add("mailto");
        sanitizer.AllowedSchemes.Add("data");
        return sanitizer;
    }

    private static void RewriteRelativeMedia(MarkdownDocument document, string? documentDirectory)
    {
        if (string.IsNullOrWhiteSpace(documentDirectory))
        {
            return;
        }

        var root = Path.GetFullPath(documentDirectory);
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (!link.IsImage)
            {
                continue;
            }

            var url = link.Url?.Trim();
            if (string.IsNullOrEmpty(url)
                || url.StartsWith('#')
                || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                continue;
            }

            try
            {
                var decoded = Uri.UnescapeDataString(url.Replace('/', Path.DirectorySeparatorChar));
                var full = Path.GetFullPath(Path.Combine(root, decoded));
                if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
                link.Url = $"https://{DocumentHost}/{relative}";
            }
            catch
            {
                // leave original URL
            }
        }
    }

    private static void StripDangerousImages(MarkdownDocument document)
    {
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (!link.IsImage)
            {
                continue;
            }

            var url = link.Url ?? string.Empty;
            if (url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase))
            {
                link.Url = string.Empty;
                link.Title = WebUtility.HtmlEncode(link.Title ?? string.Empty);
            }
        }
    }
}
