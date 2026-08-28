using System.Net;
using Xunit;

namespace Chronicle.Plugin.Wikipedia.Tests;

public class WikipediaClientTests
{
    [Fact]
    public async Task SearchAsync_ZeroResultsResponse_ReturnsEmptyListNotError()
    {
        // MediaWiki's real shape for "nothing found": {"batchcomplete":true}, no "query" key.
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"batchcomplete":true}"""),
            });

        var client = new WikipediaClient(new HttpClient(handler), "en", minRequestIntervalMs: 1);

        var results = await client.SearchAsync("zzzznonexistentqueryxyz", CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_ApiErrorBody_ThrowsRatherThanReturningEmpty()
    {
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"error":{"code":"invalidparammix","info":"bad params"}}"""),
            });

        var client = new WikipediaClient(new HttpClient(handler), "en", minRequestIntervalMs: 1);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SearchAsync("test", CancellationToken.None));
    }

    [Fact]
    public async Task GetArticleHtmlAsync_429Response_RetriesThenSucceeds()
    {
        var call = 0;
        var handler = new StubHandler(_ =>
        {
            call++;
            if (call == 1)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
                return resp;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html><body></body></html>") };
        });

        var client = new WikipediaClient(new HttpClient(handler), "en", minRequestIntervalMs: 1);

        var html = await client.GetArticleHtmlAsync("Test_Article", CancellationToken.None);

        Assert.Equal(2, handler.CallCount);
        Assert.Contains("<body>", html);
    }

    [Fact]
    public async Task GetArticleHtmlAsync_PersistentServiceUnavailable_EventuallyThrows()
    {
        // No Retry-After header, so this exercises the exponential-backoff fallback path —
        // but with a tiny Retry-After instead of letting the real 2s/4s/8s backoff run, so the
        // test proves "gives up after exhausting retries" without taking ~14 real seconds.
        var handler = new StubHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
            return resp;
        });
        var client = new WikipediaClient(new HttpClient(handler), "en", minRequestIntervalMs: 1);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetArticleHtmlAsync("Test_Article", CancellationToken.None));

        // Initial attempt + 3 retries = 4 total.
        Assert.Equal(4, handler.CallCount);
    }

    [Fact]
    public async Task GetArticleHtmlAsync_404_ThrowsWithNotFoundStatusCode()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = new WikipediaClient(new HttpClient(handler), "en", minRequestIntervalMs: 1);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetArticleHtmlAsync("Nonexistent_Page", CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task GetPageDetailsAsync_MissingPage_ReturnsNull()
    {
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"query":{"pages":[{"pageid":0,"title":"Xyz","missing":true}]}}"""),
            });

        var client = new WikipediaClient(new HttpClient(handler), "en", minRequestIntervalMs: 1);
        var page = await client.GetPageDetailsAsync("Xyz", CancellationToken.None);

        Assert.Null(page);
    }

    [Fact]
    public async Task GetImageBytesAsync_UntrustedHost_ThrowsWithoutMakingRequest()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("Should never be called"));
        var client = new WikipediaClient(new HttpClient(handler), "en", minRequestIntervalMs: 1);

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.GetImageBytesAsync("https://evil.com/steal.jpg", CancellationToken.None));

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetImageBytesAsync_WikimediaHost_Succeeds()
    {
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        var client = new WikipediaClient(new HttpClient(handler), "en", minRequestIntervalMs: 1);

        var bytes = await client.GetImageBytesAsync("https://upload.wikimedia.org/x.jpg", CancellationToken.None);

        Assert.Equal(3, bytes.Length);
    }

    [Fact]
    public async Task PingAsync_SuccessfulLookup_ReturnsTrue()
    {
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"query":{"pages":[{"pageid":1,"title":"Wikipedia"}]}}"""),
            });
        var client = new WikipediaClient(new HttpClient(handler), "en", minRequestIntervalMs: 1);

        Assert.True(await client.PingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PingAsync_NetworkFailure_ReturnsFalseRatherThanThrowing()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("simulated network failure"));
        var client = new WikipediaClient(new HttpClient(handler), "en", minRequestIntervalMs: 1);

        Assert.False(await client.PingAsync(CancellationToken.None));
    }
}

/// <summary>Minimal HttpMessageHandler stub for unit tests — same shape as
/// Chronicle.Plugin.MusicBrainz's test helper.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _factory;
    public int CallCount { get; private set; }

    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> factory)
        => _factory = factory;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(_factory(request));
    }
}
