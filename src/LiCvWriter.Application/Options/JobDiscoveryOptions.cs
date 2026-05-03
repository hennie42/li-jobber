namespace LiCvWriter.Application.Options;

public sealed class JobDiscoveryOptions
{
    public const string SectionName = "JobDiscovery";

    public bool Enabled { get; set; } = true;

    public string DefaultProviderId { get; set; } = "jobindex";

    public bool PublicHttpsOnly { get; set; } = true;

    public int MaxResultPages { get; set; } = 2;

    public int ShortlistLimit { get; set; } = 12;

    public int RequestDelayMilliseconds { get; set; } = 500;

    public List<JobDiscoveryProviderOptions> Providers { get; set; } =
    [
        new JobDiscoveryProviderOptions
        {
            Id = "jobindex",
            DisplayName = "Jobindex",
            BaseUrl = "https://www.jobindex.dk",
            SearchPath = "/jobsoegning",
            QueryParameterName = "q",
            LocationParameterName = string.Empty,
            AllowedHosts = ["jobindex.dk", "www.jobindex.dk"]
        }
    ];
}

public sealed class JobDiscoveryProviderOptions
{
    public string Id { get; set; } = "jobindex";

    public string DisplayName { get; set; } = "Jobindex";

    public string BaseUrl { get; set; } = "https://www.jobindex.dk";

    public string SearchPath { get; set; } = "/jobsoegning";

    public string QueryParameterName { get; set; } = "q";

    public string LocationParameterName { get; set; } = string.Empty;

    public string[] AllowedHosts { get; set; } = ["jobindex.dk", "www.jobindex.dk"];
}