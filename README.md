# Chronicle.Plugin.Wikipedia

[![Latest Release](https://img.shields.io/github/v/release/thegoddamnbeckster/Chronicle.Plugin.Wikipedia?label=Chronicle.Plugin.Wikipedia&color=000000)](https://github.com/thegoddamnbeckster/Chronicle.Plugin.Wikipedia/releases/latest)

Broad, low-priority fallback metadata source plugin for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle)
that fetches article summaries, full section text, images, and biographical data from
[Wikipedia](https://wikipedia.org).

**Plugin ID:** `chronicle.plugin.wikipedia`
**Version:** 1.0.0
**Media Types:** Movies, TV, Music, Books, Audiobooks, Games, Podcasts, Fan Edits, and People (`movies`, `tv`, `music`, `book`, `audiobook`, `game`, `podcast`, `fanedits`, `people`)
**Auth:** None — public read access, identified only by a configured User-Agent contact
**Data source:** Wikipedia's MediaWiki Action API + REST API (official, documented, public)

---

## Table of Contents

- [Overview](#overview)
- [Architecture](#architecture)
- [Rate Limiting Strategy](#rate-limiting-strategy)
- [Data Model](#data-model)
- [Article Extraction](#article-extraction)
- [Scoring Strategy](#scoring-strategy)
- [Settings Schema](#settings-schema)
- [manifest.json](#manifestjson)
- [Background Tasks](#background-tasks)
- [Logging](#logging)
- [Testing](#testing)
- [Error Handling](#error-handling)
- [Repository Structure](#repository-structure)
- [Building & Packaging](#building--packaging)
- [Design Documents](#design-documents)

---

## Overview

Wikipedia is a free, multilingual, crowd-edited encyclopedia with extremely broad but uneven
coverage — flagship movies, major albums, and notable people have detailed articles; individual
songs, TV episodes, and minor tracks usually do not. This plugin treats that as expected
behavior, not a defect: scoring is built to return confident matches for well-covered items and
quietly return nothing for items Wikipedia genuinely has no article about.

Unlike Chronicle's other metadata providers, this plugin is deliberately **not** an authority
for any consumable-media type — it never sets a `DisplayName` for `movies`, `tv`, `music`, etc.,
so it never creates or claims ownership of those types in Chronicle's `media_types` table, and
its `DefaultPriority` is always the lowest in the field-priority stack. It exists to fill gaps
other providers leave (a fuller plot summary, an article's full section text, additional
photos), not to compete with TMDB, MusicBrainz, or Hardcover for primary authority. The one
exception is `people`: nothing else in Chronicle's plugin ecosystem declares that type, so this
plugin is its canonical registrant.

This plugin uses Wikipedia's official, documented public APIs — no scraping, no unofficial
endpoints. It carries no API key (none is required for read access), but Wikimedia's
[User-Agent policy](https://meta.wikimedia.org/wiki/User-Agent_policy) requires every client to
identify itself with contact information, which is a **required setting** you provide — never
hardcoded, since this is a per-instance operator identity, not a plugin secret.

---

## Architecture

This plugin implements `IMetadataProvider` from `Chronicle.Plugins`.

```
Chronicle Host
    │
    ├── Configure(settings)        ← language + contact info + throttle/logging settings stored
    │
    ├── HealthCheckAsync()         ← fetch a known reference page; true if reachable
    │
    ├── SearchAsync(context)       ← one combined MediaWiki query: search + short description
    │       │                        + thumbnail + Wikidata id, all in one request
    │       └─ returns scored candidates with ExternalId = "wikipedia:{lang}:{title}"
    │
    ├── GetByIdAsync(externalId)   ← REST full-article HTML (Parsoid) + a detail query for the
    │       │                        poster/categories/wikidata id — two requests total
    │       └─ returns MediaMetadata: Title, Overview (lead section), PosterUrl, Tags
    │          (filtered categories), AdditionalImages (article-wide, deduped by file
    │          identity), ExtendedData (every section's heading+text, skipped boilerplate
    │          section names, all categories, Wikidata id, and — for biography articles —
    │          best-effort birth/death dates)
    │
    └── GetImageAsync(url)         ← downloads image bytes; refuses any non-wikimedia.org host
```

### Why two requests in `GetByIdAsync`, not one

Wikipedia's REST `/page/html/{title}` endpoint returns the entire current article as Parsoid
HTML in a single request regardless of article length — that's what makes full-article capture
well-mannered (no per-section round trips). It does **not** carry the "best single image for
this page" that the `pageimages` Action API module computes, nor page categories — a separate,
much smaller request gets both. Both requests are still routed through the same throttled
client, so this stays within the plugin's configured request-rate floor.

### Stateless Design

In keeping with Chronicle's plugin contract, `WikipediaMetadataProvider` is stateless between
`Configure()` calls — all live configuration (language, contact info, throttle floor, max
images, log level) lives in instance fields populated once at configure time, not persisted
anywhere else.

### Dependency Overview

| Dependency | Purpose |
|---|---|
| `HtmlAgilityPack` | Parses the Parsoid article HTML — section splitting, image collection, non-prose stripping |
| `System.Net.Http.HttpClient` | HTTP transport, self-constructed (no `IHttpClientFactory` dependency) |

---

## Rate Limiting Strategy

Wikimedia publishes no hard numeric rate limit for a well-identified client on the public API
(unlike, say, MusicBrainz's documented 1 req/sec) — their actual guidance is qualitative:
serialize requests, set a real User-Agent, don't hammer a shared, donation-funded resource. This
plugin is deliberately far more conservative than that guidance requires.

### Rules

| Rule | Value | Rationale |
|---|---|---|
| Minimum inter-request gap | **100 ms** (10 req/s), configurable | An order of magnitude more conservative than Wikimedia's own guidance |
| Minimum floor (absolute) | **50 ms** | Prevents a misconfigured `0` from disabling throttling entirely |
| Implementation | `SemaphoreSlim(1,1)` + `DateTime.MinValue`-sentinel elapsed check | Serializes every outbound request; no burst possible |
| Applies to | ALL requests — search, detail, article HTML, health check, image download | No request is exempt |
| Retry on 429/503 | Up to 3 retries, honoring `Retry-After` when present, else exponential backoff (2s → 4s → 8s → capped 16s) | Kept lower than a naive higher retry budget because Chronicle's host-side `ProviderCallGuard` hard-kills any provider call at 25s regardless, and `GetByIdAsync` already makes two sequential requests before any retry logic runs |

The rate limiter uses a `DateTime.MinValue` "no previous request yet" sentinel rather than
starting a `Stopwatch` at construction time — the latter would incorrectly delay the very first
request a freshly-configured client makes, since "time since construction" isn't the same thing
as "time since the last actual request."

### Contact Info Is a Setting, Not a Constant

Wikimedia's User-Agent policy requires a way to reach the operator of an automated client. This
plugin does not hardcode any identity — **you** supply a URL or email in the plugin's settings,
sent as part of the User-Agent on every request:

```
User-Agent: Chronicle-Wikipedia-Plugin/{version} (+{your contact_info})
```

This is a required setting; the plugin refuses to configure without it.

---

## Data Model

### ExternalId Format

```
wikipedia:{lang}:{title}
```

Where `{lang}` is the Wikipedia language subdomain (e.g. `en`, `de`, `simple`) and `{title}` is
the page title with spaces as underscores — Wikipedia's own URL convention. Example:
`wikipedia:en:The_Batman_(film)`.

Fix Match also accepts a bare `lang:Title` entry, or a pasted Wikipedia URL
(`https://en.wikipedia.org/wiki/The_Batman_(film)`, including the `.m.` mobile subdomain). Every
URL form is validated against a strict host pattern (`^([a-z0-9-]+\.)?(m\.)?wikipedia\.org$`)
**before** any part of it is trusted or used to build an outbound request — the SSRF guard
`PLUGIN_AUTHORING.md` requires for Fix Match URL normalization.

### MediaMetadata Mapping

| MediaMetadata field | Source | Notes |
|---|---|---|
| `Title` | Detail query's canonical title | Includes any disambiguation suffix, e.g. `"The Batman (film)"` |
| `Overview` | The article's lead section (section 0), plain text | Not the short search-preview extract — the full lead paragraph(s) |
| `PosterUrl` | `pageimages` API's `original.source`, falling back to `thumbnail.source` | Wikipedia's own algorithmic "best single image" pick — not a heuristic over in-article images |
| `Tags` | Page categories, `Category:` prefix stripped, maintenance/meta categories filtered out (see `CategoryStoplistPrefixes`), capped at 15 | e.g. `"2022 films"`, not `"CS1 errors: dates"` |
| `AdditionalImages` | Every other article-body image, deduped against the poster and against each other by extracted file identity (not raw URL — see below), capped by the `max_images` setting | `Type = "Article"` for all — Wikipedia has no front/back/still taxonomy the way TMDB does |
| `ExtendedData` | See schema below | Sections, skipped boilerplate section names, all categories, Wikidata id, best-effort birth/death dates |
| `Year`, `RuntimeMinutes`, `Genres`, `Cast`, `Crew`, `Rating` | Not populated | Wikipedia has no structured fields for these and is not the authority for them — left for TMDB/MusicBrainz/Hardcover |

**Image identity, not raw URL:** Wikipedia serves the same underlying file at different URLs for
its full-size vs. thumbnail forms (`.../f/f7/Poster.jpg` vs.
`.../thumb/f/f7/Poster.jpg/300px-Poster.jpg`). Comparing raw URL strings fails to recognize
these as the same image — the plugin instead extracts a content-identity key (last path segment,
with any `NNNpx-` thumbnail-size prefix stripped, percent-decoded, lowercased) so the poster
doesn't silently reappear a second time inside `AdditionalImages`.

### ExtendedData Schema

```jsonc
{
  "sections": [
    { "heading": null, "level": 0, "text": "The Batman is a 2022 American superhero film..." },
    { "heading": "Plot", "level": 2, "text": "In his second year of fighting crime..." },
    { "heading": "Cast", "level": 2, "text": "Robert Pattinson as Bruce Wayne..." }
  ],
  "skippedSections": ["References", "External links", "See also", "Further reading"],
  "wikipediaUrl": "https://en.wikipedia.org/wiki/The_Batman_(film)",
  "allCategories": ["2022 films", "American superhero films", "Films directed by Matt Reeves"],
  "imageCount": 14,
  "imagesIncluded": 14,
  "ids": { "wikidata": "Q64768688" },

  // Only present for biography-shaped articles — omitted entirely (not emitted as null)
  // for every other type, best-effort regex extraction from the lead sentence's
  // "(born Month Day, Year[; died Month Day, Year])" opening.
  "bornDate": "1962-07-03T00:00:00",
  "diedDate": null
}
```

`ids.wikidata` is published so Chronicle's cross-reference cascade can seed a future
Wikidata-consuming plugin automatically, per `PLUGIN_AUTHORING.md`'s cross-reference authority
convention.

---

## Article Extraction

Wikipedia's REST `/page/html/{title}` endpoint (Parsoid HTML) wraps each top-level heading's
content in its own `<section data-mw-section-id="N">` element — `N=0` for the untitled lead —
which is what makes section-splitting a DOM walk rather than a wikitext parser.

### What gets kept vs. skipped

- **Kept as prose:** every section's `<p>` text, cleaned of reference markers
  (`<sup class="reference">`), edit-section links, tables (infoboxes/wikitables/navboxes),
  `<style>` blocks, and hatnotes.
- **Skipped (name recorded in `skippedSections`, but images inside are still collected):**
  sections whose heading is an **exact** (not substring) match against
  `References`, `External links`, `See also`, `Further reading`, `Notes`, `Bibliography`,
  `Citations`, `Sources`, `Works cited`, `Footnotes`. Exact-match only matters — a heading like
  "References in popular culture" is legitimate prose and must not be caught by a loose match.
- **Images:** every `<img>` across the whole article body, filtered to drop icon-sized images
  (flags, edit icons, coordinate markers — anything Parsoid reports as narrower than 50px in its
  own source-file width), deduped by content identity, then capped by `max_images`. Deduping and
  capping happen in that order — capping the raw stream *before* dedup would let a duplicate
  image (the same photo often appears in both the infobox and a later gallery/Cast section)
  consume a slot that should go to a genuinely distinct image.

### Redirects

A REST `/page/html/` 404 on a title the search step just returned is rare but possible (search
index lag vs. the live redirect table). The plugin resolves this via one
`action=query&redirects=1` retry — and critically, every subsequent call in `GetByIdAsync`
(the detail/poster lookup, the canonical title, the rebuilt `ExternalId`) uses the **resolved**
title, not the one originally requested, so a redirected article doesn't lose its poster/
categories/Wikidata id or repeat the same redirect resolution on every future resync.

---

## Scoring Strategy

Applies identically at every hierarchy level and every media type — the only per-level
difference is what gets folded into the search query (parent/grandparent name at hierarchy
levels 1–2) and the parent-corroboration signal below.

| Signal | Points | Notes |
|---|---|---|
| Title similarity | 0–45 | Disambiguation suffix stripped before comparing; exact match after normalization scores full, otherwise a Jaccard token-similarity score scaled, floored below 0.5 similarity |
| Media-type keyword match against Wikidata short description | 0–25, or hard-reject | e.g. `"film"`/`"movie"` for movies, occupation words (`"actor"`, `"film director"`, ...) for people. A description that unambiguously names a *different* type — a movie search landing on a video-game article — hard-rejects the candidate rather than scoring it low |
| Disambiguation page | Hard-reject | `pageprops.disambiguation` present means it's a list of links, not an article about the item |
| Year corroboration | 0–20 | Exact year in the short description or extract; ±1 year scores partial. Naturally a no-op for `people`, which has no `Year` search field |
| Parent/grandparent corroboration | 0–15, hierarchy levels 1–2 only | The primary defense against a level-2 search (one episode, one track) matching an unrelated same-titled article — requires the parent/grandparent name to actually appear in the candidate's extract |

Minimum score to appear in results at all: **20**. Chronicle's own confidence threshold (default
60) then decides whether a candidate auto-applies.

**Known, accepted limitation:** common-name collisions for `people` (two different notable
people sharing a name and occupation) have no distinguishing context available in
`MediaSearchContext` to resolve against — these land in the low-score diagnostic band rather
than auto-matching, and need Fix Match.

---

## Settings Schema

| Key | Label | Type | Required | Default | Notes |
|---|---|---|---|---|---|
| `language` | Language | Text | Yes | `en` | Wikipedia language subdomain — one per plugin instance |
| `contact_info` | Contact Info (for User-Agent) | Text | Yes | – | A URL or email identifying this Chronicle instance's operator, per Wikimedia's User-Agent policy. Never hardcoded. |
| `min_request_interval_ms` | Minimum Request Interval (ms) | Number | No | `100` | Floor between outbound requests. Clamped to a 50ms absolute minimum. |
| `max_images` | Max Images per Article | Number | No | `20` | Upper bound on stored additional images per item |
| `log_level` | Log Level | Dropdown (Debug/Info/Warn/Error) | No | `Info` | Verbosity of this plugin's own log file — see [Logging](#logging) |

---

## manifest.json

```json
{
  "plugin_id":             "chronicle.plugin.wikipedia",
  "name":                  "Wikipedia",
  "version":               "1.0.0",
  "author":                "Chronicle Contributors",
  "description":           "Broad fallback summaries, article sections, and images from Wikipedia for any media type.",
  "min_chronicle_version": "0.7.0",
  "entry_type":            "Chronicle.Plugin.Wikipedia.WikipediaMetadataProvider",
  "iconUrl":               "https://en.wikipedia.org/static/apple-touch/wikipedia.png",
  "brandColorLight":       "#000000",
  "brandColorDark":        "#F0F0F0",
  "fixMatchHint":          "Paste a Wikipedia URL, or type lang:Page_Title (e.g. en:The_Batman_(film))",
  "background_tasks": [...]
}
```

---

## Background Tasks

| Task | Schedule | Purpose |
|---|---|---|
| `fetch-missing-metadata` | Daily at 5:00 UTC | Searches Wikipedia for items that don't have a match yet |
| `resync-all-metadata` | Weekly Sunday 6:00 UTC (disabled by default) | Re-fetches article text/sections/images for everything already matched — articles change often, so this runs a more frequent-leaning cadence than most providers' resync task, though it's still opt-in |

---

## Logging

Every request, throttle wait, retry, scoring decision, and section/image extraction result is
logged to this plugin's own rolling log file — **not** routed through Chronicle's host-side
Serilog pipeline. That's a deliberate choice, not an oversight: Chronicle loads each plugin into
an isolated `PluginLoadContext`, and a plugin carrying its own copy of `Serilog.dll` (a normal
NuGet reference would do exactly that) would have `Serilog.Log` resolve to a *different* type
identity than the host's — the same class of bug `PLUGIN_AUTHORING.md` warns about for
`Chronicle.Plugins.dll` itself. Calls would silently vanish into Serilog's default no-op logger
instead of the host's configured sinks. Writing directly to a dedicated file sidesteps the
question entirely and is guaranteed correct regardless of assembly isolation.

- **Location:** `plugins/chronicle.plugin.wikipedia/logs/wikipedia-{yyyyMMdd}.log` (next to the
  plugin DLL — rolls automatically at UTC midnight, no restart needed).
- **Verbosity:** controlled by the `log_level` setting (Debug/Info/Warn/Error, default Info).
  Debug logs every request URL, throttle wait, and per-candidate scoring decision — useful when
  diagnosing why a specific item matched or didn't, noisy for normal operation.
- **A logging failure never breaks enrichment** — write errors (disk full, permissions) are
  swallowed at the logging layer, since diagnostics must never be able to take down the actual
  operation they're describing.

---

## Testing

`tests/Chronicle.Plugin.Wikipedia.Tests` — 67 xUnit tests, no network access required (HTTP
calls are stubbed via an in-memory `HttpMessageHandler`, mirroring the pattern
`Chronicle.Plugin.MusicBrainz`'s test suite already uses):

- **Scoring** — every signal individually, hard-reject paths, the 100-point cap, threshold
  boundaries.
- **External ID parsing** — native format, bare `lang:Title`, every pasted-URL form (including
  the mobile subdomain), and the SSRF host guard specifically (untrusted hosts that share a
  substring with `wikipedia.org` must still be rejected, not just obviously-wrong hosts).
- **Article HTML parsing** — section splitting, exact-vs-substring boilerplate matching,
  reference-marker stripping, icon-size image filtering, the image-identity dedup, and the
  cap-after-dedup ordering specifically (a regression test for a bug caught in review).
- **Rate limiter** — first-call-doesn't-wait, floor enforcement, the absolute-minimum clamp.
- **HTTP client** — retry-then-succeed and retry-exhaustion on 429/503, `Retry-After` honoring,
  the zero-results response shape, the SSRF guard on image downloads.
- **End-to-end redirect handling** — a dedicated regression test drives `GetByIdAsync` through a
  simulated redirect and asserts the *subsequent* detail lookup uses the resolved title, not the
  stale original (the highest-severity bug caught during this plugin's code review).

```powershell
cd tests
dotnet test
```

---

## Error Handling

| Condition | Behaviour |
|---|---|
| Search returns zero results | Empty candidate list — not an error. MediaWiki omits the `query` key entirely rather than erroring on "nothing found." |
| Article title not found (404) | One redirect-resolution retry; if that also fails, `NotFoundException`-shaped `HttpRequestException` propagates |
| HTTP 429 / 503 | Up to 3 retries with `Retry-After`-aware exponential backoff; final failure propagates as `HttpRequestException` |
| Malformed API parameters | MediaWiki returns HTTP 200 with a top-level `"error"` object — checked explicitly on every parsed response, not just non-2xx statuses |
| Missing `contact_info` setting | `Configure()` throws `InvalidOperationException` immediately — the plugin refuses to start without it |
| Not configured yet | `PluginAuthException` on any operation attempted before `Configure()` succeeds |
| Untrusted image host | `GetImageAsync`/`GetImageBytesAsync` refuse any URL whose host isn't `*.wikimedia.org` |
| `CancellationToken` cancelled | Propagates immediately, not retried |

---

## Repository Structure

```
Chronicle.Plugin.Wikipedia/
├── Chronicle.Plugin.Wikipedia.csproj
├── README.md
├── manifest.json
├── WikipediaMetadataProvider.cs   # IMetadataProvider — search, get by ID, scoring glue, Fix Match
├── WikipediaClient.cs             # Throttled HTTP client — Action API + REST API, retry/backoff
├── WikipediaRateLimiter.cs        # Per-instance rate limiting (SemaphoreSlim + sentinel timestamp)
├── ArticleHtmlParser.cs           # Parsoid HTML -> sections/images, born/died extraction
├── WikipediaScoring.cs            # Candidate scoring — title/type/year/parent signals
├── PluginLog.cs                   # Self-contained rolling file logger (see Logging above)
├── Models/
│   └── WikipediaModels.cs         # MediaWiki API response DTOs
└── tests/
    └── Chronicle.Plugin.Wikipedia.Tests/  # xUnit — see Testing above
```

---

## Building & Packaging

Both repositories must be cloned as siblings for the project reference to resolve:

```
<base>\
  Chronicle\
  Chronicle.Plugin.Wikipedia\
```

```powershell
dotnet build -c Release
```

Deploy to Chronicle:

```powershell
$pluginDir = "..\Chronicle\src\Chronicle.API\plugins\chronicle.plugin.wikipedia"
New-Item -ItemType Directory -Force $pluginDir
dotnet build -c Release
Copy-Item "bin\Release\net9.0\Chronicle.Plugin.Wikipedia.dll" $pluginDir
Copy-Item "bin\Release\net9.0\HtmlAgilityPack.dll"             $pluginDir
Copy-Item "manifest.json"                                      $pluginDir
```

> **Important:** `Chronicle.Plugins.dll` must **not** be in the plugin directory — Chronicle
> provides it. The `.csproj` sets `Private="false"` on the `Chronicle.Plugins` project reference
> to ensure this. `HtmlAgilityPack.dll`, on the other hand, **must** be copied — Chronicle's
> plugin loader expects it physically alongside the plugin DLL and doesn't consult `deps.json`.

```xml
<ProjectReference Include="..\Chronicle\src\Chronicle.Plugins\Chronicle.Plugins.csproj"
                  Private="false" ExcludeAssets="runtime" />
```

For local development against a Chronicle checkout, `Chronicle\scripts\RunTestEnvironment.ps1`
builds and deploys this plugin automatically alongside every other bundled plugin — no manual
copy step needed day-to-day.

---

## Design Documents

The full design rationale — including options considered and rejected — lives in the main
Chronicle repository:

- [`docs/plugins/PLUGIN_WIKIPEDIA_V4.md`](https://github.com/thegoddamnbeckster/Chronicle/blob/main/docs/plugins/PLUGIN_WIKIPEDIA_V4.md) — the complete implementation specification this plugin was built from.
- [`docs/plans/2026-08-28-people-section-design.md`](https://github.com/thegoddamnbeckster/Chronicle/blob/main/docs/plans/2026-08-28-people-section-design.md) — the broader People-section design that made this plugin the canonical registrant of the `people` media type.

---

## License

MIT – see [LICENSE](LICENSE).

---

*Chronicle.Plugin.Wikipedia is an independent community plugin and is not affiliated with,
endorsed by, or officially supported by the Wikimedia Foundation.*
