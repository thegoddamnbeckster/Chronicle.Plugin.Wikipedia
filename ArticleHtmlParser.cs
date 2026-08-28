using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Chronicle.Plugin.Wikipedia;

internal sealed record ArticleSection(string? Heading, int Level, string Text);

internal sealed record ParsedArticle(
    List<ArticleSection> Sections,
    List<string> SkippedSections,
    List<string> ImageUrls);

/// <summary>
/// Splits a full Parsoid HTML article (from the REST /page/html/{title} endpoint) into
/// per-heading sections and collects article-wide image URLs. Parsoid wraps each top-level
/// heading's content in its own &lt;section data-mw-section-id="N"&gt; element (N=0 for the
/// untitled lead), which is what makes this a DOM walk rather than a wikitext parser.
/// </summary>
internal static class ArticleHtmlParser
{
    /// <summary>Exact-match only (not substring) — a heading like "References in popular
    /// culture" is legitimate prose and must not be caught by a loose match against
    /// "References".</summary>
    private static readonly HashSet<string> BoilerplateHeadings = new(StringComparer.OrdinalIgnoreCase)
    {
        "References", "External links", "See also", "Further reading", "Notes",
        "Bibliography", "Citations", "Sources", "Works cited", "Footnotes",
    };

    private static readonly Regex WhitespaceRe = new(@"\s+", RegexOptions.Compiled);

    public static ParsedArticle Parse(string html, int maxImages)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var body = doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;
        IEnumerable<HtmlNode> sectionNodes =
            body.SelectNodes(".//section[@data-mw-section-id]") ?? Enumerable.Empty<HtmlNode>();

        var sections = new List<ArticleSection>();
        var skipped = new List<string>();
        var images = new List<string>();

        foreach (var section in sectionNodes)
        {
            var headingNode = section.SelectSingleNode("./h2|./h3|./h4");
            var headingText = headingNode is null ? null : CleanText(headingNode.InnerText);
            var level = headingNode is null ? 0 : ParseHeadingLevel(headingNode.Name);

            // Collect images from every section, including ones we're about to skip the text
            // of — a "Further reading" section's book cover images are still legitimate images.
            CollectImages(section, images, maxImages);

            if (headingText is not null && BoilerplateHeadings.Contains(headingText))
            {
                skipped.Add(headingText);
                continue;
            }

            StripNonProseNodes(section);

            var paragraphs = section.SelectNodes(".//p");
            var text = paragraphs is null
                ? string.Empty
                : CleanText(string.Join(" ", paragraphs.Select(p => p.InnerText)));

            if (headingText is not null || text.Length > 0)
                sections.Add(new ArticleSection(headingText, level, text));
        }

        return new ParsedArticle(sections, skipped, images.Distinct().Take(Math.Max(maxImages, 0)).ToList());
    }

    private static int ParseHeadingLevel(string tagName) =>
        tagName.Length == 2 && tagName[0] is 'h' or 'H' && char.IsDigit(tagName[1])
            ? tagName[1] - '0'
            : 0;

    /// <summary>Removes reference markers, edit-section links, tables (infoboxes/wikitables/
    /// navboxes), style blocks, and hatnotes before text is extracted — none of these are
    /// "regular text."</summary>
    private static void StripNonProseNodes(HtmlNode section)
    {
        var toRemove = section.SelectNodes(
            ".//sup[contains(@class,'reference')] | .//sup[contains(@class,'noprint')] | " +
            ".//span[contains(@class,'mw-editsection')] | .//table | .//style | " +
            ".//div[@role='note'] | .//div[contains(@class,'hatnote')]");

        if (toRemove is null) return;

        // Snapshot first — mutating the collection while iterating it can skip siblings.
        foreach (var node in toRemove.ToList())
            node.Remove();
    }

    private static void CollectImages(HtmlNode section, List<string> images, int maxImages)
    {
        if (images.Count >= maxImages) return;

        var imgNodes = section.SelectNodes(".//img");
        if (imgNodes is null) return;

        foreach (var img in imgNodes)
        {
            if (images.Count >= maxImages) return;

            var src = img.GetAttributeValue("src", string.Empty);
            if (string.IsNullOrWhiteSpace(src)) continue;

            if (src.StartsWith("//", StringComparison.Ordinal))
                src = "https:" + src;

            // Icon-sized images (flags, coordinate markers, edit icons rendered by templates)
            // are not article content — filter by the width Parsoid already reports.
            var widthStr = img.GetAttributeValue("data-file-width", string.Empty);
            if (int.TryParse(widthStr, out var width) && width is > 0 and < 50)
                continue;

            images.Add(src);
        }
    }

    private static string CleanText(string raw)
    {
        var decoded = HtmlEntity.DeEntitize(raw) ?? string.Empty;
        return WhitespaceRe.Replace(decoded, " ").Trim();
    }

    // ── Born/died date extraction (Section 7 — people bios) ─────────────────

    /// <summary>
    /// Best-effort regex over the lead section's plain text. Wikipedia biography articles
    /// overwhelmingly open with "{Full Name} (born {Month} {Day}, {Year})" or, for the
    /// deceased, "(born ...; died ...)". This is a heuristic over prose, not a structured
    /// API field — it will miss unconventional openings and must fail silently (return
    /// nulls) rather than throw. Applies uniformly to every article; it simply never matches
    /// non-biographical content, so it doesn't need to be gated by media type (GetByIdAsync
    /// has no type context to gate on anyway).
    /// </summary>
    private static readonly Regex BornDiedRe = new(
        @"\(born\s+([A-Z][a-z]+ \d{1,2},\s*\d{4})(?:\s*;\s*died\s+([A-Z][a-z]+ \d{1,2},\s*\d{4}))?\)",
        RegexOptions.Compiled);

    public static (DateTime? Born, DateTime? Died) TryExtractBornDied(string leadText)
    {
        var match = BornDiedRe.Match(leadText);
        if (!match.Success) return (null, null);

        DateTime? born = DateTime.TryParse(match.Groups[1].Value, out var b) ? b : null;
        DateTime? died = match.Groups[2].Success && DateTime.TryParse(match.Groups[2].Value, out var d)
            ? d
            : null;

        return (born, died);
    }
}
