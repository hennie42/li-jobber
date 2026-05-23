using LiCvWriter.Web.Services;

namespace LiCvWriter.Tests.Web;

public sealed class FoundrySetupDiagnosticFormatterTests
{
    [Fact]
    public void Create_WhenMessageIsBlank_ReturnsNull()
    {
        Assert.Null(FoundrySetupDiagnosticFormatter.Create("  "));
    }

    [Fact]
    public void Create_WhenWinMlAdapterFailsToLoad_ReturnsAdapterGuidance()
    {
        var diagnostic = FoundrySetupDiagnosticFormatter.Create(
            "Microsoft Foundry Local for Windows could not load the WinML adapter from 'C:\\temp\\LiCvWriter.Infrastructure.WinML.dll'. WinRT.Runtime.dll was missing.");

        Assert.NotNull(diagnostic);
        Assert.Equal("Foundry Local needs Windows adapter attention.", diagnostic!.StatusMessage);
        Assert.Contains("Windows-only Foundry adapter", diagnostic.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(diagnostic.NextSteps, step => step.Contains("WinRT.Runtime.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Create_WhenBetalgoAssemblyIsMissing_ReturnsAdapterGuidance()
    {
        var diagnostic = FoundrySetupDiagnosticFormatter.Create(
            "Could not load file or assembly 'Betalgo.Ranul.OpenAI, Version=9.1.0.0, Culture=neutral, PublicKeyToken=null'. The system cannot find the file specified.");

        Assert.NotNull(diagnostic);
        Assert.Equal("Foundry Local needs Windows adapter attention.", diagnostic!.StatusMessage);
        Assert.Contains(diagnostic.NextSteps, step => step.Contains("Betalgo.Ranul.OpenAI.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Create_WhenNativeRuntimeCoreIsMissing_ReturnsRuntimeGuidance()
    {
        var diagnostic = FoundrySetupDiagnosticFormatter.Create(
            "Microsoft Foundry Local could not start because its native runtime core is missing. Microsoft.AI.Foundry.Local.Core was not found at runtime.");

        Assert.NotNull(diagnostic);
        Assert.Equal("Foundry Local native runtime is unavailable.", diagnostic!.StatusMessage);
        Assert.Contains("native runtime core", diagnostic.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(diagnostic.NextSteps, step => step.Contains("Foundry Local runtime", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Create_WhenModelIsNotDownloaded_ReturnsDownloadGuidance()
    {
        var diagnostic = FoundrySetupDiagnosticFormatter.Create(
            "The Foundry model 'phi-3.5-mini' is not downloaded. Download it from Start / Setup before using it.");

        Assert.NotNull(diagnostic);
        Assert.Equal("A Foundry model still needs to be downloaded.", diagnostic!.StatusMessage);
        Assert.Contains("not cached locally", diagnostic.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(diagnostic.NextSteps, step => step.Contains("Download the alias", StringComparison.OrdinalIgnoreCase));
    }
}