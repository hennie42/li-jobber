using LiCvWriter.Web.Services;

namespace LiCvWriter.Tests.Web;

public sealed class FoundryProgressFormatterTests
{
    [Fact]
    public void NormalizePercent_WhenRawProgressIsFraction_ScalesToPercentage()
    {
        var normalized = FoundryProgressFormatter.NormalizePercent(0.42d);

        Assert.Equal(42.0d, normalized);
    }

    [Fact]
    public void NormalizePercent_WhenRawProgressIsWholePercent_PreservesValue()
    {
        var normalized = FoundryProgressFormatter.NormalizePercent(42.0d);

        Assert.Equal(42.0d, normalized);
    }

    [Fact]
    public void NormalizePercent_WhenRawProgressLooksLikeBasisPoints_NormalizesValue()
    {
        var normalized = FoundryProgressFormatter.NormalizePercent(4_250.0d);

        Assert.Equal(42.5d, normalized);
    }

    [Fact]
    public void FormatDetail_WhenRawProgressIsAbsurd_FallsBackToSdkReportedMessage()
    {
        var detail = FoundryProgressFormatter.FormatDetail("Downloading model before benchmark", 36_294_423_566_532_400d);

        Assert.Equal("Downloading model before benchmark. Progress reported by Foundry Local.", detail);
    }
}