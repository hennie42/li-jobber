using LiCvWriter.Application.Models;

namespace LiCvWriter.Application.Abstractions;

public interface IJobDiscoveryService
{
    Task<IReadOnlyList<JobDiscoverySuggestion>> DiscoverAsync(
        JobDiscoverySearchPlan searchPlan,
        CancellationToken cancellationToken = default);
}