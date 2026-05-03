namespace LiCvWriter.Application.Models;

public sealed record JobDiscoverySearchPlan(
    string ProviderId,
    string ProviderDisplayName,
    string Query,
    string PreferredLocation,
    Uri? SearchUri)
{
    public static JobDiscoverySearchPlan Empty { get; } = new(
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        null);

    public bool CanOpen => SearchUri is not null;
}