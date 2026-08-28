using System.Text.Json.Serialization;

namespace Chronicle.Plugin.Wikipedia.Models;

// ── Combined search response (action=query&generator=search&prop=extracts|pageimages|pageprops|pageterms) ──

internal sealed record WikiSearchResponse(
    [property: JsonPropertyName("query")] WikiSearchQuery? Query,
    [property: JsonPropertyName("error")] WikiApiError? Error);

internal sealed record WikiSearchQuery(
    [property: JsonPropertyName("pages")] List<WikiSearchPage>? Pages);

internal sealed record WikiSearchPage(
    [property: JsonPropertyName("pageid")] long PageId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("extract")] string? Extract,
    [property: JsonPropertyName("thumbnail")] WikiImageRef? Thumbnail,
    [property: JsonPropertyName("pageprops")] WikiPageProps? PageProps,
    [property: JsonPropertyName("terms")] WikiTerms? Terms);

internal sealed record WikiImageRef(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);

internal sealed record WikiPageProps(
    [property: JsonPropertyName("disambiguation")] string? Disambiguation,
    [property: JsonPropertyName("wikibase_item")] string? WikibaseItem);

internal sealed record WikiTerms(
    [property: JsonPropertyName("description")] List<string>? Description);

internal sealed record WikiApiError(
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("info")] string? Info);

// ── Detail response (action=query&titles=&prop=pageimages|pageprops|categories) ──

internal sealed record WikiDetailResponse(
    [property: JsonPropertyName("query")] WikiDetailQuery? Query,
    [property: JsonPropertyName("error")] WikiApiError? Error);

internal sealed record WikiDetailQuery(
    [property: JsonPropertyName("pages")] List<WikiDetailPage>? Pages);

internal sealed record WikiDetailPage(
    [property: JsonPropertyName("pageid")] long PageId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("missing")] bool Missing,
    [property: JsonPropertyName("original")] WikiImageRef? Original,
    [property: JsonPropertyName("thumbnail")] WikiImageRef? Thumbnail,
    [property: JsonPropertyName("pageprops")] WikiPageProps? PageProps,
    [property: JsonPropertyName("categories")] List<WikiCategory>? Categories);

internal sealed record WikiCategory(
    [property: JsonPropertyName("ns")] int Ns,
    [property: JsonPropertyName("title")] string Title);

// ── Redirect resolution (action=query&redirects=1&titles=) ──

internal sealed record WikiRedirectResponse(
    [property: JsonPropertyName("query")] WikiRedirectQuery? Query);

internal sealed record WikiRedirectQuery(
    [property: JsonPropertyName("redirects")] List<WikiRedirect>? Redirects,
    [property: JsonPropertyName("pages")] List<WikiDetailPage>? Pages);

internal sealed record WikiRedirect(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To);
