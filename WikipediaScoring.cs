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

            if (similarity >= 0.5)
            {
                var points = (int)Math.Round(45 * similarity);
                score += points;
                reasons.Add($"title similarity {similarity:P0}");
            }
        }

        // Signal 2 — media-type keyword match against the Wikidata short description.
        var description = candidate.Terms?.Description?.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(description))
        {
            var keywords = GetTypeKeywords(context.MediaTypeName, context.HierarchyLevel);
            var descLower = description.ToLowerInvariant();

            if (keywords.Count > 0 && keywords.Any(k => descLower.Contains(k, StringComparison.Ordinal)))
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
            if (otherKeywords.Count > 0 && otherKeywords.Any(k => descriptionLower.Contains(k, StringComparison.Ordinal)))
                return true;
        }
        return false;
    }

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
