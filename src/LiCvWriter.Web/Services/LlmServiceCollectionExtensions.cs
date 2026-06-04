using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Options;
using LiCvWriter.Application.Services;
using LiCvWriter.Infrastructure.Llm;

namespace LiCvWriter.Web.Services;

/// <summary>
/// Registers local LLM provider services used by workspace drafting, setup, and benchmarking flows.
/// </summary>
internal static class LlmServiceCollectionExtensions
{
    public static IServiceCollection AddLiCvWriterLlmServices(this IServiceCollection services, OllamaOptions ollamaOptions)
    {
        services.AddHttpClient<OllamaClient>(client =>
        {
            client.BaseAddress = NormalizeApiBase(ollamaOptions.BaseUrl);
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        services.AddSingleton<FoundryLocalManagerAccessor>();
        services.AddSingleton<DefaultFoundrySdkBridge>();
        services.AddSingleton<IFoundrySdkBridge>(provider =>
            FoundrySdkBridgeLoader.Create(
                provider.GetRequiredService<FoundryOptions>(),
                provider.GetRequiredService<ILoggerFactory>(),
                provider.GetRequiredService<DefaultFoundrySdkBridge>()));
        services.AddSingleton<FoundryCatalogClient>();
        services.AddSingleton<PlaywrightFoundryCatalogClient>();
        services.AddSingleton<IFoundryCatalogClient>(provider =>
            provider.GetRequiredService<PlaywrightFoundryCatalogClient>());
        services.AddScoped<FoundryLlmClient>();
        services.AddScoped<WorkspaceLlmClient>();
        services.AddScoped<PromptCapturingLlmClient>(provider =>
            new PromptCapturingLlmClient(provider.GetRequiredService<WorkspaceLlmClient>()));
        services.AddScoped<ILlmClient>(provider =>
            provider.GetRequiredService<PromptCapturingLlmClient>());
        services.AddScoped<OllamaCapacityProbe>();
        services.AddScoped<OllamaModelBenchmarkService>();
        services.AddSingleton<FoundryBenchmarkLifecycleService>();
        services.AddSingleton<ModelBenchmarkHangMonitor>();
        services.AddSingleton<ModelBenchmarkCoordinator>();

        return services;
    }

    private static Uri NormalizeApiBase(string baseUrl)
    {
        var normalized = string.IsNullOrWhiteSpace(baseUrl) ? "http://localhost:11434/api/" : baseUrl.Trim();
        if (!normalized.EndsWith('/'))
        {
            normalized += "/";
        }

        return new Uri(normalized, UriKind.Absolute);
    }
}