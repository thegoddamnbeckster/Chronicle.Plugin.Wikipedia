using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Chronicle.Plugin.Wikipedia.Models;

namespace Chronicle.Plugin.Wikipedia;

/// <summary>
/// Thin, throttled HTTP client over the MediaWiki Action API and the REST page/html endpoint.
/// Every outbound call — search, detail, article HTML, health check, image download — routes
/// through one shared <see cref="WikipediaRateLimiter"/> gate, same shape as
/// Chronicle.Plugin.MusicBrainz's client.
/// </summary>
internal sealed class WikipediaClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private readonly HttpClient _http;
    private readonly WikipediaRateLimiter _limiter;
    private readonly string _language;

    public WikipediaClient(string language, string userAgent, int minRequestIntervalMs)
    {
        _language = language;
        _limiter = new WikipediaRateLimiter(minRequestIntervalMs);

        _http = new HttpClient();
        // Raw header add, not the strongly-typed UserAgent.ParseAdd — contact_info is
        // free-form user input (a URL or email) and ParseAdd throws FormatException on
        // anything that doesn't strictly match RFC product-token/comment grammar. Matches
        // Chronicle.Plugin.MusicBrainz's client, which takes the same approach for the
        // same reason.
        _http.DefaultRequestHeaders.Add("User-Agent", userAgent);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    /// <summary>Test-only constructor that accepts a pre-built HttpClient and throttle interval.</summary>
    internal WikipediaClient(HttpClient http, string language, int minRequestIntervalMs)
    {
        _http = http;
        _language = language;
        _limiter = new WikipediaRateLimiter(minRequestIntervalMs);
    }

    private string ApiBase => $"https://{_language}.wikipedia.org/w/api.php";
    private string RestBase => $"https://{_language}.wikipedia.org/api/rest_v1";

    // ── Search ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Combined search + short description + thumbnail in one request (Section 5 of the
    /// design spec). Returns an empty list for zero results — MediaWiki doesn't HTTP-error
    /// on "nothing found," it just omits the `query` key entirely.
    /// </summary>
    public async Task<List<WikiSearchPage>> SearchAsync(string query, CancellationToken ct)
    {
        PluginLog.Debug($"SearchAsync: query=\"{query}\" lang={_language}");

        var url = $"{ApiBase}?action=query&generator=search&gsrsearch={Uri.EscapeDataString(query)}" +
                  "&gsrlimit=8&gsrnamespace=0&prop=extracts%7Cpageimages%7Cpageprops%7Cpageterms" +
                  "&exintro=1&explaintext=1&exsentences=3&piprop=thumbnail&pithumbsize=300" +
                  "&ppprop=disambiguation%7Cwikibase_item&wbptterms=description&format=json&formatversion=2";

        var json = await GetJsonAsync(url, ct).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<WikiSearchResponse>(json, JsonOptions);

        if (parsed?.Error is not null)
        {
            PluginLog.Warn($"SearchAsync: API error for query=\"{query}\": {parsed.Error.Code} — {parsed.Error.Info}");
            throw new HttpRequestException(
                $"Wikipedia search returned an error: {parsed.Error.Code} — {parsed.Error.Info}");
        }

        var pages = parsed?.Query?.Pages ?? [];
        PluginLog.Info($"SearchAsync: query=\"{query}\" returned {pages.Count} candidate(s)");
        return pages;
    }

    // ── Detail (poster + categories + wikidata id) ──────────────────────────

    /// <summary>Returns null when the title doesn't exist upstream ("missing": true).</summary>
    public async Task<WikiDetailPage?> GetPageDetailsAsync(string title, CancellationToken ct)
    {
        var url = $"{ApiBase}?action=query&titles={Uri.EscapeDataString(title)}" +
                  "&prop=pageimages%7Cpageprops%7Ccategories&piprop=original%7Cthumbnail" +
                  "&pithumbsize=1000&ppprop=wikibase_item&cllimit=50&clshow=%21hidden" +
                  "&format=json&formatversion=2";

        var json = await GetJsonAsync(url, ct).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<WikiDetailResponse>(json, JsonOptions);

        if (parsed?.Error is not null)
        {
            PluginLog.Warn($"GetPageDetailsAsync: API error for title=\"{title}\": {parsed.Error.Code} — {parsed.Error.Info}");
            throw new HttpRequestException(
                $"Wikipedia detail lookup returned an error: {parsed.Error.Code} — {parsed.Error.Info}");
        }

        var page = parsed?.Query?.Pages?.FirstOrDefault();
        if (page is null || page.Missing)
        {
            PluginLog.Debug($"GetPageDetailsAsync: title=\"{title}\" missing upstream");
            return null;
        }

        PluginLog.Debug($"GetPageDetailsAsync: title=\"{title}\" -> pageid={page.PageId}, " +
                         $"hasImage={page.Original is not null || page.Thumbnail is not null}, " +
                         $"categories={page.Categories?.Count ?? 0}");
        return page;
    }

    // ── Redirect resolution ──────────────────────────────────────────────────

    /// <summary>Resolves a title through Wikipedia's redirect table. Returns null if the title
    /// is not a redirect (or doesn't exist) — the original title should be used as-is.</summary>
    public async Task<string?> ResolveRedirectAsync(string title, CancellationToken ct)
    {
        var url = $"{ApiBase}?action=query&redirects=1&titles={Uri.EscapeDataString(title)}" +
                  "&format=json&formatversion=2";

        var json = await GetJsonAsync(url, ct).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<WikiRedirectResponse>(json, JsonOptions);

        var resolved = parsed?.Query?.Pages?.FirstOrDefault()?.Title;
        if (string.IsNullOrWhiteSpace(resolved) || string.Equals(resolved, title, StringComparison.Ordinal))
        {
            PluginLog.Debug($"ResolveRedirectAsync: \"{title}\" is not a redirect");
            return null;
        }

        PluginLog.Info($"ResolveRedirectAsync: \"{title}\" -> \"{resolved}\"");
        return resolved;
    }

    // ── Full article HTML (Parsoid) ─────────────────────────────────────────

    /// <summary>Throws <see cref="HttpRequestException"/> with <see cref="HttpStatusCode.NotFound"/>
    /// when the title doesn't exist — callers should retry once via <see cref="ResolveRedirectAsync"/>.</summary>
    public async Task<string> GetArticleHtmlAsync(string title, CancellationToken ct)
    {
        var url = $"{RestBase}/page/html/{Uri.EscapeDataString(title)}";
        PluginLog.Debug($"GetArticleHtmlAsync: fetching \"{title}\"");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        var response = await SendWithRetryAsync(request, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            PluginLog.Warn($"GetArticleHtmlAsync: \"{title}\" -> HTTP {(int)response.StatusCode} {response.StatusCode}");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        PluginLog.Debug($"GetArticleHtmlAsync: \"{title}\" -> {html.Length} chars of HTML");
        return html;
    }

    // ── Health check ─────────────────────────────────────────────────────────

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            var page = await GetPageDetailsAsync("Wikipedia", ct).ConfigureAwait(false);
            var healthy = page is not null;
            PluginLog.Info($"PingAsync: {(healthy ? "healthy" : "unhealthy — reference page not found")}");
            return healthy;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            PluginLog.Warn($"PingAsync: unhealthy — {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // ── Image download (IMetadataProvider.GetImageAsync) ───────────────────

    public async Task<byte[]> GetImageBytesAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Host.EndsWith("wikimedia.org", StringComparison.OrdinalIgnoreCase))
        {
            PluginLog.Warn($"GetImageBytesAsync: refused untrusted host for '{url}'");
            throw new ArgumentException($"Refusing to download image from an untrusted host: '{url}'");
        }

        await _limiter.ThrottleAsync(ct).ConfigureAwait(false);
        var bytes = await _http.GetByteArrayAsync(uri, ct).ConfigureAwait(false);
        PluginLog.Debug($"GetImageBytesAsync: {url} -> {bytes.Length} bytes");
        return bytes;
    }

    // ── Shared GET + retry/backoff (429/503, honoring Retry-After) ──────────

    private async Task<string> GetJsonAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var response = await SendWithRetryAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a request, retrying up to 3 times on 429/503 with exponential backoff (2s, 4s, 8s,
    /// capped at 16s), honoring a Retry-After header when present. Kept lower than MusicBrainz's
    /// 4-retry budget because Chronicle's host-side ProviderCallGuard hard-kills any provider
    /// call at 25s regardless, and GetByIdAsync already makes two sequential requests before any
    /// retry logic runs — a longer retry budget risks losing the whole call to the host timeout
    /// instead of failing cleanly.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(HttpRequestMessage request, CancellationToken ct)
    {
        const int maxRetries = 3;
        var delay = TimeSpan.FromSeconds(2);

        for (var attempt = 0; ; attempt++)
        {
            await _limiter.ThrottleAsync(ct).ConfigureAwait(false);

            var toSend = attempt == 0 ? request : CloneRequest(request);
            var response = await _http.SendAsync(toSend, ct).ConfigureAwait(false);

            var isRetryable = response.StatusCode == HttpStatusCode.TooManyRequests
                            || response.StatusCode == HttpStatusCode.ServiceUnavailable;

            if (!isRetryable || attempt >= maxRetries)
            {
                if (attempt > 0)
                    PluginLog.Info($"SendWithRetryAsync: {request.RequestUri} succeeded on attempt {attempt + 1}");
                return response;
            }

            var wait = response.Headers.RetryAfter?.Delta ?? delay;
            var honoredRetryAfter = response.Headers.RetryAfter?.Delta is not null;
            PluginLog.Warn($"SendWithRetryAsync: {request.RequestUri} -> HTTP {(int)response.StatusCode} " +
                            $"(attempt {attempt + 1}/{maxRetries + 1}), waiting {wait.TotalSeconds:F1}s" +
                            (honoredRetryAfter ? " (honoring Retry-After)" : " (exponential backoff)"));
            response.Dispose();
            await Task.Delay(wait, ct).ConfigureAwait(false);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 16));
        }
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);
        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }

    public void Dispose() => _http.Dispose();
}
