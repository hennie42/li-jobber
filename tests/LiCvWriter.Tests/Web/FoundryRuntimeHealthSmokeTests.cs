using LiCvWriter.Application.Models;

namespace LiCvWriter.Tests.Web;

public sealed class FoundryRuntimeHealthSmokeTests : IClassFixture<FoundryHealthAppFixture>
{
    private readonly FoundryHealthAppFixture fixture;

    public FoundryRuntimeHealthSmokeTests(FoundryHealthAppFixture fixture)
    {
        this.fixture = fixture;
    }

    [LiveWindowsFoundryFact]
    public async Task HealthEndpoints_WhenWindowsFoundryBridgeLoads_ReturnSuccess()
    {
        var availability = await fixture.GetJsonAsync<LlmModelAvailability>("/api/health/foundry");
        var acceleration = await fixture.GetJsonAsync<FoundryAccelerationSnapshot>("/api/health/foundry/acceleration");

        Assert.Equal(LlmProviderKind.Foundry, availability.Provider);
        Assert.True(acceleration.IsSupported);
        Assert.True(acceleration.IsEnabled);
        Assert.True(acceleration.CanManageExecutionProviders);
        Assert.False(string.IsNullOrWhiteSpace(acceleration.StatusMessage));
    }
}