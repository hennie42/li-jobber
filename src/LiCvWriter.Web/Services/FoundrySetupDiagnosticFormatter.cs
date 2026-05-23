namespace LiCvWriter.Web.Services;

public sealed record FoundrySetupDiagnostic(
    string StatusMessage,
    string Summary,
    string Guidance,
    IReadOnlyList<string> NextSteps);

public static class FoundrySetupDiagnosticFormatter
{
    /// <summary>
    /// Translates raw Foundry setup failures into user-facing guidance for the setup page.
    /// </summary>
    public static FoundrySetupDiagnostic? Create(string? rawMessage)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            return null;
        }

        var message = rawMessage.Trim();

        if (IsAdapterLoadFailure(message))
        {
            return new FoundrySetupDiagnostic(
                "Foundry Local needs Windows adapter attention.",
                "The Windows-only Foundry adapter did not load cleanly in this app process.",
                "The web app can see Foundry Local, but the WinML adapter payload beside the web binaries is incomplete or out of date.",
                [
                    "Rebuild the web project on Windows so the LiCvWriter.Infrastructure.WinML payload is copied into the web output folder.",
                    "Confirm the web output contains Microsoft.AI.Foundry.Local.WinML.dll, Betalgo.Ranul.OpenAI.dll, and WinRT.Runtime.dll beside LiCvWriter.Infrastructure.WinML.dll.",
                    "Retry \"Load Foundry catalog\" after the rebuild completes."
                ]);
        }

        if (IsNativeRuntimeFailure(message))
        {
            return new FoundrySetupDiagnostic(
                "Foundry Local native runtime is unavailable.",
                "The managed Foundry SDK loaded, but the native runtime core it needs was not available to this process.",
                "This usually means the local Foundry runtime payload or its native dependencies are missing from the machine or not visible to the running app.",
                [
                    "Verify the Windows machine has the expected Foundry Local runtime installed for this repository's pinned package set.",
                    "Rebuild the web project after updating Foundry Local or Windows App SDK components so the app output is refreshed.",
                    "Retry \"Load Foundry catalog\" after the runtime is available."
                ]);
        }

        if (IsModelDownloadFailure(message))
        {
            return new FoundrySetupDiagnostic(
                "A Foundry model still needs to be downloaded.",
                "The selected alias exists in the Foundry catalog, but it is not cached locally yet.",
                "Finish the model download first, then choose the cached alias for the session.",
                [
                    "Load the Foundry catalog if the model table is empty.",
                    "Download the alias from the catalog list on this page.",
                    "Select the cached alias again once the download completes."
                ]);
        }

        return new FoundrySetupDiagnostic(
            "Foundry Local needs attention.",
            "Foundry Local returned an error while this setup step was running.",
            "The technical detail below contains the raw exception. Use it to confirm whether the issue is adapter loading, native runtime availability, or a model/setup mismatch.",
            [
                "Retry the step once to rule out a transient startup failure.",
                "If the error mentions WinML, rebuild the web project on Windows so the adapter payload is recopied.",
                "If the error persists, use the technical detail below to check which local dependency is missing."
            ]);
    }

    /// <summary>
    /// Matches errors caused by a missing or stale WinML adapter payload.
    /// </summary>
    private static bool IsAdapterLoadFailure(string message)
        => message.Contains("WinML adapter", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Microsoft.AI.Foundry.Local.WinML", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Betalgo.Ranul.OpenAI", StringComparison.OrdinalIgnoreCase)
            || message.Contains("WinRT.Runtime", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Matches errors caused by the native Foundry runtime core being unavailable.
    /// </summary>
    private static bool IsNativeRuntimeFailure(string message)
        => message.Contains("native runtime core", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Microsoft.AI.Foundry.Local.Core", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Matches errors that mean the selected Foundry model still needs to be downloaded.
    /// </summary>
    private static bool IsModelDownloadFailure(string message)
        => message.Contains("is not downloaded", StringComparison.OrdinalIgnoreCase)
            || message.Contains("download it from Start / Setup", StringComparison.OrdinalIgnoreCase);
}