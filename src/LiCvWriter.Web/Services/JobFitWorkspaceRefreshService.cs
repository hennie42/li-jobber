using LiCvWriter.Application.Services;

namespace LiCvWriter.Web.Services;

public sealed class JobFitWorkspaceRefreshService(
    JobFitAnalysisService jobFitAnalysisService,
    EvidenceSelectionService evidenceSelectionService,
    WorkspaceSession workspace)
{
    public bool RefreshActiveJobSet()
        => RefreshJobSet(workspace.ActiveJobSetId);

    /// <summary>
    /// Re-runs evidence selection for the active job set using its current fit assessment.
    /// Call after the LLM enhancement pass has updated the fit assessment in-place.
    /// </summary>
    public void RefreshActiveJobSetEvidence()
        => RefreshJobSetEvidence(workspace.ActiveJobSetId);

    public void RefreshJobSetEvidence(string jobSetId)
    {
        var candidateProfile = workspace.CandidateProfile;
        var jobSet = workspace.JobSets.FirstOrDefault(job => job.Id == jobSetId);
        if (candidateProfile is null || jobSet?.JobPosting is null || jobSet.JobFitAssessment is null)
        {
            return;
        }

        var evidenceSelection = evidenceSelectionService.Build(
            candidateProfile,
            jobSet.JobPosting,
            jobSet.CompanyProfile,
            jobSet.JobFitAssessment,
            workspace.ApplicantDifferentiatorProfile);

        workspace.SetJobSetEvidenceSelection(jobSetId, evidenceSelection);
    }

    public int RefreshAllJobSets()
    {
        var refreshed = 0;
        foreach (var jobSetId in workspace.JobSets
                     .Where(static jobSet => jobSet.JobPosting is not null)
                     .OrderBy(static jobSet => jobSet.SortOrder)
                     .Select(static jobSet => jobSet.Id))
        {
            if (RefreshJobSet(jobSetId))
            {
                refreshed++;
            }
        }

        return refreshed;
    }

    public bool RefreshJobSet(string jobSetId)
    {
        var candidateProfile = workspace.CandidateProfile;
        var jobSet = workspace.JobSets.FirstOrDefault(job => job.Id == jobSetId);
        if (candidateProfile is null || jobSet?.JobPosting is null)
        {
            return false;
        }

        var fitAssessment = jobFitAnalysisService.Analyze(
            candidateProfile,
            jobSet.JobPosting,
            jobSet.CompanyProfile,
            workspace.ApplicantDifferentiatorProfile);

        workspace.SetJobSetJobFitAssessment(jobSetId, fitAssessment);

        var evidenceSelection = evidenceSelectionService.Build(
            candidateProfile,
            jobSet.JobPosting,
            jobSet.CompanyProfile,
            fitAssessment,
            workspace.ApplicantDifferentiatorProfile);

        workspace.SetJobSetEvidenceSelection(jobSetId, evidenceSelection);
        return true;
    }
}