namespace LiCvWriter.Application.Models;

public sealed record LinkedInExportInspection(
    string RootPath,
    IReadOnlyList<string> DiscoveredFiles,
    IReadOnlyList<string> Warnings);
