using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;

namespace LiCvWriter.Application.Services;

public sealed class JobDiscoverySearchPlanService(JobDiscoveryOptions options)
{
    public IReadOnlyList<JobDiscoveryProviderOptions> GetProviders()
        => options.Providers
            .Where(static provider => !string.IsNullOrWhiteSpace(provider.Id) && !string.IsNullOrWhiteSpace(provider.BaseUrl))
            .ToArray();

    public JobDiscoveryProviderOptions? ResolveProvider(string? providerId)
    {
        var providers = GetProviders();
        if (providers.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(providerId))
        {
            var explicitProvider = providers.FirstOrDefault(provider => provider.Id.Equals(providerId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (explicitProvider is not null)
            {
                return explicitProvider;
            }
        }

        if (!string.IsNullOrWhiteSpace(options.DefaultProviderId))
        {
            var defaultProvider = providers.FirstOrDefault(provider => provider.Id.Equals(options.DefaultProviderId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (defaultProvider is not null)
            {
                return defaultProvider;
            }
        }

        return providers[0];
    }

    public JobDiscoverySearchPlan Build(
        JobDiscoveryProfileLight profileLight,
        string? providerId = null,
        string? queryOverride = null,
        string? locationOverride = null)
    {
        if (!options.Enabled)
        {
            return JobDiscoverySearchPlan.Empty;
        }

        var provider = ResolveProvider(providerId);
        if (provider is null)
        {
            return JobDiscoverySearchPlan.Empty;
        }

        var query = Normalize(queryOverride) ?? Normalize(profileLight.SearchQuery) ?? string.Empty;
        var location = Normalize(locationOverride) ?? Normalize(profileLight.PreferredLocation) ?? string.Empty;

        return new JobDiscoverySearchPlan(
            provider.Id,
            string.IsNullOrWhiteSpace(provider.DisplayName) ? provider.Id : provider.DisplayName,
            query,
            location,
            BuildSearchUri(provider, query, location));
    }

    private Uri? BuildSearchUri(JobDiscoveryProviderOptions provider, string query, string location)
    {
        if (string.IsNullOrWhiteSpace(provider.BaseUrl)
            || string.IsNullOrWhiteSpace(provider.SearchPath)
            || string.IsNullOrWhiteSpace(provider.QueryParameterName)
            || string.IsNullOrWhiteSpace(query)
            || !Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        if (options.PublicHttpsOnly && !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (provider.AllowedHosts.Length > 0
            && !provider.AllowedHosts.Contains(baseUri.Host, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var builder = new UriBuilder(baseUri)
        {
            Path = CombinePath(baseUri.AbsolutePath, provider.SearchPath),
            Query = BuildQuery(provider, query, location)
        };

        return builder.Uri;
    }

    private static string BuildQuery(JobDiscoveryProviderOptions provider, string query, string location)
    {
        var pairs = new List<string>
        {
            $"{Uri.EscapeDataString(provider.QueryParameterName)}={Uri.EscapeDataString(query)}"
        };

        if (!string.IsNullOrWhiteSpace(location) && !string.IsNullOrWhiteSpace(provider.LocationParameterName))
        {
            pairs.Add($"{Uri.EscapeDataString(provider.LocationParameterName)}={Uri.EscapeDataString(location)}");
        }

        return string.Join("&", pairs);
    }

    private static string CombinePath(string basePath, string relativePath)
    {
        var normalizedBase = string.IsNullOrWhiteSpace(basePath) || basePath == "/"
            ? string.Empty
            : basePath.TrimEnd('/');
        var normalizedRelative = relativePath.TrimStart('/');

        return string.IsNullOrWhiteSpace(normalizedBase)
            ? $"/{normalizedRelative}"
            : $"{normalizedBase}/{normalizedRelative}";
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}