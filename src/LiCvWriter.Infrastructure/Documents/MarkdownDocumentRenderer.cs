using System.Text;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Core.Documents;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Infrastructure.Documents;

public sealed class MarkdownDocumentRenderer : IDocumentRenderer
{
    public Task<GeneratedDocument> RenderAsync(DocumentRenderRequest request, CancellationToken cancellationToken = default)
    {
        var outputLanguage = request.OutputLanguage;
        var selectedEvidence = request.EvidenceSelection?.SelectedEvidence ?? Array.Empty<RankedEvidenceItem>();
        var builder = new StringBuilder();
        builder.AppendLine($"# {request.Candidate.Name.FullName}");
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(request.Candidate.Headline))
        {
            builder.AppendLine($"> {request.Candidate.Headline}");
            builder.AppendLine();
        }

        builder.AppendLine($"## {Translate(outputLanguage, "Target Role", "Målrolle")}");
        builder.AppendLine();
        builder.AppendLine($"- {Translate(outputLanguage, "Role", "Rolle")}: {request.JobPosting.RoleTitle}");
        builder.AppendLine($"- {Translate(outputLanguage, "Company", "Virksomhed")}: {request.JobPosting.CompanyName}");
        builder.AppendLine();

        var generatedBody = !string.IsNullOrWhiteSpace(request.GeneratedBody)
            ? request.GeneratedBody.Trim()
            : request.Candidate.Summary?.Trim();

        switch (request.Kind)
        {
            case DocumentKind.Cv:
                AppendSection(builder, Translate(outputLanguage, "Executive Summary", "Resumé"), generatedBody);
                AppendFitSnapshot(builder, request.JobFitAssessment, outputLanguage);
                AppendApplicantAngle(builder, request.ApplicantDifferentiatorProfile, outputLanguage);
                if (HasSelectedProof(selectedEvidence))
                {
                    AppendSelectedEvidence(builder, selectedEvidence, outputLanguage);
                }
                else
                {
                    AppendExperience(builder, request, outputLanguage);
                }

                if (HasSelectedCertifications(selectedEvidence))
                {
                    AppendSelectedCertifications(builder, selectedEvidence, outputLanguage);
                }
                break;
            case DocumentKind.CoverLetter:
                AppendSection(builder, Translate(outputLanguage, "Letter", "Ansøgning"), generatedBody);
                AppendFitSnapshot(builder, request.JobFitAssessment, outputLanguage);
                AppendApplicantAngle(builder, request.ApplicantDifferentiatorProfile, outputLanguage);
                AppendSelectedEvidence(builder, selectedEvidence, outputLanguage);
                break;
            case DocumentKind.ProfileSummary:
                AppendSection(builder, Translate(outputLanguage, "Summary", "Profil") , generatedBody);
                AppendApplicantAngle(builder, request.ApplicantDifferentiatorProfile, outputLanguage);
                if (HasSelectedCertifications(selectedEvidence))
                {
                    AppendSelectedCertifications(builder, selectedEvidence, outputLanguage);
                }
                break;
            case DocumentKind.InterviewNotes:
                AppendSection(builder, Translate(outputLanguage, "Talking Points", "Samtalepunkter"), generatedBody);
                AppendFitSnapshot(builder, request.JobFitAssessment, outputLanguage);
                AppendSelectedEvidence(builder, selectedEvidence, outputLanguage);
                if (!selectedEvidence.Any(static item => item.Evidence.Type == CandidateEvidenceType.Recommendation))
                {
                    AppendRecommendations(builder, request, outputLanguage);
                }
                break;
        }

        return Task.FromResult(new GeneratedDocument(
            request.Kind,
            $"{request.Candidate.Name.FullName} - {request.Kind}",
            builder.ToString().Trim(),
            builder.ToString().Trim(),
            DateTimeOffset.UtcNow));
    }

    private static void AppendSection(StringBuilder builder, string title, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        builder.AppendLine($"## {title}");
        builder.AppendLine();
        builder.AppendLine(content);
        builder.AppendLine();
    }

    private static void AppendFitSnapshot(StringBuilder builder, JobFitAssessment? assessment, OutputLanguage outputLanguage)
    {
        if (assessment is null || !assessment.HasSignals || assessment.Strengths.Count == 0)
        {
            return;
        }

        builder.AppendLine($"## {Translate(outputLanguage, "Fit Snapshot", "Matchvurdering")}");
        builder.AppendLine();

        foreach (var strength in assessment.Strengths.Take(3))
        {
            builder.AppendLine($"- {Translate(outputLanguage, "Strength", "Styrke")}: {strength}");
        }

        builder.AppendLine();
    }

    private static void AppendApplicantAngle(StringBuilder builder, ApplicantDifferentiatorProfile? differentiatorProfile, OutputLanguage outputLanguage)
    {
        if (differentiatorProfile is null || !differentiatorProfile.HasContent)
        {
            return;
        }

        builder.AppendLine($"## {Translate(outputLanguage, "Applicant Angle", "Kandidatvinkel")}");
        builder.AppendLine();
        foreach (var line in differentiatorProfile.ToSummaryLines())
        {
            builder.AppendLine($"- {line}");
        }

        builder.AppendLine();
    }

    private static void AppendExperience(StringBuilder builder, DocumentRenderRequest request, OutputLanguage outputLanguage)
    {
        if (request.Candidate.Experience.Count == 0)
        {
            return;
        }

        builder.AppendLine($"## {Translate(outputLanguage, "Experience", "Erfaring")}");
        builder.AppendLine();

        foreach (var role in request.Candidate.Experience.Take(8))
        {
            builder.AppendLine($"### {role.Title} | {role.CompanyName}");
            if (!string.IsNullOrWhiteSpace(role.Period.DisplayValue))
            {
                builder.AppendLine(role.Period.DisplayValue);
            }

            if (!string.IsNullOrWhiteSpace(role.Description))
            {
                builder.AppendLine();
                builder.AppendLine(role.Description.Trim());
            }

            builder.AppendLine();
        }
    }

    private static void AppendSelectedEvidence(StringBuilder builder, IReadOnlyList<RankedEvidenceItem> selectedEvidence, OutputLanguage outputLanguage)
    {
        var proofItems = selectedEvidence
            .Take(6)
            .ToArray();

        if (proofItems.Length == 0)
        {
            return;
        }

        builder.AppendLine($"## {Translate(outputLanguage, "Selected Proof Points", "Udvalgt dokumentation")}");
        builder.AppendLine();
        foreach (var item in proofItems)
        {
            builder.AppendLine($"- {item.Evidence.Title}: {item.Evidence.Summary}");
        }

        builder.AppendLine();
    }

    private static void AppendSelectedCertifications(StringBuilder builder, IReadOnlyList<RankedEvidenceItem> selectedEvidence, OutputLanguage outputLanguage)
    {
        var certItems = selectedEvidence
            .Where(static item => item.Evidence.Type is CandidateEvidenceType.Certification)
            .Take(8)
            .ToArray();

        if (certItems.Length == 0)
        {
            return;
        }

        builder.AppendLine($"## {Translate(outputLanguage, "Certifications", "Certificeringer")}");
        builder.AppendLine();
        foreach (var item in certItems)
        {
            builder.AppendLine($"- {item.Evidence.Title}");
        }

        builder.AppendLine();
    }

    private static void AppendRecommendations(StringBuilder builder, DocumentRenderRequest request, OutputLanguage outputLanguage)
    {
        if (request.Candidate.Recommendations.Count == 0)
        {
            return;
        }

        builder.AppendLine($"## {Translate(outputLanguage, "Evidence From Recommendations", "Anbefalinger")}");
        builder.AppendLine();
        foreach (var recommendation in request.Candidate.Recommendations.Take(3))
        {
            builder.AppendLine($"- {recommendation.Author.FullName}{FormatAt(recommendation.Company)}: {recommendation.Text}");
        }

        builder.AppendLine();
    }

    private static string FormatAt(string? company)
        => string.IsNullOrWhiteSpace(company) ? string.Empty : $" at {company}";

    private static bool HasSelectedProof(IReadOnlyList<RankedEvidenceItem> selectedEvidence)
        => selectedEvidence.Any(static item => item.Evidence.Type is CandidateEvidenceType.Experience or CandidateEvidenceType.Project or CandidateEvidenceType.Recommendation or CandidateEvidenceType.Certification or CandidateEvidenceType.Note);

    private static bool HasSelectedCertifications(IReadOnlyList<RankedEvidenceItem> selectedEvidence)
        => selectedEvidence.Any(static item => item.Evidence.Type is CandidateEvidenceType.Certification);

    private static string Translate(OutputLanguage outputLanguage, string english, string danish)
        => outputLanguage == OutputLanguage.Danish ? danish : english;
}

