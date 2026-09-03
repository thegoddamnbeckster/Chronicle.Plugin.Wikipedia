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
            // No maxImages cap applied here — capping the raw stream before dedup would let a
            // duplicate image (the same photo often appears in the infobox AND a gallery
            // section) consume a slot that should have gone to a genuinely distinct image,
            // silently under-delivering relative to the configured cap. Capped once, after
            // dedup, below. Caught in review.
            CollectImages(section, images);

            if (headingText is not null && BoilerplateHeadings.Contains(headingText))
            {
                skipped.Add(headingText);
                continue;
            }

            StripNonProseNodes(section);

            // Each <p> cleaned individually (collapsing only that paragraph's own internal
            // whitespace/line-wraps), then rejoined with a blank line between paragraphs --
            // confirmed directly (2026-08-29) that joining with a single space and cleaning
            // the whole lot afterward (CleanText's \s+ -> " " collapses newlines too) flattened
            // every multi-paragraph article into one unbroken run of text, e.g. Chronicle's own
            // overview field for a genuinely multi-paragraph lead section like "A Knight of the
            // Seven Kingdoms" rendered as a single gigantic blob with no paragraph breaks at all.
            var paragraphs = section.SelectNodes(".//p");
            var text = paragraphs is null
                ? string.Empty
                : string.Join("\n\n", paragraphs.Select(p => CleanText(p.InnerText)).Where(t => t.Length > 0));

            if (headingText is not null || text.Length > 0)
                sections.Add(new ArticleSection(headingText, level, text));
        }

        // Dedup by content identity (not raw URL — the same underlying file is served at
        // different derived URLs for its full-size vs. thumbnail forms), THEN cap -- but only
        // when the caller actually asked for a cap. maxImages <= 0 means unlimited (the
        // default, per-user directive 2026-08-29: "grab everything... discard nothing") --
        // Take(0) would silently store ZERO images, exactly backwards from what "no configured
        // limit" should mean.
        var deduped = images.DistinctBy(ExtractImageIdentity);
        var dedupedImages = (maxImages > 0 ? deduped.Take(maxImages) : deduped).ToList();

        return new ParsedArticle(sections, skipped, dedupedImages);
    }

    private static readonly Regex ThumbnailPrefixRe = new(@"^\d+px-(.+)$", RegexOptions.Compiled);

    /// <summary>
    /// Extracts a content-identity key from a Wikimedia image URL for dedup purposes. Exact
    /// URL string equality fails to recognize the same underlying file at different derived
    /// sizes — e.g. ".../thumb/f/f7/Poster.jpg/300px-Poster.jpg" and ".../f/f7/Poster.jpg" are
    /// the same image, different URLs. Strips the query string, takes the last path segment,
    /// strips a leading "NNNpx-" thumbnail-size prefix if present, then case/percent-decode
    /// normalizes — so a thumbnail and its original resolve to the same identity.
    /// </summary>
    internal static string ExtractImageIdentity(string url)
    {
        var withoutQuery = url.Split('?')[0];
        var lastSegment = withoutQuery.Split('/').LastOrDefault(s => !string.IsNullOrEmpty(s)) ?? withoutQuery;
        var thumbMatch = ThumbnailPrefixRe.Match(lastSegment);
        var baseName = thumbMatch.Success ? thumbMatch.Groups[1].Value : lastSegment;
        return Uri.UnescapeDataString(baseName).ToLowerInvariant();
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

    private static void CollectImages(HtmlNode section, List<string> images)
    {
        var imgNodes = section.SelectNodes(".//img");
        if (imgNodes is null) return;

        foreach (var img in imgNodes)
        {
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
    /// overwhelmingly open with "{Full Name} (born {Month} {Day}, {Year})" for a living
    /// person, or "(born ...; died ...)" for a deceased one. But a large share of deceased
    /// people's articles instead use a bare dash-separated date range with no "born"/"died"
    /// words at all -- "{Full Name} (August 4, 1901 – July 6, 1971), ..." -- confirmed live
    /// (2026-09-03): this second convention matched neither alternative, so both dates came
    /// back null and a genuinely deceased person (Louis Armstrong, ...) showed up with no
    /// death date at all, i.e. as if still alive. A third convention prefixes that same bare
    /// range with a birth-name swap when the subject is best known by a different name --
    /// Bea Arthur's actual lead text is "(born Bernice Frankel; May 13, 1922 – April 25,
    /// 2009)" -- also confirmed live to slip past both other alternatives the same way. This
    /// is a heuristic over prose, not a structured API field — it will miss unconventional
    /// openings and must fail silently (return nulls) rather than throw. Applies uniformly to
    /// every article; it simply never matches non-biographical content, so it doesn't need to
    /// be gated by media type (GetByIdAsync has no type context to gate on anyway).
    /// </summary>
    private static readonly Regex BornDiedRe = new(
        @"\(born\s+([A-Z][a-z]+ \d{1,2},\s*\d{4})(?:\s*;\s*died\s+([A-Z][a-z]+ \d{1,2},\s*\d{4}))?\)" +
        @"|\((?:born\s+[^;()]+;\s*)?([A-Z][a-z]+ \d{1,2},\s*\d{4})\s*[-–—]\s*([A-Z][a-z]+ \d{1,2},\s*\d{4})\)",
        RegexOptions.Compiled);

    public static (DateTime? Born, DateTime? Died) TryExtractBornDied(string leadText)
    {
        var match = BornDiedRe.Match(leadText);
        if (!match.Success) return (null, null);

        // Two mutually-exclusive alternatives: "(born X; died Y)" populates groups 1/2, the
        // bare "(X – Y)" dash-range populates groups 3/4 instead.
        var bornText = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[3].Value;
        var diedText = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[4].Value;

        DateTime? born = DateTime.TryParse(bornText, out var b) ? b : null;
        DateTime? died = !string.IsNullOrEmpty(diedText) && DateTime.TryParse(diedText, out var d)
            ? d
            : null;

        return (born, died);
    }
}
