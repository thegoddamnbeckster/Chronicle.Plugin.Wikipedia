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
    public void Score_PeopleReorderedNameTokens_HardRejects()
    {
        // Confirmed live (2026-09-02): a "Martin Quinn" (Strange New Worlds actor) search
        // matched the unrelated "Quinn Martin" (1922-1987 TV producer) article -- Jaccard
        // similarity can't distinguish a set of tokens from its reordering, and scored this a
        // "perfect" 45-point title match. For "people" this must hard-reject, not merely
        // score lower, since a same-token different-order candidate is a different real person.
        var context = new MediaSearchContext(Name: "Martin Quinn", MediaTypeName: "people");
        var result = WikipediaScoring.Score(context,
            Page("Quinn Martin", description: "American television producer"));

        Assert.Equal(0, result.Score);
        Assert.True(result.HardReject);
    }

    [Fact]
    public void Score_PeopleKnownBirthYearConflictsWithCandidate_HardRejects()
    {
        // Confirmed live (2026-09-14): "Arturo Castro" (the modern Broad City actor, born 1985)
        // matched the unrelated "Arturo Castro (Mexican actor)" article (1918-1975) -- exact
        // title match (after stripping the disambiguation suffix) plus a matching "actor" type
        // keyword scored 70/100 with nothing to catch the 67-year-old identity mismatch, since
        // context.Year is never set for a people query and the reordered-name-token check above
        // only catches a different bug shape.
        var context = new MediaSearchContext(Name: "Arturo Castro", MediaTypeName: "people", KnownBirthYear: 1985);
        var result = WikipediaScoring.Score(context, Page(
            "Arturo Castro (Mexican actor)",
            extract: "Arturo Castro Rivas Cacho (March 21, 1918 – March 6, 1975) was a Mexican character actor.",
            description: "Mexican actor"));

        Assert.Equal(0, result.Score);
        Assert.True(result.HardReject);
    }

    [Fact]
    public void Score_PeopleKnownBirthYearMatchesCandidate_DoesNotHardReject()
    {
        var context = new MediaSearchContext(Name: "Arturo Castro", MediaTypeName: "people", KnownBirthYear: 1985);
        var result = WikipediaScoring.Score(context, Page(
            "Arturo Castro",
            extract: "Arturo Castro (born November 26, 1985) is an American actor and writer.",
            description: "American actor"));

        Assert.False(result.HardReject);
        Assert.True(result.Score > 0);
    }

    [Fact]
    public void Score_PeopleNoKnownBirthYear_DoesNotHardRejectOnLifeYears()
    {
        // KnownBirthYear is null on the very first provider to ever search for this person (no
        // other provider has enriched it yet) -- the signal must be a no-op, not a reject, since
        // there's nothing yet to corroborate against.
        var context = new MediaSearchContext(Name: "Arturo Castro", MediaTypeName: "people");
        var result = WikipediaScoring.Score(context, Page(
            "Arturo Castro (Mexican actor)",
            extract: "Arturo Castro Rivas Cacho (March 21, 1918 – March 6, 1975) was a Mexican character actor.",
            description: "Mexican actor"));

        Assert.False(result.HardReject);
    }

    [Fact]
    public void Score_PeopleCandidateHasExtraNameToken_HardRejects()
    {
        // Confirmed live (2026-09-14): two unrelated real people both credited simply as
        // "Jesse James" (a reality-TV mechanic from Monster Garage, and a separate child actor
        // from The Amityville Horror/Jumper) both had their Chronicle item renamed to "Jesse
        // James Dupree" -- a third, unrelated musician -- this way. Jaccard alone can't catch
        // it: {jesse, james} vs {jesse, james, dupree} scores 0.667, clearing the return
        // threshold once combined with the type-keyword signal (Wikipedia's description for
        // Dupree, "American musician", doesn't conflict with anything people-specific), and
        // neither side had a known birth year yet for Signal 3b to catch it either.
        var context = new MediaSearchContext(Name: "Jesse James", MediaTypeName: "people");
        var result = WikipediaScoring.Score(context,
            Page("Jesse James Dupree", description: "American musician"));

        Assert.Equal(0, result.Score);
        Assert.True(result.HardReject);
    }

    [Fact]
    public void Score_PeopleQueryHasExtraNameToken_HardRejects()
    {
        // Same shape as above, direction reversed -- the query carries the extra token and the
        // candidate is the shorter name. Both directions are the same underlying bug (an extra
        // full name component signals a different specific person), so both must reject.
        var context = new MediaSearchContext(Name: "Jesse James Dupree", MediaTypeName: "people");
        var result = WikipediaScoring.Score(context,
            Page("Jesse James", description: "American television personality"));

        Assert.Equal(0, result.Score);
        Assert.True(result.HardReject);
    }

    [Fact]
    public void Score_TypeKeywordAsSubstringOfUnrelatedWord_DoesNotConflict()
    {
        // Caught in review before release: LooksLikeConflictingType used a plain substring
        // match, so a "people" description of "American filmmaker" was treated as conflicting
        // with the "people" type because "filmmaker" contains "film" -- a "movies" keyword --
        // as a substring, hard-rejecting a correct match purely on word-boundary sloppiness.
        var context = new MediaSearchContext(Name: "Jane Director", MediaTypeName: "people");
        var result = WikipediaScoring.Score(context,
            Page("Jane Director", description: "American filmmaker"));

        Assert.False(result.HardReject);
    }

    [Fact]
    public void Score_PeopleCandidateHasBenignSuffixToken_DoesNotHardReject()
    {
        // Caught in review before release: the subset/superset hard-reject as first written had
        // no exception for a generational suffix, so a query missing "Jr." against the correct
        // Wikipedia title would have hard-rejected a genuinely correct match.
        var context = new MediaSearchContext(Name: "Sammy Davis", MediaTypeName: "people");
        var result = WikipediaScoring.Score(context,
            Page("Sammy Davis Jr.", description: "American singer"));

        Assert.False(result.HardReject);
        Assert.True(result.Score > 0);
    }

    [Fact]
    public void Score_PeopleCandidateHasBenignParticleToken_DoesNotHardReject()
    {
        // Same shape, a name particle instead of a suffix -- "Guillermo del Toro" is routinely
        // shortened to "Guillermo Toro" by sources that don't preserve the particle.
        var context = new MediaSearchContext(Name: "Guillermo Toro", MediaTypeName: "people");
        var result = WikipediaScoring.Score(context,
            Page("Guillermo del Toro", description: "Mexican film director"));

        Assert.False(result.HardReject);
        Assert.True(result.Score > 0);
    }

    [Fact]
    public void Score_PeopleCandidateHasBenignAndNonBenignExtraTokens_StillHardRejects()
    {
        // A benign suffix token does NOT give a free pass to an actual extra name component
        // sitting alongside it -- only an ALL-benign difference is exempted.
        var context = new MediaSearchContext(Name: "Jesse James", MediaTypeName: "people");
        var result = WikipediaScoring.Score(context,
            Page("Jesse James Dupree Jr.", description: "American musician"));

        Assert.Equal(0, result.Score);
        Assert.True(result.HardReject);
    }

    [Fact]
    public void Score_NonPeopleCandidateHasExtraToken_StillScoresBySimilarity()
    {
        // The hard-reject above is scoped to "people" only -- a movie/show subtitle or
        // qualifier ("Toy Story" vs "Toy Story 2") is routine and not a distinct-identity
        // signal the way an added name component is for a person.
        var context = new MediaSearchContext(Name: "Toy Story", MediaTypeName: "movies");
        var result = WikipediaScoring.Score(context, Page("Toy Story 2"));

        Assert.False(result.HardReject);
    }

    [Fact]
    public void Score_NonPeopleReorderedTitleTokens_StillScoresBySimilarity()
    {
        // The hard-reject above is scoped to "people" only -- movie/show titles don't carry the
        // same "reordering means a different real-world identity" guarantee, so a coincidental
        // reordering there should still be scored (not silently rejected) like any other
        // high-Jaccard partial match.
        var context = new MediaSearchContext(Name: "Martin Quinn", MediaTypeName: "movies");
        var result = WikipediaScoring.Score(context, Page("Quinn Martin"));

        Assert.Equal(45, result.Score);
        Assert.False(result.HardReject);
    }

    [Fact]
    public void Score_TitleDiffersOnlyBySequelNumber_HardRejects()
    {
        // Root-caused live (2026-09-12): a real "Toy Story 4" file (correctly matched
        // everywhere else -- TMDB movie:301528, correct cast/overview) had its Wikipedia match
        // land on the unrelated "Toy Story 5" article instead, overwriting its own correct
        // title. Jaccard treats "Toy Story 4" vs "Toy Story 5" as 50% similar (every token but
        // the trailing number matches) -- combined with the type-keyword signal alone (the
        // wrong article is still plainly a film), that clears the return threshold with zero
        // year corroboration ever needing to agree. Each numbered franchise entry gets its own
        // distinct Wikipedia article specifically because they are different works.
        var context = new MediaSearchContext(Name: "Toy Story 4", MediaTypeName: "movies", Year: 2019);
        var result = WikipediaScoring.Score(context,
            Page("Toy Story 5", description: "2026 American animated comedy-drama film"));

        Assert.Equal(0, result.Score);
        Assert.True(result.HardReject);
    }

    [Fact]
    public void Score_TitleDiffersOnlyBySequelNumber_ExactYearMatchStillHardRejects()
    {
        // The sequel-number mismatch alone is disqualifying -- it must not be treatable as "one
        // bad signal outvoted by good ones." Even a candidate that (implausibly) also carries
        // the query's own exact year is still a different, specifically-numbered entry.
        var context = new MediaSearchContext(Name: "Toy Story 4", MediaTypeName: "movies", Year: 2019);
        var result = WikipediaScoring.Score(context,
            Page("Toy Story 5", description: "2019 American animated comedy-drama film"));

        Assert.Equal(0, result.Score);
        Assert.True(result.HardReject);
    }

    [Fact]
    public void Score_TitlesDifferBySequelNumberButAlsoOtherWords_StillScoresBySimilarity()
    {
        // The hard-reject is deliberately narrow: titles must be identical in every token
        // except the trailing number. A title that also differs somewhere else (not just a
        // sequel-numbering coincidence) doesn't carry the same "these are definitely two
        // different, specifically-numbered works" certainty, so it falls back to ordinary
        // similarity scoring instead of being rejected outright.
        var context = new MediaSearchContext(Name: "Toy Story 4", MediaTypeName: "movies");
        var result = WikipediaScoring.Score(context, Page("Toy Adventure 5"));

        Assert.False(result.HardReject);
    }

    [Fact]
    public void Score_TitleAndNumberBothMatch_StillScoresNormally()
    {
        // Sanity check: the new hard-reject must only fire when the trailing numbers actually
        // DIFFER -- an exact match (already handled earlier in Score()) is unaffected.
        var context = new MediaSearchContext(Name: "Toy Story 4", MediaTypeName: "movies");
        var result = WikipediaScoring.Score(context, Page("Toy Story 4"));

        Assert.Equal(45, result.Score);
        Assert.False(result.HardReject);
    }

    // ── ExtractDisambiguator ─────────────────────────────────────────────────

    [Theory]
    [InlineData("Michael Lerner (actor)", "actor")]
    [InlineData("Poltergeist (1982 film)", "1982 film")]
    [InlineData("Barbie (doll)", "doll")]
    public void ExtractDisambiguator_TitleWithTrailingParenthetical_ReturnsItsContent(string title, string expected)
    {
        Assert.Equal(expected, WikipediaScoring.ExtractDisambiguator(title));
    }

    [Theory]
    [InlineData("Michael Lerner")]
    [InlineData("The Batman")]
    public void ExtractDisambiguator_TitleWithNoParenthetical_ReturnsNull(string title)
    {
        Assert.Null(WikipediaScoring.ExtractDisambiguator(title));
    }

    [Fact]
    public void ExtractDisambiguator_EmptyParentheses_ReturnsNull()
    {
        // Degenerate input ("Some Title ()") -- an empty capture is treated the same as no
        // disambiguator at all, not as a zero-length string worth storing.
        Assert.Null(WikipediaScoring.ExtractDisambiguator("Some Title ()"));
    }

    [Fact]
    public void Score_CompletelyUnrelatedTitle_ScoresZeroTitleSignal()
    {
        var context = new MediaSearchContext(Name: "The Batman", MediaTypeName: "movies");
        var result = WikipediaScoring.Score(context, Page("Giraffe"));

        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void Score_ZeroTitleOverlap_HardRejectsEvenWithStrongTypeAndYearSignals()
    {
        // General-case version of the "20XX in film" bug below, using a title shape the new
        // YearSummaryArticleRe hard-reject does NOT itself match, so this isolates the OTHER,
        // independent fix: when title similarity contributes nothing (zero token overlap between
        // the candidate and query titles), Score() used to fall through to type+year signals
        // alone -- up to 45 points, comfortably over the 20-point return threshold -- letting a
        // candidate with ZERO relation to the query title win outright. Type and year are
        // corroborating signals, not substitutes for identity.
        var context = new MediaSearchContext(Name: "Rango", MediaTypeName: "movies", Year: 2011);
        var result = WikipediaScoring.Score(context,
            Page("Some Unrelated Film", description: "2011 film", extract: "Released in 2011, this film..."));

        Assert.True(result.HardReject);
        Assert.Equal(0, result.Score);
    }

    // ── Yearly-summary/list articles ("2011 in film") ────────────────────────

    [Theory]
    [InlineData("2011 in film")]
    [InlineData("2011 in television")]
    [InlineData("List of American films of 2011")]
    public void Score_YearSummaryArticle_HardRejectsRegardlessOfOtherSignals(string candidateTitle)
    {
        // Regression test for a real production bug (2026-09-09): several real movies (Rango,
        // Alexander, Whiplash, Going in Style, Yesterday, Seance, Hunted, Presence, 2012) had
        // their titles overwritten with a Wikipedia "20XX in film" yearly-summary article instead
        // of their own name. Applied at any level -- unlike the season/discography hard-rejects
        // above, no real Chronicle item at ANY level is legitimately titled this way, so there's
        // no level-1+ carve-out needed.
        var context = new MediaSearchContext(Name: "Rango", MediaTypeName: "movies", Year: 2011);
        var result = WikipediaScoring.Score(context,
            Page(candidateTitle, description: "year in film", extract: "A year in film with many notable releases."));

        Assert.True(result.HardReject);
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void Score_YearSummaryArticle_HardRejectsEvenWhenQueryNameItselfIsAlreadyCorrupted()
    {
        // The regex checks the CANDIDATE's own title shape, independent of what the query name
        // currently says -- deliberately, so an item already corrupted by this bug (its own Name
        // now literally "2011 in film") doesn't reconfirm the same wrong match on its next
        // re-scrape just because the corrupted query name now shares tokens with it.
        var context = new MediaSearchContext(Name: "2011 in film", MediaTypeName: "movies", Year: 2011);
        var result = WikipediaScoring.Score(context,
            Page("2011 in film", description: "year in film", extract: "A year in film with many notable releases."));

        Assert.True(result.HardReject);
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

    // ── Season-specific articles (level-0 show searches only) ───────────────────

    [Theory]
    [InlineData("3rd Rock from the Sun season 1")]
    [InlineData("3rd Rock from the Sun (season 1)")]
    [InlineData("3rd Rock from the Sun series 1")]
    public void Score_SeasonSpecificTitle_AtLevel0_HardRejectsRegardlessOfOtherSignals(string candidateTitle)
    {
        // Regression test for a real production bug: "3rd Rock from the Sun season 1" out-
        // scored the show's own article for a level-0 show search (top score 77, driven by
        // high title similarity, a generic "television series" description satisfying the
        // type-keyword signal, and year corroboration) -- so the show's own Name field got set
        // to "3rd Rock from the Sun season 1" instead of just "3rd Rock from the Sun". A
        // season-specific article must never win a level-0 search, no matter how well it
        // otherwise scores.
        var context = new MediaSearchContext(
            Name: "3rd Rock from the Sun", MediaTypeName: "tv", HierarchyLevel: 0, Year: 1996);
        var result = WikipediaScoring.Score(
            context, Page(candidateTitle, description: "Season of an American television series"));

        Assert.True(result.HardReject);
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void Score_SeasonSpecificTitle_AtLevel1_DoesNotHardReject()
    {
        // The same title shape is exactly correct when the search context IS the season --
        // this rule must only fire for level-0 (whole-show) searches.
        var context = new MediaSearchContext(
            Name: "Season 1", MediaTypeName: "tv", HierarchyLevel: 1,
            ParentName: "3rd Rock from the Sun");
        var result = WikipediaScoring.Score(
            context, Page("3rd Rock from the Sun season 1", description: "Season of an American television series"));

        Assert.False(result.HardReject);
    }

    // ── Discography/filmography/bibliography list pages (level-0 artist/people searches only) ──

    [Theory]
    [InlineData("Limp Bizkit discography")]
    [InlineData("Tom Hanks filmography")]
    [InlineData("Stephen King bibliography")]
    public void Score_DiscographyFilmographyBibliographyTitle_AtLevel0_HardRejects(string candidateTitle)
    {
        // Regression test for a real production bug (2026-08-03): a level-0 "Limp Bizkit"
        // artist search matched "Limp Bizkit discography" -- the artist's own name appears
        // verbatim as a prefix so title similarity scores it highly, and a generic "American
        // band" description satisfies the type-keyword signal just as well as the band's own
        // article would. Chronicle's core enrichment then used the article's own title as the
        // artist item's Name. A discography/filmography/bibliography list page must never win
        // a level-0 artist/person search, no matter how well it otherwise scores.
        var context = new MediaSearchContext(
            Name: "Limp Bizkit", MediaTypeName: "music", HierarchyLevel: 0);
        var result = WikipediaScoring.Score(
            context, Page(candidateTitle, description: "Discography of an American band"));

        Assert.True(result.HardReject);
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void Score_DiscographyTitle_AtLevel1_DoesNotHardReject()
    {
        // A track/album can legitimately be named this way (rare, but not this rule's concern) --
        // the hard-reject must only fire for level-0 (whole-artist) searches. Query name shares
        // the candidate's own tokens (unlike a real "different work entirely" case) so this test
        // isolates just the level-0-only carve-out, not the separate zero-title-overlap floor
        // (see Score_ZeroTitleOverlap_HardRejectsEvenWithStrongTypeAndYearSignals above) --
        // otherwise this would hard-reject for an unrelated reason and no longer test what its
        // name says it tests.
        var context = new MediaSearchContext(
            Name: "Limp Bizkit Discography", MediaTypeName: "music", HierarchyLevel: 1,
            ParentName: "Limp Bizkit");
        var result = WikipediaScoring.Score(
            context, Page("Limp Bizkit discography", description: "Discography of an American band"));

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
