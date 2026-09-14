using System.Text.RegularExpressions;
using Chronicle.Plugin.Wikipedia.Models;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.Wikipedia;

internal sealed record ScoreResult(int Score, string Reason, bool HardReject);

/// <summary>
/// Numeric scoring for a Wikipedia search candidate against a Chronicle search context.
/// Applies identically at every hierarchy level and every media type — the only per-level
/// difference is the parent/grandparent-corroboration signal (levels 1-2 only). See
/// PLUGIN_WIKIPEDIA_V4.md Section 11 for the full rationale.
/// </summary>
internal static class WikipediaScoring
{
    private const int MinScoreToReturn = 20;

    private static readonly Regex DisambiguationSuffixRe = new(@"\s*\([^)]*\)\s*$", RegexOptions.Compiled);
    private static readonly Regex DisambiguationSuffixCaptureRe = new(@"\(([^)]*)\)\s*$", RegexOptions.Compiled);
    private static readonly Regex NonWordRe = new(@"[^\w\s]", RegexOptions.Compiled);
    private static readonly Regex YearRe = new(@"\b(1[89]\d{2}|20\d{2})\b", RegexOptions.Compiled);

    /// <summary>Generational suffixes and name particles that legitimately appear on one side
    /// of a person's name and not the other WITHOUT signaling a different real person -- unlike
    /// an added surname/given-name (the "Jesse James" vs "Jesse James Dupree" shape the
    /// subset/superset hard-reject below exists to catch). Caught in review (2026-09-14) before
    /// release: the subset/superset check as first written had no such exception, so a query
    /// like "Sammy Davis" against the correct candidate "Sammy Davis Jr." -- or "Guillermo
    /// Toro" against "Guillermo del Toro" -- would hard-reject a genuinely correct match purely
    /// because Chronicle's own stored name happened to omit a suffix/particle the Wikipedia
    /// title carries. When EVERY extra token is one of these, the pair falls through to normal
    /// similarity scoring below instead of an automatic reject.</summary>
    private static readonly HashSet<string> BenignExtraNameTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "jr", "sr", "ii", "iii", "iv", "v",
        "de", "del", "von", "van", "da", "dos", "das", "la", "le", "bin", "al",
    };

    /// <summary>Matches a Wikipedia article title that's about one SEASON of a TV show rather
    /// than the show itself -- "3rd Rock from the Sun season 1", "Fargo (season 3)", "Doctor
    /// Who series 12" -- in either the bare or parenthetical convention Wikipedia uses for
    /// these. Used to hard-reject such a candidate for a level-0 (whole-show) search: the two
    /// are different articles about different things, and no amount of title/type/year scoring
    /// should let a season-specific page stand in for the show's own page. Never applied at
    /// level 1+, where matching exactly this kind of article is the correct outcome.</summary>
    private static readonly Regex SeasonSpecificTitleRe = new(
        @"\b(?:season|series)\s+(?:\d+|one|two|three|four|five|six|seven|eight|nine|ten)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Matches a Wikipedia article title that's a discography/filmography/bibliography
    /// LIST page for an artist/creator rather than that person or group's own page --
    /// "Limp Bizkit discography", "Tom Hanks filmography" -- same class of bug as
    /// SeasonSpecificTitleRe above, for level-0 artist/people searches instead of level-0 show
    /// searches. Confirmed live (2026-08-03): a level-0 "Limp Bizkit" artist search matched
    /// "Limp Bizkit discography" -- title similarity necessarily scores this kind of article
    /// highly since the subject's own name appears verbatim as a prefix, and Chronicle's core
    /// enrichment then used the article's own title as this artist item's Name (a separate,
    /// unrelated bug in the FanartTV plugin's own album-artwork fallback independently caused
    /// this same artist item to absorb 19 real albums via merge -- see that plugin's own fix --
    /// but the wrong "discography" name is this Wikipedia-side bug's doing on its own). Never
    /// applied at level 1+, where a track/album/episode legitimately can be titled this way.</summary>
    private static readonly Regex DiscographySpecificTitleRe = new(
        @"\b(?:discography|filmography|bibliography)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Matches a Wikipedia yearly-summary/list article -- "2011 in film", "2011 in
    /// television", "List of American films of 2011" -- standing in for one specific movie/show.
    /// Confirmed live (2026-09-09): several real movies (Rango, Alexander, Whiplash, ...) had
    /// their titles overwritten with "20XX in film" instead of their own name. Root cause was a
    /// scoring gap, not a search-quality problem (Wikipedia's own search correctly returns the
    /// real "Rango (2011 film)" article first for a "Rango" query) -- see the zero-title-overlap
    /// hard-reject below for that fix. This regex is the independent, second layer: it rejects
    /// the candidate by its own recognizable shape regardless of what the query title currently
    /// says, which matters because an item already corrupted by this bug has a query title that
    /// now partially overlaps the wrong article's own title (the corruption reinforces itself on
    /// the next re-scrape otherwise). Applied at every level -- no real Chronicle item at any
    /// level is legitimately titled this way.</summary>
    private static readonly Regex YearSummaryArticleRe = new(
        @"^(?:List\s+of\s+.*\bfilms?\s+of\s+)?(?:1[89]\d{2}|20\d{2})\s+in\s+(?:film|television)\b|" +
        @"^List\s+of\s+.*\bfilms?\s+of\s+(?:1[89]\d{2}|20\d{2})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Strips a trailing Wikipedia disambiguation parenthetical -- "(film)",
    /// "(1982 film)", "(TV series)", etc. -- from a page title for DISPLAY purposes only.
    /// Never apply this to a page title used as (or to build) an ExternalId: two distinct
    /// Wikipedia articles like "Barbie (film)" and "Barbie (doll)" only stay distinguishable
    /// because of the parenthetical, which is exactly what Wikipedia disambiguation is for.</summary>
    public static string StripDisambiguationSuffix(string title) => DisambiguationSuffixRe.Replace(title, string.Empty);

    /// <summary>Returns just the disambiguator text itself -- "actor" from "Michael Lerner
    /// (actor)", "1982 film" from "Poltergeist (1982 film)" -- or null when the title carries
    /// none. Per-user request (2026-09-03): the disambiguator is genuinely useful data (Wikipedia
    /// only disambiguates a title when ANOTHER article shares the bare name, which is exactly
    /// the situation where two different real people/works can collide onto one Chronicle item)
    /// even though it must never appear in the display Title -- store it explicitly in
    /// ExtendedData instead of leaving it merely inferable from ExternalId/wikipediaUrl, so
    /// future dedup/collision-detection tooling can query it directly rather than re-parsing an
    /// opaque identifier string.</summary>
    public static string? ExtractDisambiguator(string title)
    {
        var match = DisambiguationSuffixCaptureRe.Match(title);
        return match.Success && match.Groups[1].Value.Length > 0 ? match.Groups[1].Value : null;
    }

    /// <summary>Occupation/genre keyword sets per Chronicle media type. Music varies by
    /// hierarchy level (artist vs. album vs. track); everything else is level-independent.</summary>
    private static IReadOnlyList<string> GetTypeKeywords(string? mediaTypeName, int hierarchyLevel)
    {
        var type = mediaTypeName?.ToLowerInvariant();
        return type switch
        {
            "movies" or "movie" or "fanedits" => ["film", "movie"],
            "tv" => ["television series", "tv series", "anime television series"],
            "music" when hierarchyLevel == 0 => ["singer", "band", "musician", "rapper", "musical group"],
            "music" when hierarchyLevel == 1 => ["album", "ep", "soundtrack album"],
            "music" => ["song", "single"],
            "book" or "audiobook" => ["novel", "book", "graphic novel"],
            "game" or "video_game" => ["video game"],
            "podcast" => ["podcast"],
            "people" =>
            [
                "actor", "actress", "film director", "television director", "screenwriter",
                "film producer", "television producer", "musician", "singer", "voice actor",
                "comedian", "television presenter", "cinematographer", "film editor",
                "stunt performer",
            ],
            _ => [],
        };
    }

    public static ScoreResult Score(MediaSearchContext context, WikiSearchPage candidate)
    {
        // Hard-reject: disambiguation page (a list of links, not an article about the item).
        if (candidate.PageProps?.Disambiguation is not null)
            return new ScoreResult(0, "disambiguation page", HardReject: true);

        // Hard-reject: a season-specific article standing in for a level-0 show search.
        // Confirmed live (2026-09-03): "3rd Rock from the Sun season 1" out-scored the show's
        // own article for a level-0 "3rd Rock from the Sun" query -- its generic, templated
        // Wikidata description ("season of an American television series") satisfies the
        // type-keyword signal at least as well as the show's own often genre-specific
        // description ("sitcom") does, while title similarity and year corroboration both
        // still score highly since a season article necessarily repeats the show's name and
        // premiere year. No combination of the other signals can reliably tell these apart, so
        // this checks the actual title shape directly instead.
        if (context.HierarchyLevel == 0 && SeasonSpecificTitleRe.IsMatch(candidate.Title))
            return new ScoreResult(0, "season-specific article, not the show itself", HardReject: true);

        // Hard-reject: a discography/filmography/bibliography list page standing in for a
        // level-0 artist/person search.
        if (context.HierarchyLevel == 0 && DiscographySpecificTitleRe.IsMatch(candidate.Title))
            return new ScoreResult(0, "discography/filmography/bibliography list page, not the artist's own page", HardReject: true);

        // Hard-reject: a yearly-summary/list article ("2011 in film") standing in for one
        // specific movie/show, at any level.
        if (YearSummaryArticleRe.IsMatch(candidate.Title))
            return new ScoreResult(0, "yearly-summary/list article, not the item itself", HardReject: true);

        var reasons = new List<string>();
        var score = 0;

        var candidateTitle = DisambiguationSuffixRe.Replace(candidate.Title, string.Empty);
        var queryName = context.PreciseName ?? context.Name;

        // Signal 1 — title similarity (0-45).
        var cn = Normalize(candidateTitle);
        var qn = Normalize(queryName);
        if (string.Equals(cn, qn, StringComparison.Ordinal))
        {
            score += 45;
            reasons.Add("title exact");
        }
        else
        {
            var similarity = JaccardSimilarity(cn, qn);

            // Jaccard is a set comparison -- it can't tell "Martin Quinn" from "Quinn Martin"
            // apart, since both normalize to the identical token set {martin, quinn} and score a
            // "perfect" 1.0 despite word order being exactly what makes them two different real
            // people. Confirmed live (2026-09-02): a "Martin Quinn" (Star Trek: Strange New
            // Worlds actor) search matched the unrelated "Quinn Martin" (1922-1987 TV producer)
            // article this way, and Chronicle's enrichment pipeline had already merged the wrong
            // bio/photo onto the item by the time its own duplicate-id check caught the mismatch
            // downstream. Reaching similarity >= 0.999 here (cn != qn was already established
            // above) can only mean an identical token SET in a different arrangement -- for
            // "people" specifically, that's the textbook signature of a different real
            // individual, not a legitimate spelling/formatting variant, unlike movie/show titles
            // where word order rarely carries identity. Hard-reject rather than score it as
            // near-exact.
            if (similarity >= 0.999 &&
                string.Equals(context.MediaTypeName, "people", StringComparison.OrdinalIgnoreCase))
            {
                return new ScoreResult(0,
                    $"person name tokens reordered: \"{candidateTitle}\" vs \"{queryName}\"", HardReject: true);
            }

            var candidateTokens = cn.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var queryTokens     = qn.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // Hard-reject: for `people`, the candidate's name tokens are a strict subset or
            // superset of the query's -- every word on the shorter side also appears on the
            // longer side, with at least one extra word tacked on (a middle name, a surname, a
            // suffix). Confirmed live (2026-09-14): two unrelated real people both credited
            // simply as "Jesse James" (a reality-TV mechanic from Monster Garage, and a
            // separate child actor from The Amityville Horror/Jumper) both had their Chronicle
            // item renamed to "Jesse James Dupree" -- a third, unrelated musician -- this way.
            // Jaccard alone can't catch it: {jesse, james} vs {jesse, james, dupree} scores a
            // comfortable 0.667, and combined with the type-keyword signal (Wikipedia's own
            // description for Dupree, "American musician", doesn't conflict with anything
            // people-specific) that alone clears MinScoreToReturn. Signal 3b's birth-year
            // hard-reject doesn't help either -- it only fires once some OTHER provider has
            // already supplied a birth year for this person, which neither side had yet. A
            // shared given/family name (surnames like "James", given names like "Jesse") is
            // common across unrelated people; requiring the FULL token set to agree, not just a
            // majority of it, is the only signal that's actually about identity here. Same
            // reasoning as the reordered-token hard-reject just above, generalized to cover a
            // token added or dropped instead of only shuffled. Never applied to any other media
            // type, where a subtitle/qualifier is routine and not a distinct-identity signal.
            if (string.Equals(context.MediaTypeName, "people", StringComparison.OrdinalIgnoreCase) &&
                candidateTokens.Length > 0 && queryTokens.Length > 0 &&
                candidateTokens.Length != queryTokens.Length)
            {
                var candidateSet = candidateTokens.ToHashSet();
                var querySet     = queryTokens.ToHashSet();
                if (candidateSet.IsSubsetOf(querySet) || querySet.IsSubsetOf(candidateSet))
                {
                    var extraTokens = candidateSet.Count > querySet.Count
                        ? candidateSet.Except(querySet)
                        : querySet.Except(candidateSet);
                    if (!extraTokens.All(t => BenignExtraNameTokens.Contains(t)))
                    {
                        return new ScoreResult(0,
                            $"person name token set mismatch (extra/missing name component): \"{candidateTitle}\" vs \"{queryName}\"",
                            HardReject: true);
                    }
                    // Every extra token is a known-benign suffix/particle (e.g. "Jr.", "del") --
                    // fall through to normal similarity scoring below instead of rejecting.
                }
            }

            // Hard-reject: candidate and query titles are identical except for a different
            // trailing sequel number -- "Toy Story 4" vs "Toy Story 5", "Halloween 2" vs
            // "Halloween 3". Jaccard treats these as ~50% similar (every token but the number
            // matches), which alone is enough to clear the return threshold once combined with
            // the type-keyword signal below -- no year corroboration ever needs to agree.
            // Confirmed live (2026-09-12): a real "Toy Story 4" file (correctly matched
            // everywhere else -- TMDB movie:301528, correct cast/overview) had its Wikipedia
            // match land on the "Toy Story 5" article instead, overwriting its own correct
            // title. Each numbered entry in a franchise gets its own distinct Wikipedia article
            // specifically because they are different works -- a title differing ONLY in that
            // trailing number is never a legitimate match for the query, regardless of what
            // every other signal says.
            if (candidateTokens.Length > 0 && candidateTokens.Length == queryTokens.Length &&
                candidateTokens[..^1].SequenceEqual(queryTokens[..^1]) &&
                IsPlainNumber(candidateTokens[^1]) && IsPlainNumber(queryTokens[^1]) &&
                candidateTokens[^1] != queryTokens[^1])
            {
                return new ScoreResult(0,
                    $"differs only by sequel number: \"{candidateTitle}\" vs \"{queryName}\"", HardReject: true);
            }

            if (similarity >= 0.5)
            {
                var points = (int)Math.Round(45 * similarity);
                score += points;
                reasons.Add($"title similarity {similarity:P0}");
            }
            else if (similarity == 0.0)
            {
                // Hard-reject: literally no shared word between the candidate and query titles.
                // Confirmed live (2026-09-09): several real movies had their titles overwritten
                // with a Wikipedia "20XX in film" yearly-summary article -- title similarity
                // contributed nothing (zero token overlap), so Score() fell through to type+year
                // signals alone (up to 45 points, comfortably over the 20-point return threshold)
                // and Wikipedia's own top hit for a bare-year-shaped query won by default, with
                // zero actual relation to the movie being searched for. No legitimate match
                // should ever share NO tokens at all with the title Chronicle is searching for --
                // type and year alone are corroborating signals, not substitutes for identity.
                return new ScoreResult(0,
                    $"no title overlap at all: \"{candidateTitle}\" vs \"{queryName}\"", HardReject: true);
            }
        }

        // Signal 2 — media-type keyword match against the Wikidata short description.
        var description = candidate.Terms?.Description?.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(description))
        {
            var keywords = GetTypeKeywords(context.MediaTypeName, context.HierarchyLevel);
            var descLower = description.ToLowerInvariant();

            if (keywords.Count > 0 && keywords.Any(k => ContainsKeyword(descLower, k)))
            {
                score += 25;
                reasons.Add("type match");
            }
            else if (LooksLikeConflictingType(descLower, context.MediaTypeName, context.HierarchyLevel))
            {
                return new ScoreResult(0, $"description conflicts with expected type: \"{description}\"", HardReject: true);
            }
        }

        // Signal 3 — year corroboration (0-20). Ordinarily a no-op for `people` (context.Year
        // is naturally absent for a person search — no special-casing needed, it just doesn't fire).
        if (context.Year.HasValue)
        {
            var haystack = $"{description} {candidate.Extract}";
            var years = YearRe.Matches(haystack).Select(m => int.Parse(m.Value)).ToList();
            if (years.Contains(context.Year.Value))
            {
                score += 20;
                reasons.Add("year exact");
            }
            else if (years.Any(y => Math.Abs(y - context.Year.Value) == 1))
            {
                score += 12;
                reasons.Add("year ±1");
            }
        }

        // Signal 3b — known birth-year corroboration for people (hard-reject on conflict).
        // Confirmed live (2026-09-14): "Arturo Castro" (the modern Broad City actor, born 1985)
        // matched the WRONG Wikipedia article, "Arturo Castro (Mexican actor)" (a 1918-1975
        // Mexican film actor) -- title-exact (both normalize to bare "Arturo Castro" once the
        // disambiguation suffix strips) plus type-match ("actor" appears in both descriptions)
        // alone scored 70/100, comfortably over MinScoreToReturn, with zero disambiguation
        // between two people sharing a name 67 years apart. Signal 1's own person-name-token
        // hard-reject above only catches a reordered name, not two genuinely different people
        // who happen to share the identical name outright -- and context.Year (Signal 3) is
        // never set for a people query at all (see its own comment). When Chronicle already
        // knows this person's birth year from an earlier-enriched provider, a candidate whose
        // own extract/description states a conflicting year for its subject is almost certainly
        // a different real person wearing the same name -- hard-reject rather than merely
        // down-score, since the other signals here can and did clear the return threshold
        // entirely on their own.
        if (string.Equals(context.MediaTypeName, "people", StringComparison.OrdinalIgnoreCase) &&
            context.KnownBirthYear.HasValue)
        {
            var lifeYearsHaystack = $"{description} {candidate.Extract}";
            var mentionedYears = YearRe.Matches(lifeYearsHaystack)
                .Select(m => int.Parse(m.Value)).Distinct().ToList();
            if (mentionedYears.Count > 0 &&
                mentionedYears.All(y => Math.Abs(y - context.KnownBirthYear.Value) > 1))
            {
                return new ScoreResult(0,
                    $"known birth year {context.KnownBirthYear.Value} matches none of the years " +
                    $"mentioned in the candidate ({string.Join(", ", mentionedYears)})",
                    HardReject: true);
            }
        }

        // Signal 4 — parent/grandparent corroboration (0-15), hierarchy levels 1-2 only.
        if (context.HierarchyLevel > 0 && !string.IsNullOrWhiteSpace(candidate.Extract))
        {
            var corroborator = context.HierarchyLevel == 2
                ? context.GrandparentName ?? context.ParentName
                : context.ParentName;

            if (!string.IsNullOrWhiteSpace(corroborator) &&
                candidate.Extract.Contains(corroborator, StringComparison.OrdinalIgnoreCase))
            {
                score += 15;
                reasons.Add("parent corroborated");
            }
        }

        score = Math.Min(score, 100);
        return new ScoreResult(score, reasons.Count > 0 ? string.Join(", ", reasons) : "no signals", HardReject: false);
    }

    /// <summary>True when a description exists, is non-empty, and unambiguously names a
    /// DIFFERENT type's keyword set with none of the expected type's own keywords present —
    /// the main defense against title collisions (e.g. a movie search landing on a video
    /// game article of the same name).</summary>
    private static bool LooksLikeConflictingType(string descriptionLower, string? mediaTypeName, int hierarchyLevel)
    {
        var expected = GetTypeKeywords(mediaTypeName, hierarchyLevel);
        if (expected.Count == 0) return false;

        foreach (var (otherType, otherLevel) in AllOtherTypeProbes(mediaTypeName))
        {
            var otherKeywords = GetTypeKeywords(otherType, otherLevel);
            if (otherKeywords.Count > 0 && otherKeywords.Any(k => ContainsKeyword(descriptionLower, k)))
                return true;
        }
        return false;
    }

    /// <summary>Whole-word/-phrase match of a type keyword inside a description, not a plain
    /// substring match. Caught in review before release: plain `string.Contains` let "American
    /// filmmaker" match the "movies" keyword "film" (a substring of "filmmaker"), hard-rejecting
    /// a correct "people" candidate via LooksLikeConflictingType as if their description named a
    /// conflicting type. `\b` word-boundary anchors fix this for both single-word keywords
    /// ("film" no longer matches inside "filmmaker" -- there's no boundary between the two m's)
    /// and multi-word keywords ("film director" still needs boundaries at both ends).</summary>
    private static bool ContainsKeyword(string haystackLower, string keyword) =>
        Regex.IsMatch(haystackLower, $@"\b{Regex.Escape(keyword)}\b");

    private static IEnumerable<(string?, int)> AllOtherTypeProbes(string? excludeType)
    {
        string[] probeTypes = ["movies", "tv", "book", "game", "podcast", "people"];
        foreach (var t in probeTypes)
            if (!string.Equals(t, excludeType, StringComparison.OrdinalIgnoreCase))
                yield return (t, 0);
        // Music's three levels have distinct keyword sets — probe each independently.
        if (!string.Equals(excludeType, "music", StringComparison.OrdinalIgnoreCase))
        {
            yield return ("music", 0);
            yield return ("music", 1);
            yield return ("music", 2);
        }
    }

    public static bool MeetsReturnThreshold(int score) => score >= MinScoreToReturn;

    private static string Normalize(string s) =>
        NonWordRe.Replace(s.Trim(), " ").Trim().ToLowerInvariant();

    /// <summary>True for a token that is purely digits ("4", "5") -- deliberately not roman
    /// numerals or spelled-out numbers ("IV", "four"): the live-confirmed bug this guards
    /// against is specifically the plain-digit sequel-numbering convention ("Toy Story 4/5"),
    /// and a narrower check has less room to misfire on an unrelated title that happens to end
    /// in an ordinary word.</summary>
    private static bool IsPlainNumber(string token) => token.Length > 0 && token.All(char.IsDigit);

    private static double JaccardSimilarity(string a, string b)
    {
        var setA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var setB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (setA.Count == 0 && setB.Count == 0) return 1.0;
        if (setA.Count == 0 || setB.Count == 0) return 0.0;

        var intersection = setA.Intersect(setB).Count();
        var union = setA.Union(setB).Count();
        return union == 0 ? 0.0 : (double)intersection / union;
    }
}
