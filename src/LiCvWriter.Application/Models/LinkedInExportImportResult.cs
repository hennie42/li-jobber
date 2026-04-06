using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Application.Models;

public sealed record LinkedInExportImportResult(
    CandidateProfile Profile,
    LinkedInExportInspection Inspection,
    IReadOnlyList<string> Warnings,
    string SourceDescription = "Local LinkedIn export");
