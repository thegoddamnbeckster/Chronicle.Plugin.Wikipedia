using Xunit;

namespace Chronicle.Plugin.Wikipedia.Tests;

public class ArticleHtmlParserTests
{
    private const string SampleArticle = """
        <!DOCTYPE html>
        <html><body>
        <section data-mw-section-id="0">
          <p>The Batman is a 2022 American superhero film. It stars Robert Pattinson.</p>
          <img src="//upload.wikimedia.org/wikipedia/en/f/f7/poster.jpg" data-file-width="236" />
        </section>
        <section data-mw-section-id="1">
          <h2 id="Plot">Plot</h2>
          <p>In his second year of fighting crime, Batman<sup class="reference">[1]</sup> uncovers corruption.</p>
        </section>
        <section data-mw-section-id="2">
          <h2 id="Cast">Cast</h2>
          <p>Robert Pattinson as Bruce Wayne.</p>
          <img src="//upload.wikimedia.org/wikipedia/commons/thumb/1/10/actor.jpg/250px-actor.jpg" data-file-width="1230" />
          <img src="//upload.wikimedia.org/wikipedia/commons/icon.svg" data-file-width="20" />
        </section>
        <section data-mw-section-id="3">
          <h2 id="References">References</h2>
          <div class="reflist"><ol class="references"><li>Citation text here.</li></ol></div>
        </section>
        <section data-mw-section-id="4">
          <h2 id="External_links">External links</h2>
          <p>Some link list text.</p>
        </section>
        </body></html>
        """;

    [Fact]
    public void Parse_SplitsIntoSectionsByHeading()
    {
        var result = ArticleHtmlParser.Parse(SampleArticle, maxImages: 20);

        // Lead + Plot + Cast survive; References + External links are boilerplate.
        Assert.Equal(3, result.Sections.Count);
        Assert.Null(result.Sections[0].Heading);
        Assert.Equal("Plot", result.Sections[1].Heading);
        Assert.Equal("Cast", result.Sections[2].Heading);
    }

    [Fact]
    public void Parse_LeadSectionHasLevelZeroAndNullHeading()
    {
        var result = ArticleHtmlParser.Parse(SampleArticle, maxImages: 20);

        var lead = result.Sections[0];
        Assert.Null(lead.Heading);
        Assert.Equal(0, lead.Level);
        Assert.Contains("The Batman is a 2022 American superhero film", lead.Text);
    }

    [Fact]
    public void Parse_HeadingLevelParsedFromTagName()
    {
        var result = ArticleHtmlParser.Parse(SampleArticle, maxImages: 20);

        Assert.Equal(2, result.Sections[1].Level); // <h2>
    }

    [Fact]
    public void Parse_SkipsBoilerplateSectionsByExactHeadingMatch()
    {
        var result = ArticleHtmlParser.Parse(SampleArticle, maxImages: 20);

        Assert.Contains("References", result.SkippedSections);
        Assert.Contains("External links", result.SkippedSections);
        Assert.DoesNotContain(result.Sections, s => s.Heading == "References");
        Assert.DoesNotContain(result.Sections, s => s.Heading == "External links");
    }

    [Fact]
    public void Parse_DoesNotSkipHeadingThatOnlyContainsBoilerplateWordAsSubstring()
    {
        const string html = """
            <html><body>
            <section data-mw-section-id="0"><p>Lead.</p></section>
            <section data-mw-section-id="1">
              <h2 id="x">References in popular culture</h2>
              <p>This is legitimate prose about references in media, not a citation list.</p>
            </section>
            </body></html>
            """;

        var result = ArticleHtmlParser.Parse(html, maxImages: 20);

        Assert.DoesNotContain("References in popular culture", result.SkippedSections);
        Assert.Contains(result.Sections, s => s.Heading == "References in popular culture");
    }

    [Fact]
    public void Parse_StripsReferenceMarkersFromProseText()
    {
        var result = ArticleHtmlParser.Parse(SampleArticle, maxImages: 20);

        var plot = result.Sections.First(s => s.Heading == "Plot");
        Assert.DoesNotContain("[1]", plot.Text);
        Assert.Contains("uncovers corruption", plot.Text);
    }

    [Fact]
    public void Parse_CollectsImagesAndResolvesProtocolRelativeUrls()
    {
        var result = ArticleHtmlParser.Parse(SampleArticle, maxImages: 20);

        Assert.All(result.ImageUrls, url => Assert.StartsWith("https://", url));
    }

    [Fact]
    public void Parse_FiltersOutIconSizedImages()
    {
        var result = ArticleHtmlParser.Parse(SampleArticle, maxImages: 20);

        Assert.DoesNotContain(result.ImageUrls, url => url.Contains("icon.svg"));
        Assert.Contains(result.ImageUrls, url => url.Contains("poster.jpg"));
        Assert.Contains(result.ImageUrls, url => url.Contains("actor.jpg"));
    }

    [Fact]
    public void Parse_StillCollectsImagesFromSkippedBoilerplateSections()
    {
        const string html = """
            <html><body>
            <section data-mw-section-id="0"><p>Lead.</p></section>
            <section data-mw-section-id="1">
              <h2 id="Further_reading">Further reading</h2>
              <img src="//upload.wikimedia.org/wikipedia/commons/bookcover.jpg" data-file-width="300" />
            </section>
            </body></html>
            """;

        var result = ArticleHtmlParser.Parse(html, maxImages: 20);

        Assert.Contains("Further reading", result.SkippedSections);
        Assert.Contains(result.ImageUrls, url => url.Contains("bookcover.jpg"));
    }

    [Fact]
    public void Parse_RespectsMaxImagesCap()
    {
        var sections = string.Concat(Enumerable.Range(0, 10).Select(i =>
            $"""<section data-mw-section-id="{i}"><p>Text.</p><img src="//upload.wikimedia.org/x/img{i}.jpg" data-file-width="300" /></section>"""));
        var html = $"<html><body>{sections}</body></html>";

        var result = ArticleHtmlParser.Parse(html, maxImages: 3);

        Assert.Equal(3, result.ImageUrls.Count);
    }

    // ── Born/died extraction ──────────────────────────────────────────────────

    [Fact]
    public void TryExtractBornDied_LivingPerson_ExtractsBirthDateOnly()
    {
        var (born, died) = ArticleHtmlParser.TryExtractBornDied(
            "Thomas Cruise Mapother IV (born July 3, 1962) is an American actor.");

        Assert.Equal(new DateTime(1962, 7, 3), born);
        Assert.Null(died);
    }

    [Fact]
    public void TryExtractBornDied_DeceasedPerson_ExtractsBothDates()
    {
        var (born, died) = ArticleHtmlParser.TryExtractBornDied(
            "Some Person (born January 1, 1920; died December 31, 1999) was an actor.");

        Assert.Equal(new DateTime(1920, 1, 1), born);
        Assert.Equal(new DateTime(1999, 12, 31), died);
    }

    [Fact]
    public void TryExtractBornDied_DeceasedPerson_BareDashDateRange_ExtractsBothDates()
    {
        // Regression test for a real production bug: a large share of deceased people's
        // Wikipedia articles use a bare dash-separated date range with no "born"/"died"
        // words at all -- Louis Armstrong's actual lead text is exactly this shape --
        // which the "(born X; died Y)" pattern alone never matched, so both dates came
        // back null and a genuinely deceased person showed up as if still alive.
        var (born, died) = ArticleHtmlParser.TryExtractBornDied(
            "Louis Daniel Armstrong (August 4, 1901 – July 6, 1971), nicknamed \"Satchmo\", " +
            "was an American jazz and blues trumpeter and vocalist.");

        Assert.Equal(new DateTime(1901, 8, 4), born);
        Assert.Equal(new DateTime(1971, 7, 6), died);
    }

    [Fact]
    public void TryExtractBornDied_DeceasedPerson_BirthNameSwapBeforeDateRange_ExtractsBothDates()
    {
        // Regression test for a real production bug: when a deceased subject is best known by
        // a different name than they were born with, Wikipedia prefixes the bare date-range
        // convention with a birth-name swap -- Bea Arthur's actual lead text is exactly this
        // shape -- which slipped past both other alternatives the same way the plain bare
        // range did, so she also showed up with no death date, as if still alive.
        var (born, died) = ArticleHtmlParser.TryExtractBornDied(
            "Beatrice Arthur (born Bernice Frankel; May 13, 1922 – April 25, 2009) was an " +
            "American actress, comedian, and singer.");

        Assert.Equal(new DateTime(1922, 5, 13), born);
        Assert.Equal(new DateTime(2009, 4, 25), died);
    }

    [Fact]
    public void TryExtractBornDied_DeceasedPerson_BareHyphenDateRange_ExtractsBothDates()
    {
        // Same bare-range convention but with a plain ASCII hyphen instead of an en dash --
        // Wikipedia's own markup isn't perfectly consistent about which character it uses.
        var (born, died) = ArticleHtmlParser.TryExtractBornDied(
            "Some Person (January 1, 1920 - December 31, 1999) was an actor.");

        Assert.Equal(new DateTime(1920, 1, 1), born);
        Assert.Equal(new DateTime(1999, 12, 31), died);
    }

    [Fact]
    public void TryExtractBornDied_NonBiographicalText_ReturnsNulls()
    {
        var (born, died) = ArticleHtmlParser.TryExtractBornDied(
            "The Batman is a 2022 American superhero film.");

        Assert.Null(born);
        Assert.Null(died);
    }

    [Fact]
    public void TryExtractBornDied_EmptyText_ReturnsNullsWithoutThrowing()
    {
        var (born, died) = ArticleHtmlParser.TryExtractBornDied(string.Empty);

        Assert.Null(born);
        Assert.Null(died);
    }

    // ── Image identity extraction (dedup across thumbnail vs. original URLs) ──

    [Theory]
    [InlineData(
        "https://upload.wikimedia.org/wikipedia/en/f/f7/The_Batman_%28film%29_poster.jpg",
        "https://upload.wikimedia.org/wikipedia/en/thumb/f/f7/The_Batman_%28film%29_poster.jpg/300px-The_Batman_%28film%29_poster.jpg")]
    [InlineData(
        "https://upload.wikimedia.org/wikipedia/commons/1/10/Photo.jpg",
        "https://upload.wikimedia.org/wikipedia/commons/thumb/1/10/Photo.jpg/250px-Photo.jpg?utm_source=en.wikipedia.org")]
    public void ExtractImageIdentity_OriginalAndThumbnailUrls_ResolveToSameIdentity(string original, string thumbnail)
    {
        Assert.Equal(
            ArticleHtmlParser.ExtractImageIdentity(original),
            ArticleHtmlParser.ExtractImageIdentity(thumbnail));
    }

    [Fact]
    public void ExtractImageIdentity_DifferentFiles_ResolveToDifferentIdentities()
    {
        var a = ArticleHtmlParser.ExtractImageIdentity("https://upload.wikimedia.org/wikipedia/en/f/f7/PosterA.jpg");
        var b = ArticleHtmlParser.ExtractImageIdentity("https://upload.wikimedia.org/wikipedia/en/f/f7/PosterB.jpg");

        Assert.NotEqual(a, b);
    }

    // ── Cap-before-dedup regression (a duplicate image must not consume a cap slot that
    // should go to a genuinely distinct image) ──

    [Fact]
    public void Parse_DuplicateImageAcrossSections_DoesNotConsumeCapSlotFromDistinctImages()
    {
        // Same photo appears in the lead (infobox) AND again in Cast, at different derived
        // sizes — a real, common Wikipedia pattern. A third, genuinely distinct image follows.
        const string html = """
            <html><body>
            <section data-mw-section-id="0">
              <p>Lead.</p>
              <img src="//upload.wikimedia.org/x/f/f7/Same.jpg" data-file-width="1000" />
            </section>
            <section data-mw-section-id="1">
              <h2 id="Cast">Cast</h2>
              <img src="//upload.wikimedia.org/x/thumb/f/f7/Same.jpg/220px-Same.jpg" data-file-width="1000" />
              <img src="//upload.wikimedia.org/x/f/f8/Distinct.jpg" data-file-width="1000" />
            </section>
            </body></html>
            """;

        var result = ArticleHtmlParser.Parse(html, maxImages: 2);

        Assert.Equal(2, result.ImageUrls.Count);
        Assert.Contains(result.ImageUrls, url => url.Contains("Distinct.jpg"));
    }
}
