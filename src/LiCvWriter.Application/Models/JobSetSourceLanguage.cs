namespace LiCvWriter.Application.Models;

/// <summary>
/// Declared language of the source job posting and company context for a job set.
/// Drives an optional language hint added to LLM prompts that consume that text.
/// <see cref="Auto"/> means no hint is injected; the model auto-detects.
/// </summary>
public enum JobSetSourceLanguage
{
    Auto,
    English,
    Danish
}

public static class JobSetSourceLanguageExtensions
{
    /// <summary>
    /// Returns a human-readable language label (e.g. <c>"Danish"</c>) suitable for
    /// embedding in an LLM prompt, or <c>null</c> when the source language is
    /// <see cref="JobSetSourceLanguage.Auto"/>.
    /// </summary>
    public static string? ToPromptHint(this JobSetSourceLanguage language)
        => language switch
        {
            JobSetSourceLanguage.English => "English",
            JobSetSourceLanguage.Danish => "Danish",
            _ => null
        };
}
