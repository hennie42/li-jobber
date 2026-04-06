using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Core.Profiles;
using LiCvWriter.Infrastructure.Csv;
using System.Text;

namespace LiCvWriter.Infrastructure.LinkedIn;

public sealed class LinkedInExportImporter(
    SimpleCsvParser csvParser,
    LinkedInPartialDateParser dateParser,
    LinkedInMemberSnapshotImporter memberSnapshotImporter) : ILinkedInExportImporter
{
    public async Task<LinkedInExportImportResult> ImportAsync(string exportRootPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(exportRootPath))
        {
            throw new ArgumentException("Export root path is required.", nameof(exportRootPath));
        }

        if (!Directory.Exists(exportRootPath))
        {
            throw new DirectoryNotFoundException($"LinkedIn export folder was not found: {exportRootPath}");
        }

        var discoveredFiles = Directory
            .EnumerateFiles(exportRootPath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(exportRootPath, path))
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var warnings = new List<string>();

        foreach (var requiredFile in LinkedInExportFileMap.FirstClassFiles)
        {
            var absolutePath = Path.Combine(exportRootPath, requiredFile);
            if (!File.Exists(absolutePath))
            {
                warnings.Add($"Missing expected LinkedIn export file: {requiredFile}");
            }
        }

        var profileRecordSet = await ParseIfExistsAsync(exportRootPath, LinkedInExportFileMap.Profile, cancellationToken);
        var positionsRecordSet = await ParseIfExistsAsync(exportRootPath, LinkedInExportFileMap.Positions, cancellationToken);
        var educationRecordSet = await ParseIfExistsAsync(exportRootPath, LinkedInExportFileMap.Education, cancellationToken);
        var skillsRecordSet = await ParseIfExistsAsync(exportRootPath, LinkedInExportFileMap.Skills, cancellationToken);
        var certificationsRecordSet = await ParseIfExistsAsync(exportRootPath, LinkedInExportFileMap.Certifications, cancellationToken);
        var projectsRecordSet = await ParseIfExistsAsync(exportRootPath, LinkedInExportFileMap.Projects, cancellationToken);
        var recommendationsRecordSet = await ParseIfExistsAsync(exportRootPath, LinkedInExportFileMap.Recommendations, cancellationToken);
        var volunteeringRecordSet = await ParseIfExistsAsync(exportRootPath, LinkedInExportFileMap.VolunteeringExperiences, cancellationToken);
        var languagesRecordSet = await ParseIfExistsAsync(exportRootPath, LinkedInExportFileMap.Languages, cancellationToken);
        var publicationsRecordSet = await ParseIfExistsAsync(exportRootPath, LinkedInExportFileMap.Publications, cancellationToken);
        var patentsRecordSet = await ParseIfExistsAsync(exportRootPath, LinkedInExportFileMap.Patents, cancellationToken);
        var honorsRecordSet = await ParseIfExistsAsync(exportRootPath, LinkedInExportFileMap.Honors, cancellationToken);
        var coursesRecordSet = await ParseIfExistsAsync(exportRootPath, LinkedInExportFileMap.Courses, cancellationToken);
        var organizationsRecordSet = await ParseIfExistsAsync(exportRootPath, LinkedInExportFileMap.Organizations, cancellationToken);

        var profileRecord = profileRecordSet?.Records.FirstOrDefault();
        if (profileRecord is null)
        {
            warnings.Add("Profile.csv did not contain a profile row.");
        }

        var profile = new CandidateProfile
        {
            Name = new PersonName(
                Get(profileRecord, "First Name"),
                Get(profileRecord, "Last Name"),
                NullIfWhiteSpace(Get(profileRecord, "Maiden Name"))),
            Headline = NullIfWhiteSpace(Get(profileRecord, "Headline")),
            Summary = NullIfWhiteSpace(Get(profileRecord, "Summary")),
            Industry = NullIfWhiteSpace(Get(profileRecord, "Industry")),
            Location = NullIfWhiteSpace(Get(profileRecord, "Geo Location")),
            Experience = MapExperience(positionsRecordSet),
            Education = MapEducation(educationRecordSet),
            Skills = MapSkills(skillsRecordSet),
            Certifications = MapCertifications(certificationsRecordSet),
            Projects = MapProjects(projectsRecordSet),
            Recommendations = MapRecommendations(recommendationsRecordSet),
            ManualSignals = BuildImportedManualSignals(
                volunteeringRecordSet,
                languagesRecordSet,
                publicationsRecordSet,
                patentsRecordSet,
                honorsRecordSet,
                coursesRecordSet,
                organizationsRecordSet)
        };

        if (profile.Experience.Count == 0)
        {
            warnings.Add("No positions were imported from Positions.csv.");
        }

        return new LinkedInExportImportResult(
            profile,
            new LinkedInExportInspection(exportRootPath, discoveredFiles, warnings),
            warnings,
            "Local LinkedIn export");
    }

    public async Task<LinkedInExportImportResult> ImportMemberSnapshotAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        var result = await memberSnapshotImporter.ImportAsync(accessToken, ImportAsync, cancellationToken);
        Console.WriteLine(LinkedInImportDiagnosticsFormatter.BuildExperienceConsoleOutput(result.Profile));
        return result;
    }

    private IReadOnlyList<ExperienceEntry> MapExperience(CsvRecordSet? recordSet)
        => recordSet?.Records
            .Select(record => new ExperienceEntry(
                Get(record, "Company Name"),
                Get(record, "Title"),
                NullIfWhiteSpace(Get(record, "Description")),
                NullIfWhiteSpace(Get(record, "Location")),
                new DateRange(
                    dateParser.Parse(Get(record, "Started On")),
                    dateParser.Parse(Get(record, "Finished On")))))
            .Where(static record => !string.IsNullOrWhiteSpace(record.CompanyName) || !string.IsNullOrWhiteSpace(record.Title))
            .OrderByDescending(static record => record.Period.IsCurrent)
            .ThenByDescending(static record => GetExperienceSortDate(record.Period.FinishedOn) ?? GetExperienceSortDate(record.Period.StartedOn) ?? DateOnly.MinValue)
            .ThenByDescending(static record => GetExperienceSortDate(record.Period.StartedOn) ?? DateOnly.MinValue)
            .ThenBy(static record => record.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray()
           ?? Array.Empty<ExperienceEntry>();

    private IReadOnlyList<EducationEntry> MapEducation(CsvRecordSet? recordSet)
        => recordSet?.Records
            .Select(record => new EducationEntry(
                NullIfWhiteSpace(Get(record, "School Name")) ?? "Unspecified institution",
                NullIfWhiteSpace(Get(record, "Degree Name")),
                NullIfWhiteSpace(Get(record, "Notes")),
                NullIfWhiteSpace(Get(record, "Activities")),
                new DateRange(
                    dateParser.Parse(Get(record, "Start Date")),
                    dateParser.Parse(Get(record, "End Date")))))
            .Where(static record => !string.IsNullOrWhiteSpace(record.SchoolName) || !string.IsNullOrWhiteSpace(record.DegreeName) || record.Period.StartedOn is not null)
            .ToArray()
           ?? Array.Empty<EducationEntry>();

    private static IReadOnlyList<SkillTag> MapSkills(CsvRecordSet? recordSet)
        => recordSet?.Records
            .Select((record, index) => new SkillTag(Get(record, "Name"), index + 1))
            .Where(static record => !string.IsNullOrWhiteSpace(record.Name))
            .ToArray()
           ?? Array.Empty<SkillTag>();

    private IReadOnlyList<CertificationEntry> MapCertifications(CsvRecordSet? recordSet)
        => recordSet?.Records
            .Select(record => new CertificationEntry(
                Get(record, "Name"),
                NullIfWhiteSpace(Get(record, "Authority")),
                CreateUri(Get(record, "Url")),
                new DateRange(
                    dateParser.Parse(Get(record, "Started On")),
                    dateParser.Parse(Get(record, "Finished On"))),
                NullIfWhiteSpace(Get(record, "License Number"))))
            .Where(static record => !string.IsNullOrWhiteSpace(record.Name))
            .ToArray()
           ?? Array.Empty<CertificationEntry>();

    private IReadOnlyList<ProjectEntry> MapProjects(CsvRecordSet? recordSet)
        => recordSet?.Records
            .Select(record => new ProjectEntry(
                Get(record, "Title"),
                NullIfWhiteSpace(Get(record, "Description")),
                CreateUri(Get(record, "Url")),
                new DateRange(
                    dateParser.Parse(Get(record, "Started On")),
                    dateParser.Parse(Get(record, "Finished On")))))
            .Where(static record => !string.IsNullOrWhiteSpace(record.Title))
            .ToArray()
           ?? Array.Empty<ProjectEntry>();

    private IReadOnlyList<RecommendationEntry> MapRecommendations(CsvRecordSet? recordSet)
        => recordSet?.Records
            .Where(record => string.Equals(Get(record, "Status"), "VISIBLE", StringComparison.OrdinalIgnoreCase))
            .Select(record => new RecommendationEntry(
                new PersonName(Get(record, "First Name"), Get(record, "Last Name")),
                NullIfWhiteSpace(Get(record, "Company")),
                NullIfWhiteSpace(Get(record, "Job Title")),
                Get(record, "Text"),
                Get(record, "Status"),
                dateParser.Parse(Get(record, "Creation Date"))))
            .Where(static record => !string.IsNullOrWhiteSpace(record.Text))
            .ToArray()
           ?? Array.Empty<RecommendationEntry>();

    private async Task<CsvRecordSet?> ParseIfExistsAsync(string exportRootPath, string relativePath, CancellationToken cancellationToken)
    {
        var absolutePath = Path.Combine(exportRootPath, relativePath);
        if (!File.Exists(absolutePath))
        {
            return null;
        }

        return await csvParser.ParseFileAsync(absolutePath, cancellationToken);
    }

    private static IReadOnlyDictionary<string, string> BuildImportedManualSignals(
        CsvRecordSet? volunteeringRecordSet,
        CsvRecordSet? languagesRecordSet,
        CsvRecordSet? publicationsRecordSet,
        CsvRecordSet? patentsRecordSet,
        CsvRecordSet? honorsRecordSet,
        CsvRecordSet? coursesRecordSet,
        CsvRecordSet? organizationsRecordSet)
    {
        var signals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        AddManualSignal(signals, "Volunteering experiences", FormatRecords(
            volunteeringRecordSet,
            ["Role", "Title", "Organization", "Company Name", "Cause"],
            ["Description", "Location", "Started On", "Finished On"]));
        AddManualSignal(signals, "Languages", FormatRecords(
            languagesRecordSet,
            ["Name", "Language", "Locale"],
            ["Proficiency", "Proficiency Level", "Level"]));
        AddManualSignal(signals, "Publications", FormatRecords(
            publicationsRecordSet,
            ["Title", "Name", "Publisher"],
            ["Description", "Published On", "Publication Date", "Url"]));
        AddManualSignal(signals, "Patents", FormatRecords(
            patentsRecordSet,
            ["Title", "Name", "Patent Number", "Application Number"],
            ["Issuer", "Office", "Issued On", "Publication Date", "Url"]));
        AddManualSignal(signals, "Honors", FormatRecords(
            honorsRecordSet,
            ["Title", "Name", "Issuer", "Awarder"],
            ["Description", "Issued On", "Date"]));
        AddManualSignal(signals, "Courses", FormatRecords(
            coursesRecordSet,
            ["Course Name", "Name", "Title", "Provider", "School Name"],
            ["Description", "Started On", "Finished On", "Completed On"]));
        AddManualSignal(signals, "Organizations", FormatRecords(
            organizationsRecordSet,
            ["Name", "Title", "Role"],
            ["Description", "Started On", "Finished On"]));

        return signals;
    }

    private static void AddManualSignal(IDictionary<string, string> signals, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            signals[key] = value;
        }
    }

    private static string? FormatRecords(
        CsvRecordSet? recordSet,
        IReadOnlyList<string> headlineFields,
        IReadOnlyList<string> detailFields)
    {
        if (recordSet is null)
        {
            return null;
        }

        var lines = recordSet.Records
            .Select(record => FormatRecord(record, headlineFields, detailFields))
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return lines.Length == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    private static string? FormatRecord(
        IReadOnlyDictionary<string, string> record,
        IReadOnlyList<string> headlineFields,
        IReadOnlyList<string> detailFields)
    {
        var headline = headlineFields
            .Select(field => Get(record, field))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var details = detailFields
            .Select(field => (Field: field, Value: Get(record, field)))
            .Where(static item => !string.IsNullOrWhiteSpace(item.Value))
            .Select(static item => $"{NormalizeFieldLabel(item.Field)}: {item.Value}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (headline.Length == 0 && details.Length == 0)
        {
            var fallback = record
                .Where(static pair => !string.IsNullOrWhiteSpace(pair.Value))
                .Select(static pair => $"{NormalizeFieldLabel(pair.Key)}: {pair.Value.Trim()}")
                .Take(5)
                .ToArray();

            return fallback.Length == 0 ? null : string.Join(" | ", fallback);
        }

        var segments = new List<string>();
        if (headline.Length > 0)
        {
            segments.Add(string.Join(" | ", headline));
        }

        if (details.Length > 0)
        {
            segments.Add(string.Join("; ", details));
        }

        return string.Join(" | ", segments);
    }

    private static string NormalizeFieldLabel(string field)
        => field.Replace("_", " ", StringComparison.Ordinal).Trim();

    private static string Get(IReadOnlyDictionary<string, string>? record, string key)
        => record is not null && record.TryGetValue(key, out var value) ? value.Trim() : string.Empty;

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static Uri? CreateUri(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    private static DateOnly? GetExperienceSortDate(PartialDate? partialDate)
        => partialDate?.ToDateOnly();

}
