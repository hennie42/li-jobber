using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Application.Services;

namespace LiCvWriter.Tests.Application;

public sealed class JobDiscoverySearchPlanServiceTests
{
    [Fact]
    public void Build_UsesConfiguredProviderAndEncodesTheQuery()
    {
        var options = new JobDiscoveryOptions
        {
            Providers =
            [
                new JobDiscoveryProviderOptions
                {
                    Id = "jobindex",
                    DisplayName = "Jobindex",
                    BaseUrl = "https://jobindex.dk",
                    SearchPath = "/jobsoegning/it/systemudvikling/storkoebenhavn",
                    QueryParameterName = "q",
                    AllowedHosts = ["jobindex.dk", "www.jobindex.dk"]
                }
            ]
        };

        var service = new JobDiscoverySearchPlanService(options);
        var profileLight = new JobDiscoveryProfileLight(
            "Lead Architect",
            "Lead architect for Azure delivery",
            "Copenhagen",
            "Consulting",
            ["Lead Architect"],
            ["Azure", "Kubernetes"],
            "Pragmatic AI architect",
            "Lead Architect Azure Kubernetes");

        var result = service.Build(profileLight);

        Assert.True(result.CanOpen);
        Assert.Equal("jobindex", result.ProviderId);
        Assert.Equal("Jobindex", result.ProviderDisplayName);
        Assert.Equal("Lead Architect Azure Kubernetes", result.Query);
        Assert.Equal("https://jobindex.dk/jobsoegning/it/systemudvikling/storkoebenhavn?q=Lead%20Architect%20Azure%20Kubernetes", result.SearchUri!.AbsoluteUri);
    }

    [Fact]
    public void Build_OmitsLocationWhenProviderDoesNotDefineLocationParameter()
    {
        var options = new JobDiscoveryOptions
        {
            Providers =
            [
                new JobDiscoveryProviderOptions
                {
                    Id = "jobindex",
                    DisplayName = "Jobindex",
                    BaseUrl = "https://jobindex.dk",
                    SearchPath = "/jobsoegning/it/systemudvikling/storkoebenhavn",
                    QueryParameterName = "q",
                    LocationParameterName = string.Empty,
                    AllowedHosts = ["jobindex.dk", "www.jobindex.dk"]
                }
            ]
        };

        var service = new JobDiscoverySearchPlanService(options);
        var result = service.Build(
            new JobDiscoveryProfileLight(
                "Lead Architect",
                "Lead architect",
                "Copenhagen",
                "Consulting",
                ["Lead Architect"],
                ["Azure"],
                string.Empty,
                "Lead Architect Azure"),
            locationOverride: "Aarhus");

        Assert.True(result.CanOpen);
        Assert.DoesNotContain("Aarhus", result.SearchUri!.Query, StringComparison.OrdinalIgnoreCase);
    }
}