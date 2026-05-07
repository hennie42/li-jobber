using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace LiCvWriter.Infrastructure.Research;

internal sealed record WebFetchResult(
    Uri RequestedUri,
    Uri FinalUri,
    string Content,
    string? MediaType);

internal static class PublicWebContentFetcher
{
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36";
    private const string HtmlAcceptHeader = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
    private const int MaxRedirectCount = 5;
    private const int MaxResponseBytes = 1_000_000;
    private static readonly Func<string, CancellationToken, Task<IPAddress[]>> DefaultHostAddressesResolver =
        static (host, cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken);
    private static readonly AsyncLocal<Func<string, CancellationToken, Task<IPAddress[]>>?> HostAddressesResolverOverride = new();

    internal static IDisposable PushHostAddressesResolverForTesting(Func<string, CancellationToken, Task<IPAddress[]>> hostAddressesResolver)
    {
        ArgumentNullException.ThrowIfNull(hostAddressesResolver);

        var previousResolver = HostAddressesResolverOverride.Value;
        HostAddressesResolverOverride.Value = hostAddressesResolver;
        return new HostAddressesResolverScope(previousResolver);
    }

    internal static async Task<WebFetchResult> FetchAsync(
        HttpClient httpClient,
        Uri sourceUrl,
        string acceptLanguageHeader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(sourceUrl);

        await ValidatePublicHttpsUriAsync(sourceUrl, cancellationToken);

        var currentUri = sourceUrl;

        for (var redirectCount = 0; redirectCount <= MaxRedirectCount; redirectCount++)
        {
            using var request = CreateHtmlRequest(currentUri, acceptLanguageHeader);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (TryResolveRedirectUri(currentUri, response, out var redirectedUri))
            {
                await ValidatePublicHttpsUriAsync(redirectedUri, cancellationToken);
                currentUri = redirectedUri;
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new InvalidOperationException(
                    $"The site blocked access to {currentUri} with 403 Forbidden. Some job and company pages reject automated requests even when the request looks like a browser.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var failureDetail = await ReadFailureDetailAsync(response, cancellationToken);
                throw new InvalidOperationException(
                    $"Fetching {currentUri} failed with {(int)response.StatusCode} {response.ReasonPhrase}.{failureDetail}");
            }

            var content = await ReadContentAsStringAsync(response.Content, currentUri, cancellationToken);
            return new WebFetchResult(sourceUrl, currentUri, content, response.Content.Headers.ContentType?.MediaType);
        }

        throw new InvalidOperationException(
            $"Fetching {sourceUrl} followed too many redirects. The final redirect target was not reached within {MaxRedirectCount} hops.");
    }

    internal static async Task ValidatePublicHttpsUriAsync(Uri sourceUrl, CancellationToken cancellationToken)
    {
        if (!sourceUrl.IsAbsoluteUri)
        {
            throw new InvalidOperationException($"Only absolute public HTTPS URLs are allowed: {sourceUrl}");
        }

        if (!string.Equals(sourceUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Only public HTTPS URLs are allowed: {sourceUrl}");
        }

        if (string.IsNullOrWhiteSpace(sourceUrl.Host))
        {
            throw new InvalidOperationException($"The URL host is invalid: {sourceUrl}");
        }

        if (sourceUrl.IsLoopback || IsLocalHostName(sourceUrl.Host))
        {
            throw new InvalidOperationException($"Local or private hosts are not allowed: {sourceUrl}");
        }

        if (IPAddress.TryParse(sourceUrl.Host, out var parsedAddress))
        {
            if (IsPrivateAddress(parsedAddress))
            {
                throw new InvalidOperationException($"Local or private hosts are not allowed: {sourceUrl}");
            }

            return;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await ResolveHostAddressesAsync(sourceUrl.DnsSafeHost, cancellationToken);
        }
        catch (SocketException)
        {
            throw new InvalidOperationException($"The URL host could not be resolved: {sourceUrl}");
        }

        if (addresses.Any(IsPrivateAddress))
        {
            throw new InvalidOperationException($"Local or private hosts are not allowed: {sourceUrl}");
        }
    }

    internal static void EnsureHtmlLikeResponse(Uri sourceUrl, string? mediaType, string content)
    {
        ArgumentNullException.ThrowIfNull(sourceUrl);
        ArgumentNullException.ThrowIfNull(content);

        if (IsSupportedHtmlMediaType(mediaType) || LooksLikeHtml(content))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Fetching {sourceUrl} returned unsupported content type '{mediaType ?? "unknown"}'. Only HTML pages are supported for this operation.");
    }

    private static Task<IPAddress[]> ResolveHostAddressesAsync(string host, CancellationToken cancellationToken)
        => (HostAddressesResolverOverride.Value ?? DefaultHostAddressesResolver)(host, cancellationToken);

    private static bool TryResolveRedirectUri(Uri currentUri, HttpResponseMessage response, out Uri redirectedUri)
    {
        redirectedUri = null!;

        if (!IsRedirectStatusCode(response.StatusCode))
        {
            return false;
        }

        var location = response.Headers.Location;
        if (location is null)
        {
            throw new InvalidOperationException(
                $"Fetching {currentUri} returned {(int)response.StatusCode} {response.ReasonPhrase} without a redirect target.");
        }

        redirectedUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
        return true;
    }

    private static bool IsRedirectStatusCode(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.Moved
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static async Task<string> ReadContentAsStringAsync(HttpContent content, Uri sourceUrl, CancellationToken cancellationToken)
    {
        var contentLength = content.Headers.ContentLength;
        if (contentLength is > MaxResponseBytes)
        {
            throw new InvalidOperationException(
                $"Fetching {sourceUrl} exceeded the maximum supported response size of {MaxResponseBytes} bytes.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = contentLength is > 0 and <= MaxResponseBytes
            ? new MemoryStream((int)contentLength.Value)
            : new MemoryStream();

        var chunk = new byte[81_920];
        var totalBytesRead = 0;

        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            totalBytesRead += read;
            if (totalBytesRead > MaxResponseBytes)
            {
                throw new InvalidOperationException(
                    $"Fetching {sourceUrl} exceeded the maximum supported response size of {MaxResponseBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        var encoding = ResolveEncoding(content.Headers.ContentType?.CharSet);
        return encoding.GetString(buffer.ToArray());
    }

    private static Encoding ResolveEncoding(string? charset)
    {
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                return Encoding.GetEncoding(charset);
            }
            catch (ArgumentException)
            {
                // Fall back to UTF-8 when the server declares an unknown charset.
            }
        }

        return Encoding.UTF8;
    }

    private static HttpRequestMessage CreateHtmlRequest(Uri sourceUrl, string acceptLanguageHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, sourceUrl);
        request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
        request.Headers.Accept.ParseAdd(HtmlAcceptHeader);
        request.Headers.AcceptLanguage.ParseAdd(acceptLanguageHeader);
        request.Headers.AcceptEncoding.ParseAdd("gzip");
        request.Headers.AcceptEncoding.ParseAdd("deflate");
        request.Headers.AcceptEncoding.ParseAdd("br");
        request.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        return request;
    }

    private static async Task<string> ReadFailureDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await ReadContentAsStringAsync(response.Content, response.RequestMessage?.RequestUri ?? new Uri("https://invalid.local"), cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        var trimmed = Regex.Replace(body, @"\s+", " ").Trim();
        if (trimmed.Length > 240)
        {
            trimmed = trimmed[..240].TrimEnd() + "...";
        }

        return $" Response excerpt: {trimmed}";
    }

    private static bool IsSupportedHtmlMediaType(string? mediaType)
        => string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeHtml(string content)
    {
        var trimmed = content.TrimStart();
        return trimmed.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<body", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<main", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<article", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<section", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<div", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocalHostName(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase);

    private sealed class HostAddressesResolverScope(Func<string, CancellationToken, Task<IPAddress[]>>? previousResolver) : IDisposable
    {
        public void Dispose()
        {
            HostAddressesResolverOverride.Value = previousResolver;
        }
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast
                || address.Equals(IPAddress.IPv6Loopback)
                || address.IsIPv6UniqueLocal;
        }

        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return true;
        }

        return bytes[0] switch
        {
            0 => true,
            10 => true,
            127 => true,
            169 when bytes[1] == 254 => true,
            172 when bytes[1] >= 16 && bytes[1] <= 31 => true,
            192 when bytes[1] == 168 => true,
            _ => false
        };
    }
}