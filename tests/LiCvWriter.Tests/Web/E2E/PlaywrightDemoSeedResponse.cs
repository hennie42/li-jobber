namespace LiCvWriter.Tests.Web.E2E;

public sealed record PlaywrightDemoSeedResponse(
    string Model,
    IReadOnlyList<string> CompanyNames,
    IReadOnlyList<string> JobSetIds);
