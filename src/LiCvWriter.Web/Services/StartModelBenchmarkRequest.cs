using LiCvWriter.Application.Models;

namespace LiCvWriter.Web.Services;

/// <summary>
/// Describes a benchmark batch request for the local model benchmark coordinator.
/// </summary>
public sealed record StartModelBenchmarkRequest(
    LlmProviderKind Provider,
    IReadOnlyList<string> Models,
    bool DownloadMissingModels = false,
    bool RemoveTooLargeModelsAfterBenchmark = false);