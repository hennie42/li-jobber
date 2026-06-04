using System.Net;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Infrastructure.Research;

namespace LiCvWriter.Web.Services;

/// <summary>
/// Registers outbound HTTP clients for public job research and discovery services.
/// </summary>
internal static class JobHttpServiceCollectionExtensions
{
    public static IServiceCollection AddLiCvWriterJobHttpServices(this IServiceCollection services)
    {
        services.AddHttpClient<IJobResearchService, HttpJobResearchService>(client =>
        {
            client.Timeout = TimeSpan.FromMinutes(1);
        })
            .ConfigurePrimaryHttpMessageHandler(CreateNonRedirectingDecompressionHandler);

        services.AddHttpClient<IJobDiscoveryService, HttpJobDiscoveryService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        })
            .ConfigurePrimaryHttpMessageHandler(CreateNonRedirectingDecompressionHandler);

        return services;
    }

    private static HttpClientHandler CreateNonRedirectingDecompressionHandler()
        => new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
}