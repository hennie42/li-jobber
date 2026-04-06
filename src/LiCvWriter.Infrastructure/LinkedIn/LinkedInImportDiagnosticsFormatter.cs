using System.Text;
using LiCvWriter.Application.Models;
using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Infrastructure.LinkedIn;

public static class LinkedInImportDiagnosticsFormatter
{
    public static LinkedInImportDiagnosticsSnapshot BuildSnapshot(LinkedInExportImportResult importResult)
    {
        ArgumentNullException.ThrowIfNull(importResult);

        var profile = importResult.Profile;
        return new LinkedInImportDiagnosticsSnapshot(
            importResult.SourceDescription,
            importResult.Inspection.RootPath,
            importResult.Inspection.DiscoveredFiles.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            importResult.Warnings.Distinct(StringComparer.Ordinal).ToArray(),
            new LinkedInImportProfileSummary(
                profile.Name.FullName,
                profile.Headline,
                profile.Summary,
                profile.Experience.Count,
                profile.Education.Count,
                profile.Skills.Count,
                profile.Certifications.Count,
                profile.Projects.Count,
                profile.Recommendations.Count,
                profile.ManualSignals.Count),
            profile.Experience.Select(static role => new LinkedInImportExperienceSnapshot(
                $"{role.Title} @ {role.CompanyName}".Trim(' ', '@'),
                role.Period.DisplayValue,
                role.Location,
                SplitLines(role.Description, fallback: "(none provided)")))
                .ToArray(),
            profile.ManualSignals
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => new LinkedInImportManualSignalSnapshot(pair.Key, SplitLines(pair.Value)))
                .ToArray());
    }

    public static string BuildExperienceConsoleOutput(CandidateProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var builder = new StringBuilder();
        builder.AppendLine();
        builder.AppendLine("LinkedIn DMA Imported Experience");
        builder.AppendLine(new string('=', 31));

        if (profile.Experience.Count == 0)
        {
            builder.AppendLine("No job titles were imported.");
            return builder.ToString();
        }

        var experienceEntries = profile.Experience.Select(static role => new LinkedInImportExperienceSnapshot(
            $"{role.Title} @ {role.CompanyName}".Trim(' ', '@'),
            role.Period.DisplayValue,
            role.Location,
            SplitLines(role.Description, fallback: "(none provided)")))
            .ToArray();

        for (var index = 0; index < experienceEntries.Length; index++)
        {
            var role = experienceEntries[index];
            builder.AppendLine($"{index + 1}. {role.DisplayTitle}");

            if (!string.IsNullOrWhiteSpace(role.Period))
            {
                builder.AppendLine($"   Period: {role.Period}");
            }

            if (!string.IsNullOrWhiteSpace(role.Location))
            {
                builder.AppendLine($"   Location: {role.Location}");
            }

            builder.AppendLine("   Description:");
            foreach (var line in role.DescriptionLines)
            {
                builder.AppendLine($"     {line}");
            }

            if (index < experienceEntries.Length - 1)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    private static string[] SplitLines(string? value, string? fallback = null)
    {
        var lines = (value ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        return lines.Length > 0
            ? lines
            : string.IsNullOrWhiteSpace(fallback)
                ? Array.Empty<string>()
                : [fallback];
    }
}

public sealed record LinkedInImportDiagnosticsSnapshot(
    string SourceDescription,
    string InspectionRootPath,
    IReadOnlyList<string> DiscoveredFiles,
    IReadOnlyList<string> Warnings,
    LinkedInImportProfileSummary Profile,
    IReadOnlyList<LinkedInImportExperienceSnapshot> ExperienceEntries,
    IReadOnlyList<LinkedInImportManualSignalSnapshot> ManualSignalEntries);

public sealed record LinkedInImportProfileSummary(
    string FullName,
    string? Headline,
    string? Summary,
    int ExperienceCount,
    int EducationCount,
    int SkillCount,
    int CertificationCount,
    int ProjectCount,
    int RecommendationCount,
    int ManualSignalCount);

public sealed record LinkedInImportExperienceSnapshot(
    string DisplayTitle,
    string? Period,
    string? Location,
    IReadOnlyList<string> DescriptionLines);

public sealed record LinkedInImportManualSignalSnapshot(
    string Title,
    IReadOnlyList<string> Lines);