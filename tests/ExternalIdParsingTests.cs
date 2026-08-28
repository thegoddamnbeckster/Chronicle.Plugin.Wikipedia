using Xunit;

namespace Chronicle.Plugin.Wikipedia.Tests;

public class ExternalIdParsingTests
{
    // ── Native "wikipedia:{lang}:{title}" format ─────────────────────────────

    [Fact]
    public void ParseExternalId_NativeFormat_ParsesLangAndTitle()
    {
        var (lang, title) = WikipediaMetadataProvider.ParseExternalId("wikipedia:en:The_Batman_(film)");

        Assert.Equal("en", lang);
        Assert.Equal("The_Batman_(film)", title);
    }

    // ── Bare "lang:Title" Fix Match format ───────────────────────────────────

    [Fact]
    public void ParseExternalId_BareLangTitleFormat_Parses()
    {
        var (lang, title) = WikipediaMetadataProvider.ParseExternalId("de:Berlin");

        Assert.Equal("de", lang);
        Assert.Equal("Berlin", title);
    }

    [Fact]
    public void ParseExternalId_UrlEncodedTitleInBareFormat_IsDecoded()
    {
        var (lang, title) = WikipediaMetadataProvider.ParseExternalId("en:The%20Batman");

        Assert.Equal("The Batman", title);
    }

    // ── Pasted URLs ───────────────────────────────────────────────────────────

    [Fact]
    public void ParseExternalId_StandardUrl_ExtractsLangAndTitle()
    {
        var (lang, title) = WikipediaMetadataProvider.ParseExternalId("https://en.wikipedia.org/wiki/The_Batman_(film)");

        Assert.Equal("en", lang);
        Assert.Equal("The_Batman_(film)", title);
    }

    [Fact]
    public void ParseExternalId_MobileSubdomainUrl_NormalizesToBaseLanguage()
    {
        var (lang, title) = WikipediaMetadataProvider.ParseExternalId("https://en.m.wikipedia.org/wiki/The_Batman");

        Assert.Equal("en", lang);
        Assert.Equal("The_Batman", title);
    }

    [Fact]
    public void ParseExternalId_BareWwwUrl_DefaultsToEnglish()
    {
        var (lang, title) = WikipediaMetadataProvider.ParseExternalId("https://www.wikipedia.org/wiki/Cat");

        Assert.Equal("en", lang);
        Assert.Equal("Cat", title);
    }

    [Fact]
    public void ParseExternalId_UrlWithPercentEncodedTitle_Decodes()
    {
        var (_, title) = WikipediaMetadataProvider.ParseExternalId("https://en.wikipedia.org/wiki/The_Batman_%28film%29");

        Assert.Equal("The_Batman_(film)", title);
    }

    // ── SSRF guard — must reject non-Wikipedia hosts before trusting anything ──

    [Theory]
    [InlineData("https://evil.com/wiki/Something")]
    [InlineData("https://wikipedia.org.evil.com/wiki/Something")]
    [InlineData("https://notwikipedia.org/wiki/Something")]
    [InlineData("https://en.wikipediax.org/wiki/Something")]
    public void ParseExternalId_UntrustedHost_ThrowsBeforeTrustingAnyExtractedPart(string url)
    {
        Assert.Throws<ArgumentException>(() => WikipediaMetadataProvider.ParseExternalId(url));
    }

    [Fact]
    public void ParseExternalId_UrlMissingWikiPathSegment_Throws()
    {
        Assert.Throws<ArgumentException>(() => WikipediaMetadataProvider.ParseExternalId("https://en.wikipedia.org/notwiki/Something"));
    }

    // ── Malformed input ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("justatitle")]
    [InlineData("wikipedia:")]
    [InlineData(":")]
    public void ParseExternalId_MalformedInput_Throws(string input)
    {
        Assert.Throws<ArgumentException>(() => WikipediaMetadataProvider.ParseExternalId(input));
    }
}
