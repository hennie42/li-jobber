namespace LiCvWriter.Application.Models;

public sealed record JobDiscoverySuggestion(
    string ProviderId,
    string ProviderDisplayName,
    string Title,
    string CompanyName,
    string Location,
    string Summary,
    Uri DetailUrl,
    string PostedLabel,
    Uri SearchUrl)
{
    public string DisplayCompany => string.IsNullOrWhiteSpace(CompanyName) ? "Unknown company" : CompanyName;

    public bool HasPostedLabel => !string.IsNullOrWhiteSpace(PostedLabel);
}