using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;

namespace LiCvWriter.Infrastructure.Research;

public sealed class HttpJobDiscoveryService(HttpClient httpClient, JobDiscoveryOptions options) : IJobDiscoveryService
{
    private const string AcceptLanguageHeader = "da-DK,da;q=0.9,en-US;q=0.8,en;q=0.7";
    private static readonly Regex EmbeddedHtmlPattern = new("\"html\"\\s*:\\s*\"((?:\\\\.|[^\"])*)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<IReadOnlyList<JobDiscoverySuggestion>> DiscoverAsync(
        JobDiscoverySearchPlan searchPlan,
        Action<JobDiscoveryProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled || !searchPlan.CanOpen || searchPlan.SearchUri is null)
        {
            return Array.Empty<JobDiscoverySuggestion>();
        }

        if (!string.Equals(searchPlan.ProviderId, "jobindex", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The discovery provider '{searchPlan.ProviderId}' is not implemented yet.");
        }

        progress?.Invoke(new JobDiscoveryProgressUpdate(
            "Fetching Jobindex search page",
            $"Loading {searchPlan.SearchUri.AbsoluteUri}"));

        var fetchResult = await PublicWebContentFetcher.FetchAsync(httpClient, searchPlan.SearchUri, AcceptLanguageHeader, cancellationToken);
        var html = fetchResult.Content;
        if (string.IsNullOrWhiteSpace(html))
        {
            return Array.Empty<JobDiscoverySuggestion>();
        }

        progress?.Invoke(new JobDiscoveryProgressUpdate(
            "Parsing Jobindex result cards",
            "Extracting result cards from the Jobindex search response."));

        var suggestions = ParseJobindexResults(searchPlan, fetchResult.FinalUri, html);
        progress?.Invoke(new JobDiscoveryProgressUpdate(
            "Jobindex suggestions ready",
            $"Loaded {suggestions.Count} suggestion(s) from the current search response."));

        return suggestions;
    }

    private IReadOnlyList<JobDiscoverySuggestion> ParseJobindexResults(JobDiscoverySearchPlan searchPlan, Uri searchResultBaseUri, string html)
    {
        var resultNodes = LoadResultNodes(html);
        var suggestions = new List<JobDiscoverySuggestion>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var resultNode in resultNodes)
        {
            var suggestion = TryParseResult(resultNode, searchPlan, searchResultBaseUri);
            if (suggestion is null)
            {
                continue;
            }

            if (!seenUrls.Add(suggestion.DetailUrl.AbsoluteUri))
            {
                continue;
            }

            suggestions.Add(suggestion);

            if (suggestions.Count >= options.ShortlistLimit)
            {
                break;
            }
        }

        return suggestions;
    }

    private static HtmlNode[] LoadResultNodes(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);

        var resultNodes = document.DocumentNode.SelectNodes("//div[contains(concat(' ', normalize-space(@class), ' '), ' jobsearch-result ')]")?.ToArray()
            ?? [];
        if (resultNodes.Length > 0)
        {
            return resultNodes;
        }

        return ExtractEmbeddedResultNodes(html);
    }

    private static HtmlNode[] ExtractEmbeddedResultNodes(string html)
        => EmbeddedHtmlPattern.Matches(html)
            .Select(match => TryDecodeEmbeddedHtml(match.Groups[1].Value))
            .Where(fragment => !string.IsNullOrWhiteSpace(fragment))
            .SelectMany(fragment =>
            {
                var fragmentDocument = new HtmlDocument();
                fragmentDocument.LoadHtml(fragment!);
                return fragmentDocument.DocumentNode.SelectNodes("//div[contains(concat(' ', normalize-space(@class), ' '), ' jobsearch-result ')]")?.ToArray()
                    ?? [];
            })
            .ToArray();

    private static string? TryDecodeEmbeddedHtml(string encodedFragment)
    {
        if (string.IsNullOrWhiteSpace(encodedFragment))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<string>($"\"{encodedFragment}\"");
        }
        catch (JsonException)
        {
            return Regex.Unescape(encodedFragment);
        }
    }

    private JobDiscoverySuggestion? TryParseResult(HtmlNode resultNode, JobDiscoverySearchPlan searchPlan, Uri searchResultBaseUri)
    {
        var paidInner = resultNode.SelectSingleNode(".//div[contains(concat(' ', normalize-space(@class), ' '), ' PaidJob-inner ')]");
        if (paidInner is null)
        {
            return null;
        }

        var titleLink = paidInner.SelectSingleNode(".//h4//a[@href]");
        if (titleLink is null)
        {
            return null;
        }

        var title = NormalizeText(titleLink.InnerText);
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var detailUri = ResolveDetailUri(searchResultBaseUri, titleLink.GetAttributeValue("href", string.Empty), resultNode, paidInner);
        if (detailUri is null)
        {
            return null;
        }

        var companyName = FirstNonEmpty(
            NormalizeText(paidInner.SelectSingleNode(".//img[@alt][1]")?.GetAttributeValue("alt", string.Empty)),
            NormalizeText(resultNode.SelectSingleNode(".//div[contains(concat(' ', normalize-space(@class), ' '), ' jix-toolbar-top__company ')]//a[1]")?.InnerText),
            NormalizeText(resultNode.SelectSingleNode(".//a[@data-share-title]")?.GetAttributeValue("data-share-title", string.Empty)));
        var location = NormalizeText(paidInner.SelectSingleNode(".//div[contains(concat(' ', normalize-space(@class), ' '), ' jobad-element-area ')]//span[contains(concat(' ', normalize-space(@class), ' '), ' jix_robotjob--area ')]")?.InnerText)
            ?? string.Empty;
        var summary = BuildSummary(paidInner);
        var postedLabel = NormalizeText(resultNode.SelectSingleNode(".//div[contains(concat(' ', normalize-space(@class), ' '), ' jix-toolbar__pubdate ')]//time[1]")?.InnerText)
            ?? string.Empty;

        return new JobDiscoverySuggestion(
            searchPlan.ProviderId,
            searchPlan.ProviderDisplayName,
            title,
            companyName ?? string.Empty,
            location,
            summary,
            detailUri,
            postedLabel,
            searchPlan.SearchUri!);
    }

    private Uri? ResolveDetailUri(Uri searchUri, string? href, HtmlNode resultNode, HtmlNode paidInner)
    {
        var primary = TryResolveUri(searchUri, href);
        if (IsAcceptableDetailUri(primary))
        {
            return primary;
        }

        var fallbackHref = paidInner.SelectSingleNode(".//a[contains(concat(' ', normalize-space(@class), ' '), ' seejobdesktop ') or contains(concat(' ', normalize-space(@class), ' '), ' seejobmobil ')]")?.GetAttributeValue("href", string.Empty)
            ?? resultNode.SelectSingleNode(".//a[contains(concat(' ', normalize-space(@class), ' '), ' seejobdesktop ') or contains(concat(' ', normalize-space(@class), ' '), ' seejobmobil ')]")?.GetAttributeValue("href", string.Empty);
        var fallback = TryResolveUri(searchUri, fallbackHref);
        return IsAcceptableDetailUri(fallback) ? fallback : null;
    }

    private bool IsAcceptableDetailUri(Uri? uri)
        => uri is not null
            && (!options.PublicHttpsOnly || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    private static Uri? TryResolveUri(Uri baseUri, string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        var decoded = HtmlEntity.DeEntitize(href.Trim());
        if (!Uri.TryCreate(baseUri, decoded, out var resolved))
        {
            return null;
        }

        return resolved;
    }

    private static string BuildSummary(HtmlNode paidInner)
    {
        var paragraphs = paidInner.SelectNodes("./p")?.ToArray()
            ?? paidInner.SelectNodes(".//p")?.ToArray()
            ?? [];

        return string.Join(
            " ",
            paragraphs
                .Select(paragraph => NormalizeText(paragraph.InnerText))
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Where(text => text!.Length >= 20)
                .Where(text => !text!.StartsWith("Se video", StringComparison.OrdinalIgnoreCase))
                .Take(2)!);
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var decoded = HtmlEntity.DeEntitize(value);
        return string.Join(" ", decoded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}