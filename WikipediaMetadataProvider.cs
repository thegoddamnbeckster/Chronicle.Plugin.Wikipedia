using System.Text.Json;
using System.Text.RegularExpressions;
using Chronicle.Plugin.Wikipedia.Models;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.Wikipedia;

/// <summary>
/// Chronicle metadata provider for Wikipedia. A broad, low-priority fallback provider for
/// every media type — never the authority for a type it doesn't declare DisplayName on
/// (see GetSupportedMediaTypes), and DefaultPriority is deliberately low everywhere so it
/// only wins a field in Metadata Assignment when an admin explicitly ranks it above the
/// type-specific providers (TMDB, MusicBrainz, Hardcover, ...) or when nothing else answers.
/// See docs/plugins/PLUGIN_WIKIPEDIA_V4.md for the full design.
/// </summary>
public sealed class WikipediaMetadataProvider : IMetadataProvider
{
    // ── IMetadataProvider identity ────────────────────────────────────────────

    public string PluginId => "chronicle.plugin.wikipedia";
    public string Name => "Wikipedia";
    public string Version => "1.0.0";
    public string Author => "Chronicle Contributors";

    // ── Settings keys ─────────────────────────────────────────────────────────

    private const string KeyLanguage = "language";
    private const string KeyContactInfo = "contact_info";
    private const string KeyLogLevel = "log_level";
    private const string KeyMinRequestIntervalMs = "min_request_interval_ms";
    private const string KeyMaxImages = "max_images";

    // ── Live configuration (populated by Configure()) ──────────────────────────

    private WikipediaClient? _client;
    private string _language = "en";
    // 0 = unlimited (default) -- per-user directive (2026-08-29): "grab everything that is
    // available and store it in chronicle. you are not to discard anything." A positive value
    // remains available for anyone who explicitly wants to bound storage for a heavily-
    // illustrated article, but nothing is capped unless they opt into that themselves.
    private int _maxImages = 0;

    /// <summary>Test-only constructor that injects a pre-built client.</summary>
    internal WikipediaMetadataProvider(WikipediaClient client, string language = "en", int maxImages = 0)
    {
        _client = client;
        _language = language;
        _maxImages = maxImages;
    }

    /// <summary>Required for public instantiation by the host (no-arg).</summary>
    public WikipediaMetadataProvider() { }

    // ── IMetadataProvider: static declarations ────────────────────────────────

    /// <summary>
    /// All entries except "people" have empty DisplayName — Wikipedia never owns/creates
    /// those media types. "people" is the one exception: nothing else in Chronicle's plugin
    /// ecosystem declares it, so Wikipedia is its canonical registrant (see
    /// docs/plans/2026-08-28-people-section-design.md). DefaultPriority stays the same low
    /// 90 everywhere regardless — being a type's registrant doesn't mean out-ranking a
    /// richer future source for individual fields; that's Metadata Assignment's job.
    /// </summary>
    public MediaTypeSupport[] GetSupportedMediaTypes() =>
    [
        new MediaTypeSupport
        {
            MediaTypeName = "movies",
            DefaultPriority = 90,
            SupportedFields = ["title", "overview", "poster_url", "tags", "extended_data"],
        },
        new MediaTypeSupport
        {
            MediaTypeName = "tv",
            DefaultPriority = 90,
            SupportedFields = ["title", "overview", "poster_url", "tags", "extended_data"],
        },
        new MediaTypeSupport
        {
            MediaTypeName = "music",
            DefaultPriority = 90,
            SupportedFields = ["title", "overview", "poster_url", "tags", "extended_data"],
        },
        new MediaTypeSupport
        {
            MediaTypeName = "book",
            DefaultPriority = 90,
            SupportedFields = ["title", "overview", "poster_url", "tags", "extended_data"],
        },
        new MediaTypeSupport
        {
            MediaTypeName = "audiobook",
            DefaultPriority = 90,
            SupportedFields = ["title", "overview", "poster_url", "tags", "extended_data"],
        },
        new MediaTypeSupport
        {
            MediaTypeName = "people",
            DisplayName = "People",
            HierarchyLevels = 1,
            InteractionVerb = "viewed",
            ProgressUnit = "percent",
            DefaultPriority = 90,
            SupportedFields = ["title", "overview", "poster_url", "tags", "extended_data",
                                "birth_date", "death_date"],
        },
        new MediaTypeSupport
        {
            MediaTypeName = "game",
            DefaultPriority = 90,
            SupportedFields = ["title", "overview", "poster_url", "tags", "extended_data"],
        },
        new MediaTypeSupport
        {
            MediaTypeName = "podcast",
            DefaultPriority = 90,
            SupportedFields = ["title", "overview", "poster_url", "tags", "extended_data"],
        },
        new MediaTypeSupport
        {
            MediaTypeName = "fanedits",
            DefaultPriority = 90,
            SupportedFields = ["title", "overview", "poster_url", "tags", "extended_data"],
        },
    ];

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key = KeyLanguage,
                Label = "Language",
                Description = "Wikipedia language subdomain to search, e.g. en, de, fr, ja. " +
                               "One language per plugin instance.",
                Type = SettingType.Text,
                Required = true,
                DefaultValue = "en",
            },
            new SettingDefinition
            {
                Key = KeyContactInfo,
                Label = "Contact Info (for User-Agent)",
                Description = "A URL or email identifying this Chronicle instance's operator, " +
                               "sent in every request's User-Agent header per Wikimedia's " +
                               "User-Agent policy (https://meta.wikimedia.org/wiki/User-Agent_policy). " +
                               "Use your Chronicle instance's repo/homepage URL, or an email you monitor.",
                Type = SettingType.Text,
                Required = true,
            },
            new SettingDefinition
            {
                Key = KeyMinRequestIntervalMs,
                Label = "Minimum Request Interval (ms)",
                Description = "Floor on time between outbound requests to Wikipedia. 100ms " +
                               "(10 req/s) is already conservative relative to Wikimedia's own " +
                               "guidance. Cannot be set below 50ms.",
                Type = SettingType.Number,
                Required = false,
                DefaultValue = "100",
            },
            new SettingDefinition
            {
                Key = KeyMaxImages,
                Label = "Max Images per Article",
                Description = "Upper bound on how many images from one article are stored as " +
                               "additional images. 0 (default) means unlimited -- every image " +
                               "the article has is stored, per Chronicle's own lossless-" +
                               "ingestion rule. Set a positive number only if you specifically " +
                               "want to bound storage for heavily-illustrated articles.",
                Type = SettingType.Number,
                Required = false,
                DefaultValue = "0",
            },
            new SettingDefinition
            {
                Key = KeyLogLevel,
                Label = "Log Level",
                Description = "Verbosity of this plugin's own log file (plugins/" +
                               "chronicle.plugin.wikipedia/logs/). Debug logs every request, " +
                               "throttle wait, and scoring decision — useful when diagnosing a " +
                               "match, noisy otherwise.",
                Type = SettingType.Dropdown,
                Required = false,
                DefaultValue = "Info",
                Options =
                [
                    new SelectOption { Value = "Debug", Label = "Debug — everything" },
                    new SelectOption { Value = "Info", Label = "Info — normal operation (default)" },
                    new SelectOption { Value = "Warn", Label = "Warning — problems only" },
                    new SelectOption { Value = "Error", Label = "Error — failures only" },
                ],
            },
        ],
    };

    // ── IMetadataProvider: configuration ─────────────────────────────────────

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        settings.TryGetValue(KeyLanguage, out var language);
        settings.TryGetValue(KeyContactInfo, out var contactInfo);
        settings.TryGetValue(KeyMinRequestIntervalMs, out var minIntervalStr);
        settings.TryGetValue(KeyMaxImages, out var maxImagesStr);
        settings.TryGetValue(KeyLogLevel, out var logLevelStr);

        PluginLog.SetMinLevel(Enum.TryParse<LogLevel>(logLevelStr, ignoreCase: true, out var lvl) ? lvl : LogLevel.Info);

        if (string.IsNullOrWhiteSpace(contactInfo))
        {
            PluginLog.Error("Configure: 'contact_info' is missing — plugin cannot start.");
            throw new InvalidOperationException(
                "Wikipedia plugin requires 'contact_info' to be configured — " +
                "Wikimedia's User-Agent policy requires a way to contact the operator.");
        }

        _language = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim().ToLowerInvariant();
        _maxImages = int.TryParse(maxImagesStr, out var mi) && mi > 0 ? mi : 0;

        var minInterval = int.TryParse(minIntervalStr, out var interval) ? interval : 100;
        var userAgent = $"Chronicle-Wikipedia-Plugin/{Version} (+{contactInfo})";

        _client = new WikipediaClient(_language, userAgent, minInterval);

        PluginLog.Info($"Configure: language={_language}, minRequestIntervalMs={minInterval}, " +
                        $"maxImages={_maxImages}");
    }

    // ── IMetadataProvider: search ─────────────────────────────────────────────

    public async Task<IReadOnlyList<ScoredCandidate>> SearchAsync(
        MediaSearchContext context, CancellationToken ct = default)
    {
        EnsureConfigured();

        var query = BuildSearchQuery(context);
        PluginLog.Info($"SearchAsync: name=\"{context.Name}\" mediaType={context.MediaTypeName} " +
                        $"level={context.HierarchyLevel} year={context.Year} -> query=\"{query}\"");
        var results = await _client!.SearchAsync(query, ct).ConfigureAwait(false);

        if (results.Count == 0 && !string.IsNullOrWhiteSpace(context.FilenameStem) &&
            !string.Equals(context.FilenameStem, context.Name, StringComparison.OrdinalIgnoreCase))
        {
            // Single fallback retry using FilenameStem, per MediaSearchContext's own
            // documented convention — bounded to one extra request, not a walk of AltTitles.
            PluginLog.Info($"SearchAsync: zero results for \"{query}\", retrying with FilenameStem \"{context.FilenameStem}\"");
            results = await _client.SearchAsync(context.FilenameStem, ct).ConfigureAwait(false);
        }

        var candidates = new List<ScoredCandidate>();
        foreach (var page in results)
        {
            var result = WikipediaScoring.Score(context, page);
            PluginLog.Debug($"SearchAsync: candidate \"{page.Title}\" score={result.Score} " +
                             $"hardReject={result.HardReject} reason=\"{result.Reason}\"");

            if (result.HardReject || !WikipediaScoring.MeetsReturnThreshold(result.Score))
                continue;

            var metadata = new MediaMetadata
            {
                ExternalId = BuildExternalId(_language, page.Title),
                Source = "wikipedia",
                Title = page.Title,
                Overview = page.Extract,
                PosterUrl = page.Thumbnail?.Source,
            };

            candidates.Add(new ScoredCandidate(metadata, result.Score, result.Reason));
        }

        var final = candidates.OrderByDescending(c => c.Score).Take(8).ToList();
        PluginLog.Info($"SearchAsync: \"{query}\" -> {final.Count} candidate(s) returned " +
                        (final.Count > 0 ? $"(top score {final[0].Score})" : "(none met threshold)"));
        return final;
    }

    private static string BuildSearchQuery(MediaSearchContext context)
    {
        var query = context.PreciseName ?? context.Name;

        if (context.HierarchyLevel > 0 && !string.IsNullOrWhiteSpace(context.ParentName))
            query = $"{query} {context.ParentName}";

        if (context.HierarchyLevel == 2 && !string.IsNullOrWhiteSpace(context.GrandparentName))
            query = $"{query} {context.GrandparentName}";

        return query;
    }

    // ── IMetadataProvider: get by ID ──────────────────────────────────────────

    public async Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default)
    {
        EnsureConfigured();

        var (lang, requestedTitle) = ParseExternalId(externalId);
        PluginLog.Info($"GetByIdAsync: externalId=\"{externalId}\" -> lang={lang}, title=\"{requestedTitle}\"");

        // `title` here is the EFFECTIVE title after any redirect resolution — every subsequent
        // call (detail lookup, canonical title, rebuilt ExternalId) must use this, not
        // `requestedTitle`. Using the stale pre-redirect title for GetPageDetailsAsync was a
        // real bug caught in review: the detail call would silently miss (or hit the wrong
        // stub page for) any article reached via a redirect, losing poster/categories/wikidata
        // and re-triggering the same redirect dance on every future resync.
        var (html, title) = await FetchArticleHtmlWithRedirectRetryAsync(requestedTitle, ct).ConfigureAwait(false);
        var parsed = ArticleHtmlParser.Parse(html, _maxImages);
        PluginLog.Debug($"GetByIdAsync: \"{title}\" parsed {parsed.Sections.Count} section(s), " +
                         $"skipped {parsed.SkippedSections.Count} boilerplate ({string.Join(", ", parsed.SkippedSections)}), " +
                         $"{parsed.ImageUrls.Count} image(s)");

        var detail = await _client!.GetPageDetailsAsync(title, ct).ConfigureAwait(false);

        var leadText = parsed.Sections.FirstOrDefault(s => s.Heading is null)?.Text ?? string.Empty;
        var (born, died) = ArticleHtmlParser.TryExtractBornDied(leadText);
        if (born is not null)
            PluginLog.Debug($"GetByIdAsync: \"{title}\" born/died extracted: born={born:d}, died={(died is null ? "n/a" : $"{died:d}")}");

        var posterUrl = detail?.Original?.Source ?? detail?.Thumbnail?.Source;
        var posterIdentity = posterUrl is null ? null : ArticleHtmlParser.ExtractImageIdentity(posterUrl);
        var tags = BuildTags(detail?.Categories);
        var additionalImages = parsed.ImageUrls
            // Compare by extracted file identity, not raw URL — Wikipedia serves the same
            // underlying file at different derived URLs (full-size vs. thumbnail path), so
            // exact-string comparison let the poster image reappear once more as a near-
            // duplicate "additional image." Caught in review.
            .Where(url => posterIdentity is null || ArticleHtmlParser.ExtractImageIdentity(url) != posterIdentity)
            .Select(url => new AdditionalImage { Url = url, Type = "Article", ThumbnailUrl = url })
            .ToList();

        var canonicalTitle = detail?.Title ?? title.Replace('_', ' ');

        PluginLog.Info($"GetByIdAsync: \"{title}\" -> title=\"{canonicalTitle}\", overviewChars={leadText.Length}, " +
                        $"poster={(posterUrl is null ? "none" : "yes")}, additionalImages={additionalImages.Count}, " +
                        $"tags={tags.Count}");

        return new MediaMetadata
        {
            ExternalId = BuildExternalId(lang, canonicalTitle),
            Source = "wikipedia",
            Title = canonicalTitle,
            Overview = leadText,
            PosterUrl = posterUrl,
            Tags = tags,
            AdditionalImages = additionalImages,
            ExtendedData = BuildExtendedData(lang, canonicalTitle, parsed, detail, born, died),
        };
    }

    /// <summary>Returns the article HTML plus the EFFECTIVE title it was ultimately fetched
    /// under — the caller must use this returned title for every subsequent call, not the
    /// title it passed in, since a redirect may have occurred.</summary>
    private async Task<(string Html, string Title)> FetchArticleHtmlWithRedirectRetryAsync(string title, CancellationToken ct)
    {
        try
        {
            var html = await _client!.GetArticleHtmlAsync(title, ct).ConfigureAwait(false);
            return (html, title);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            PluginLog.Info($"FetchArticleHtmlWithRedirectRetryAsync: \"{title}\" not found, trying redirect resolution");
            var resolved = await _client!.ResolveRedirectAsync(title, ct).ConfigureAwait(false);
            if (resolved is null)
            {
                PluginLog.Warn($"FetchArticleHtmlWithRedirectRetryAsync: \"{title}\" not found and not a redirect — giving up");
                throw;
            }

            var html = await _client.GetArticleHtmlAsync(resolved, ct).ConfigureAwait(false);
            return (html, resolved);
        }
    }

    private static JsonElement BuildExtendedData(
        string lang, string title, ParsedArticle parsed, WikiDetailPage? detail,
        DateTime? born, DateTime? died)
    {
        var wikipediaUrl = $"https://{lang}.wikipedia.org/wiki/{Uri.EscapeDataString(title.Replace(' ', '_'))}";
        var allCategories = (detail?.Categories ?? [])
            .Select(c => StripCategoryPrefix(c.Title))
            .ToList();

        var payload = new Dictionary<string, object?>
        {
            ["sections"] = parsed.Sections.Select(s => new { heading = s.Heading, level = s.Level, text = s.Text }),
            ["skippedSections"] = parsed.SkippedSections,
            ["wikipediaUrl"] = wikipediaUrl,
            ["allCategories"] = allCategories,
            ["imageCount"] = parsed.ImageUrls.Count,
            ["imagesIncluded"] = parsed.ImageUrls.Count,
            ["ids"] = detail?.PageProps?.WikibaseItem is { } wikidataId
                ? new { wikidata = wikidataId }
                : null,
        };

        // bornDate/diedDate are omitted entirely (not emitted as null) for the common case —
        // they only appear when the regex actually matched a biography-style opening.
        if (born is not null) payload["bornDate"] = born;
        if (died is not null) payload["diedDate"] = died;

        return JsonSerializer.SerializeToElement(payload);
    }

    /// <summary>Drops maintenance/meta categories (CS1 errors, date-format housekeeping, etc.)
    /// that are noise, not genre/subject tags. Not exhaustive by construction, only by
    /// observation — will need occasional extension as Wikipedia's naming evolves.</summary>
    private static readonly string[] CategoryStoplistPrefixes =
    [
        "CS1 ", "Articles with", "Articles containing", "Wikipedia articles", "Pages using",
        "Use mdy dates", "Use dmy dates", "Use British English", "Use American English",
        "All articles", "Short description", "Webarchive template", "Commons category",
    ];

    private static List<string> BuildTags(List<WikiCategory>? categories)
    {
        if (categories is null) return [];

        return categories
            .Select(c => StripCategoryPrefix(c.Title))
            .Where(name => !CategoryStoplistPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Take(15)
            .ToList();
    }

    private static string StripCategoryPrefix(string categoryTitle) =>
        categoryTitle.StartsWith("Category:", StringComparison.OrdinalIgnoreCase)
            ? categoryTitle["Category:".Length..]
            : categoryTitle;

    // ── IMetadataProvider: image ──────────────────────────────────────────────

    public Task<byte[]> GetImageAsync(string url, CancellationToken ct = default)
    {
        EnsureConfigured();
        return _client!.GetImageBytesAsync(url, ct);
    }

    // ── IMetadataProvider: health ─────────────────────────────────────────────

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (_client is null) return false;
        return await _client.PingAsync(ct).ConfigureAwait(false);
    }

    // ── ExternalId convention (Section 8) ────────────────────────────────────

    /// <summary>Format: "wikipedia:{lang}:{title}", title with spaces as underscores
    /// (Wikipedia's own URL convention).</summary>
    private static string BuildExternalId(string lang, string title) =>
        $"wikipedia:{lang}:{title.Replace(' ', '_')}";

    private static readonly Regex WikipediaHostRe =
        new(@"^([a-z0-9-]+\.)?(m\.)?wikipedia\.org$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parses either the native "wikipedia:{lang}:{title}" format, a bare "{lang}:{title}"
    /// Fix Match entry, or a pasted Wikipedia URL. Validates the host against a strict
    /// pattern BEFORE trusting any extracted component — the SSRF guard PLUGIN_AUTHORING.md
    /// requires for Fix Match URL normalization.
    /// </summary>
    internal static (string Lang, string Title) ParseExternalId(string externalId)
    {
        if (externalId.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            externalId.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(externalId, UriKind.Absolute, out var uri) ||
                !WikipediaHostRe.IsMatch(uri.Host))
                throw new ArgumentException($"URL is not a wikipedia.org address: '{externalId}'");

            var hostParts = uri.Host.Split('.');
            // en.wikipedia.org -> "en"; en.m.wikipedia.org -> "en"; wikipedia.org / www.wikipedia.org -> "en"
            var lang = hostParts.Length >= 3 ? hostParts[0] : "en";
            if (string.Equals(lang, "www", StringComparison.OrdinalIgnoreCase) || string.Equals(lang, "m", StringComparison.OrdinalIgnoreCase))
                lang = "en";

            var segments = uri.AbsolutePath.Trim('/').Split('/');
            if (segments.Length < 2 || !string.Equals(segments[0], "wiki", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Cannot extract a page title from Wikipedia URL: '{externalId}'");

            var title = Uri.UnescapeDataString(segments[1]);
            return (lang, title);
        }

        var withoutPrefix = externalId.StartsWith("wikipedia:", StringComparison.OrdinalIgnoreCase)
            ? externalId["wikipedia:".Length..]
            : externalId;

        var parts = withoutPrefix.Split(':', 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            throw new ArgumentException($"Invalid Wikipedia external ID format: '{externalId}'. Expected 'lang:Title'.");

        return (parts[0].ToLowerInvariant(), Uri.UnescapeDataString(parts[1]));
    }

    private void EnsureConfigured()
    {
        if (_client is null)
            throw new PluginAuthException(
                "chronicle.plugin.wikipedia",
                "Wikipedia plugin is not configured — set Contact Info in Settings → Plugins → Wikipedia.");
    }
}
