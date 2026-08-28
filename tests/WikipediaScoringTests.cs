using Chronicle.Plugin.Wikipedia.Models;
using Chronicle.Plugins.Models;
using Xunit;

namespace Chronicle.Plugin.Wikipedia.Tests;

public class WikipediaScoringTests
{
    private static WikiSearchPage Page(
        string title, string? extract = null, string? description = null,
        string? disambiguation = null, string? wikibaseItem = "Q1") =>
        new(
            PageId: 1,
            Title: title,
            Index: 1,
            Extract: extract,
            Thumbnail: null,
            PageProps: new WikiPageProps(disambiguation, wikibaseItem),
            Terms: description is null ? null : new WikiTerms([description]));

    // ── Title similarity ─────────────────────────────────────────────────────

    [Fact]
    public void Score_ExactTitleMatch_Scores45ForTitleSignal()
    {
        var context = new MediaSearchContext(Name: "The Batman", MediaTypeName: "movies");
        var result = WikipediaScoring.Score(context, Page("The Batman"));

        // No description/year signals fire here, so the score IS the title signal.
        Assert.Equal(45, result.Score);
        Assert.False(result.HardReject);
    }

    [Fact]
    public void Score_ExactTitleMatch_IgnoringDisambiguationSuffix_StillScoresFull()
    {
        var context = new MediaSearchContext(Name: "The Batman", MediaTypeName: "movies");
        var result = WikipediaScoring.Score(context, Page("The Batman (film)"));

        Assert.Equal(45, result.Score);
    }

    [Fact]
    public void Score_CompletelyUnrelatedTitle_ScoresZeroTitleSignal()
    {
        var context = new MediaSearchContext(Name: "The Batman", MediaTypeName: "movies");
        var result = WikipediaScoring.Score(context, Page("Giraffe"));

        Assert.Equal(0, result.Score);
    }

    // ── Media-type keyword matching ──────────────────────────────────────────

    [Fact]
    public void Score_DescriptionMatchesExpectedType_AddsTypeSignal()
    {
        var context = new MediaSearchContext(Name: "The Batman", MediaTypeName: "movies");
        var result = WikipediaScoring.Score(context, Page("The Batman", description: "2022 superhero film by Matt Reeves"));

        // Title (45) + type match (25) = 70.
        Assert.Equal(70, result.Score);
        Assert.Contains("type match", result.Reason);
    }

    [Fact]
    public void Score_DescriptionNamesConflictingType_HardRejects()
    {
        var context = new MediaSearchContext(Name: "Doom", MediaTypeName: "movies");
        var result = WikipediaScoring.Score(context, Page("Doom", description: "1993 video game by id Software"));

        Assert.True(result.HardReject);
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void Score_NoDescription_DoesNotHardReject()
    {
        var context = new MediaSearchContext(Name: "Some Obscure Thing", MediaTypeName: "movies");
        var result = WikipediaScoring.Score(context, Page("Some Obscure Thing"));

        Assert.False(result.HardReject);
    }

    // ── Disambiguation pages ─────────────────────────────────────────────────

    [Fact]
    public void Score_DisambiguationPage_HardRejectsRegardlessOfTitleMatch()
    {
        var context = new MediaSearchContext(Name: "Mercury", MediaTypeName: "movies");
        var result = WikipediaScoring.Score(context, Page("Mercury", disambiguation: ""));

        Assert.True(result.HardReject);
        Assert.Equal(0, result.Score);
    }

    // ── Year corroboration ───────────────────────────────────────────────────

    [Fact]
    public void Score_YearExactMatchInExtract_AddsYearSignal()
    {
        var context = new MediaSearchContext(Name: "The Batman", Year: 2022, MediaTypeName: "movies");
        var result = WikipediaScoring.Score(context, Page("The Batman", extract: "The Batman is a 2022 American superhero film."));

        // Title (45) + year exact (20) = 65.
        Assert.Equal(65, result.Score);
        Assert.Contains("year exact", result.Reason);
    }

    [Fact]
    public void Score_YearOffByOne_AddsPartialYearSignal()
    {
        var context = new MediaSearchContext(Name: "The Batman", Year: 2023, MediaTypeName: "movies");
        var result = WikipediaScoring.Score(context, Page("The Batman", extract: "The Batman is a 2022 American superhero film."));

        // Title (45) + year ±1 (12) = 57.
        Assert.Equal(57, result.Score);
        Assert.Contains("year", result.Reason);
    }

    [Fact]
    public void Score_YearAbsentFromContext_NoYearSignalContributed()
    {
        var context = new MediaSearchContext(Name: "The Batman", Year: null, MediaTypeName: "movies");
        var result = WikipediaScoring.Score(context, Page("The Batman", extract: "The Batman is a 2022 American superhero film."));

        Assert.Equal(45, result.Score);
    }

    // ── Parent/grandparent corroboration (hierarchy levels 1-2) ─────────────

    [Fact]
    public void Score_Level2WithParentNameInExtract_AddsParentSignal()
    {
        var context = new MediaSearchContext(
            Name: "Ozymandias", ParentName: "Breaking Bad", HierarchyLevel: 2, MediaTypeName: "tv");
        var result = WikipediaScoring.Score(
            context, Page("Ozymandias (Breaking Bad)", extract: "\"Ozymandias\" is the 14th episode of Breaking Bad."));

        // Title (45, since disambiguation-suffix strip normalizes to "Ozymandias") + parent (15) = 60.
        Assert.Equal(60, result.Score);
        Assert.Contains("parent corroborated", result.Reason);
    }

    [Fact]
    public void Score_Level0_NeverAppliesParentSignalEvenIfParentNameSet()
    {
        var context = new MediaSearchContext(
            Name: "The Batman", ParentName: "Should Not Matter", HierarchyLevel: 0, MediaTypeName: "movies");
        var result = WikipediaScoring.Score(context, Page("The Batman", extract: "Should Not Matter appears nowhere relevant."));

        Assert.DoesNotContain("parent corroborated", result.Reason);
    }

    // ── Threshold ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(20, true)]
    [InlineData(19, false)]
    [InlineData(0, false)]
    [InlineData(100, true)]
    public void MeetsReturnThreshold_BoundaryChecks(int score, bool expected)
    {
        Assert.Equal(expected, WikipediaScoring.MeetsReturnThreshold(score));
    }

    [Fact]
    public void Score_NeverExceeds100()
    {
        // Stack every positive signal to confirm the cap holds even when signals would
        // otherwise sum past 100 (45 + 25 + 20 + 15 = 105 uncapped).
        var context = new MediaSearchContext(
            Name: "Ozymandias", Year: 2013, ParentName: "Breaking Bad", HierarchyLevel: 2, MediaTypeName: "tv");
        var result = WikipediaScoring.Score(
            context,
            Page("Ozymandias", extract: "\"Ozymandias\" is a 2013 episode of Breaking Bad.",
                 description: "television series"));

        Assert.Equal(100, result.Score);
    }
}
