using System.Net;
using Chronicle.Plugins.Models;
using Xunit;

namespace Chronicle.Plugin.Wikipedia.Tests;

public class WikipediaMetadataProviderTests
{
    /// <summary>
    /// Regression test for a real production bug: SearchAsync passed Wikipedia's raw,
    /// disambiguated page title ("The Batman (film)") straight through as the candidate's
    /// display Title, which then flowed into Chronicle's own dedup/stub-creation logic as a
    /// literal string -- creating a second "The Batman (film)" MediaItem alongside the
    /// existing "The Batman" one instead of matching it. ExternalId must keep the
    /// disambiguator (it's part of the article's real identity on Wikipedia); only Title
    /// gets it stripped.
    /// </summary>
    [Fact]
    public async Task SearchAsync_CandidateTitleHasDisambiguationSuffix_StripsItFromDisplayTitleNotExternalId()
    {
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"query":{"pages":[{"pageid":1,"title":"The Batman (film)","index":1,"extract":"2022 superhero film."}]}}"""),
            });

        var client = new WikipediaClient(new HttpClient(handler), "en", minRequestIntervalMs: 1);
        var provider = new WikipediaMetadataProvider(client, "en", maxImages: 20);
        var context = new MediaSearchContext(Name: "The Batman", MediaTypeName: "movies");

        var results = await provider.SearchAsync(context, CancellationToken.None);

        var candidate = Assert.Single(results);
        Assert.Equal("The Batman", candidate.Metadata.Title);
        Assert.Equal("wikipedia:en:The_Batman_(film)", candidate.Metadata.ExternalId);
    }


    /// <summary>
    /// Regression test for a review-caught bug: after GetArticleHtmlAsync 404s on the
    /// requested title and resolves through a redirect, the SUBSEQUENT detail lookup
    /// (poster/categories/wikidata) must use the RESOLVED title, not the stale original —
    /// otherwise the detail call misses (wrong/missing page) on every redirected article.
    /// </summary>
    [Fact]
    public async Task GetByIdAsync_TitleRequiresRedirect_DetailLookupUsesResolvedTitleNotOriginal()
    {
        var detailRequestUris = new List<string>();

        // Exact original-title html URL, computed the same way the client itself builds it —
        // avoids needing to predict Uri.EscapeDataString's exact percent-encoding of the
        // redirect TARGET title (which contains a space and parentheses) in the match below.
        var originalHtmlUrl = $"https://en.wikipedia.org/api/rest_v1/page/html/{Uri.EscapeDataString("The_Batman")}";

        var handler = new StubHandler(request =>
        {
            var uri = request.RequestUri!.ToString();

            // 1) REST html for the exact original title -> 404, triggering redirect resolution.
            if (uri == originalHtmlUrl)
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            // 2) action=query&redirects=1&titles=The_Batman -> resolves to "The Batman (film)".
            if (uri.Contains("redirects=1"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"query":{"redirects":[{"from":"The_Batman","to":"The Batman (film)"}],"pages":[{"pageid":1,"title":"The Batman (film)"}]}}"""),
                };

            // 3) REST html for anything else under /page/html/ — the resolved title's request —
            // succeeds. Not string-matched to the exact resolved URL: this is a fallback,
            // reached only because (1) already ruled out the one URL this test expects to 404.
            if (uri.Contains("/page/html/"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """<html><body><section data-mw-section-id="0"><p>The Batman is a 2022 film.</p></section></body></html>"""),
                };

            // 4) action=query&titles=...&prop=pageimages... (the detail/poster/categories lookup).
            if (uri.Contains("prop=pageimages"))
            {
                detailRequestUris.Add(uri);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"query":{"pages":[{"pageid":1,"title":"The Batman (film)","original":{"source":"https://upload.wikimedia.org/x/poster.jpg","width":1000,"height":1000}}]}}"""),
                };
            }

            throw new InvalidOperationException($"Unexpected request: {uri}");
        });

        var client = new WikipediaClient(new HttpClient(handler), "en", minRequestIntervalMs: 1);
        var provider = new WikipediaMetadataProvider(client, "en", maxImages: 20);

        var result = await provider.GetByIdAsync("wikipedia:en:The_Batman", CancellationToken.None);

        // The detail lookup must have been issued for the RESOLVED title, not "The_Batman".
        Assert.Single(detailRequestUris);
        Assert.Contains("Batman", detailRequestUris[0]);
        Assert.DoesNotContain("titles=The_Batman&", detailRequestUris[0]);

        // And the result should actually carry the poster the detail call returned — proving
        // the detail lookup wasn't silently skipped/empty.
        Assert.Equal("https://upload.wikimedia.org/x/poster.jpg", result.PosterUrl);
        // Display Title has the disambiguator stripped; ExternalId keeps it (it's part of the
        // article's actual identity on Wikipedia -- see WikipediaScoring.StripDisambiguationSuffix).
        Assert.Equal("The Batman", result.Title);
        Assert.Equal("wikipedia:en:The_Batman_(film)", result.ExternalId);
    }
}
// StubHandler is defined once, in WikipediaClientTests.cs, and reused here (same namespace).
