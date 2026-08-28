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
}
