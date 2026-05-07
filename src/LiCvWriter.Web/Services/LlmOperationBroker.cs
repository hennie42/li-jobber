using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Core.Documents;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Core.Profiles;
using LiCvWriter.Infrastructure.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace LiCvWriter.Web.Services;

public sealed class LlmOperationBroker(
    IServiceScopeFactory scopeFactory,
    WorkspaceSession workspace,
    OperationStatusService operations,
    OllamaOptions ollamaOptions,
    TimeProvider timeProvider)
{
    private readonly object gate = new();
    private readonly ConcurrentDictionary<string, LlmOperationState> operationsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> activeOperationsByJobSet = new(StringComparer.OrdinalIgnoreCase);

    public LlmOperationStartResult StartDraftGeneration(StartDraftGenerationOperationRequest request)
    {
        var input = CaptureDraftGenerationInput(request);
        var operationId = $"draft-generation-{Guid.NewGuid():N}";
        var startedAt = timeProvider.GetUtcNow();
        var initialSnapshot = new LlmOperationSnapshot(
            operationId,
            "generate-drafts",
            "running",
            input.JobSet.Id,
            startedAt,
            startedAt,
            "Generating targeted drafts",
            $"{input.DocumentKinds.Count} document type(s) queued for {input.JobSet.Title}.",
            input.SelectedModel,
            Sequence: 0);

        var state = BeginOperation(input.JobSet.Id, initialSnapshot);

        workspace.MarkLlmWorkStarted();
        workspace.ClearJobSetGeneratedArtifacts(input.JobSet.Id);
        workspace.MarkJobSetRunning(input.JobSet.Id, "Draft generation is running for this tab.", JobSetSubtask.DraftGeneration);
        operations.BeginLlmOperation(initialSnapshot.Message, initialSnapshot.Detail);

        _ = RunDraftGenerationAsync(state, input);

        return BuildStartResult(operationId);
    }

    public LlmOperationStartResult StartJobContextAnalysis(StartJobContextOperationRequest request)
    {
        var input = CaptureJobContextInput(request);
        var operationId = $"job-context-{Guid.NewGuid():N}";
        var startedAt = timeProvider.GetUtcNow();
        var detail = input.JobSet.InputMode == JobSetInputMode.PasteText
            ? $"Analyzing pasted job text for {input.JobSet.Title}."
            : $"Analyzing {input.JobUri} and company context for {input.JobSet.Title}.";
        var initialSnapshot = new LlmOperationSnapshot(
            operationId,
            "analyze-job-context",
            "running",
            input.JobSet.Id,
            startedAt,
            startedAt,
            "Analyzing job and company context",
            detail,
            input.SelectedModel,
            Sequence: 0);

        var state = BeginOperation(input.JobSet.Id, initialSnapshot);

        workspace.MarkLlmWorkStarted();
        workspace.MarkJobSetRunning(input.JobSet.Id, "Job and company context analysis is running for this tab.", JobSetSubtask.JobContext);
        operations.BeginLlmOperation(initialSnapshot.Message, initialSnapshot.Detail);

        _ = RunJobContextAnalysisAsync(state, input);

        return BuildStartResult(operationId);
    }

    public LlmOperationStartResult StartTechnologyGapAnalysis(StartTechnologyGapOperationRequest request)
    {
        var input = CaptureTechnologyGapInput(request);
        var operationId = $"technology-gap-{Guid.NewGuid():N}";
        var startedAt = timeProvider.GetUtcNow();
        var initialSnapshot = new LlmOperationSnapshot(
            operationId,
            "technology-gap",
            "running",
            input.JobSet.Id,
            startedAt,
            startedAt,
            "Analyzing technology gaps",
            $"Comparing profile evidence against {input.JobSet.Title}.",
            input.SelectedModel,
            Sequence: 0);

        var state = BeginOperation(input.JobSet.Id, initialSnapshot);

        workspace.MarkLlmWorkStarted();
        workspace.MarkJobSetRunning(input.JobSet.Id, "Technology gap analysis is running for this tab.", JobSetSubtask.TechnologyGap);
        operations.BeginLlmOperation(initialSnapshot.Message, initialSnapshot.Detail);

        _ = RunTechnologyGapAnalysisAsync(state, input);

        return BuildStartResult(operationId);
    }

    public LlmOperationStartResult StartFitReviewAnalysis(StartFitReviewOperationRequest request)
    {
        var input = CaptureFitReviewInput(request);
        var operationId = $"fit-review-{Guid.NewGuid():N}";
        var startedAt = timeProvider.GetUtcNow();
        var initialSnapshot = new LlmOperationSnapshot(
            operationId,
            "fit-review",
            "running",
            input.JobSet.Id,
            startedAt,
            startedAt,
            "Analyzing job fit",
            $"Refreshing deterministic fit signals for {input.JobSet.Title}.",
            input.UseLlmEnhancement ? input.SelectedModel : null,
            Sequence: 0);

        var state = BeginOperation(input.JobSet.Id, initialSnapshot);

        workspace.MarkJobSetRunning(input.JobSet.Id, "Fit review is running for this tab.", JobSetSubtask.FitReview);
        operations.BeginLlmOperation(initialSnapshot.Message, initialSnapshot.Detail);

        _ = RunFitReviewAnalysisAsync(state, input);

        return BuildStartResult(operationId);
    }

    public LlmOperationStartResult StartRefreshAllAnalysis(StartRefreshAllOperationRequest request)
    {
        var input = CaptureRefreshAllInput(request);
        var operationId = $"refresh-all-{Guid.NewGuid():N}";
        var startedAt = timeProvider.GetUtcNow();
        var initialSnapshot = new LlmOperationSnapshot(
            operationId,
            "refresh-all",
            "running",
            input.JobSet.Id,
            startedAt,
            startedAt,
            "Refreshing all analysis",
            $"Refreshing research, fit review, and technology gap data for {input.JobSet.Title}.",
            input.SelectedModel,
            Sequence: 0);

        var state = BeginOperation(input.JobSet.Id, initialSnapshot);

        workspace.MarkLlmWorkStarted();
        workspace.MarkJobSetRunning(input.JobSet.Id, "Refresh all analysis is running for this tab.", JobSetSubtask.JobContext);
        operations.BeginLlmOperation(initialSnapshot.Message, initialSnapshot.Detail);

        _ = RunRefreshAllAnalysisAsync(state, input);

        return BuildStartResult(operationId);
    }

    public LlmOperationSnapshot? GetSnapshot(string operationId)
        => operationsById.TryGetValue(operationId, out var state) ? state.GetSnapshot() : null;

    public async IAsyncEnumerable<LlmOperationEvent> StreamEventsAsync(
        string operationId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!operationsById.TryGetValue(operationId, out var state))
        {
            throw new InvalidOperationException($"The operation '{operationId}' was not found.");
        }

        yield return new LlmOperationEvent("snapshot", state.GetSnapshot());

        while (await state.Events.Reader.WaitToReadAsync(cancellationToken))
        {
            while (state.Events.Reader.TryRead(out var operationEvent))
            {
                yield return operationEvent;
            }
        }
    }

    public bool Cancel(string operationId)
    {
        if (!operationsById.TryGetValue(operationId, out var state))
        {
            return false;
        }

        if (state.GetSnapshot().IsTerminal)
        {
            return false;
        }

        state.Cancellation.Cancel();
        return true;
    }

    private LlmOperationState BeginOperation(string jobSetId, LlmOperationSnapshot initialSnapshot)
    {
        var operationTimeout = GetOperationTimeout();
        var state = new LlmOperationState(initialSnapshot, operationTimeout is null ? null : initialSnapshot.StartedAt + operationTimeout.Value);

        if (operationTimeout is not null)
        {
            state.StartTimeoutCountdown(operationTimeout.Value);
        }

        lock (gate)
        {
            if (activeOperationsByJobSet.TryGetValue(jobSetId, out var activeOperationId)
                && operationsById.TryGetValue(activeOperationId, out var activeOperation)
                && !activeOperation.GetSnapshot().IsTerminal)
            {
                throw new InvalidOperationException("An LLM operation is already running for this job set.");
            }

            operationsById[initialSnapshot.OperationId] = state;
            activeOperationsByJobSet[jobSetId] = initialSnapshot.OperationId;
        }

        return state;
    }

    private TimeSpan? GetOperationTimeout()
        => ollamaOptions.MaxOperationSeconds > 0 ? TimeSpan.FromSeconds(ollamaOptions.MaxOperationSeconds) : null;

    private bool IsTimedOut(LlmOperationState state)
        => state.TimedOut || (state.TimeoutAt is { } timeoutAt && timeProvider.GetUtcNow() >= timeoutAt);

    private string BuildTimeoutDetail(string operationLabel)
    {
        var timeout = GetOperationTimeout();
        if (timeout is null)
        {
            return $"{operationLabel} timed out because it exceeded the configured inactivity window or the request stalled.";
        }

        return $"{operationLabel} exceeded the configured {FormatDuration(timeout.Value)} limit. Lower the thinking level, choose a faster model, or disable the hard operation cap and try again.";
    }

    private static string FormatDuration(TimeSpan duration)
        => duration.TotalMinutes >= 1
            ? duration.TotalMinutes % 1 == 0
                ? $"{duration.TotalMinutes:0} minute(s)"
                : $"{duration.TotalMinutes:0.#} minute(s)"
            : $"{Math.Max(1, duration.TotalSeconds):0} second(s)";

    private static LlmOperationStartResult BuildStartResult(string operationId)
        => new(
            operationId,
            $"/api/llm/operations/{operationId}",
            $"/api/llm/operations/{operationId}/events",
            $"/api/llm/operations/{operationId}/cancel");

    private DraftGenerationOperationInput CaptureDraftGenerationInput(StartDraftGenerationOperationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.JobSetId))
        {
            throw new InvalidOperationException("A job set id is required.");
        }

        if (!workspace.IsLlmReady)
        {
            throw new InvalidOperationException("Complete Start / Setup first: check Ollama, choose a session model, and choose a thinking level.");
        }

        var candidateProfile = workspace.CandidateProfile
            ?? throw new InvalidOperationException("Load a LinkedIn profile before generating drafts.");

        var jobSet = workspace.JobSets.FirstOrDefault(job => job.Id.Equals(request.JobSetId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"The job set '{request.JobSetId}' was not found.");

        if (jobSet.JobPosting is null)
        {
            throw new InvalidOperationException("Analyze a target job before generating drafts.");
        }

        var documentKinds = ParseDocumentKinds(request.DocumentKinds);

        return new DraftGenerationOperationInput(
            jobSet,
            candidateProfile,
            workspace.ApplicantDifferentiatorProfile,
            workspace.SelectedLlmModel,
            workspace.SelectedThinkingLevel,
            documentKinds,
            request.ExportToFiles,
            NormalizeContact(request.PersonalContact));
    }

    private static PersonalContactInfo? NormalizeContact(PersonalContactInfo? contact)
    {
        if (contact is null || !contact.HasAnyValue)
        {
            return null;
        }

        return new PersonalContactInfo(
            string.IsNullOrWhiteSpace(contact.Email) ? null : contact.Email.Trim(),
            string.IsNullOrWhiteSpace(contact.Phone) ? null : contact.Phone.Trim(),
            string.IsNullOrWhiteSpace(contact.LinkedInUrl) ? null : contact.LinkedInUrl.Trim(),
            string.IsNullOrWhiteSpace(contact.City) ? null : contact.City.Trim());
    }

    private JobContextOperationInput CaptureJobContextInput(StartJobContextOperationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.JobSetId))
        {
            throw new InvalidOperationException("A job set id is required.");
        }

        if (!workspace.IsLlmReady)
        {
            throw new InvalidOperationException("Complete Start / Setup first: check Ollama, choose a session model, and choose a thinking level before parsing the job or company context.");
        }

        var jobSet = workspace.JobSets.FirstOrDefault(job => job.Id.Equals(request.JobSetId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"The job set '{request.JobSetId}' was not found.");

        if (jobSet.InputMode == JobSetInputMode.PasteText)
        {
            if (string.IsNullOrWhiteSpace(jobSet.JobPostingText))
            {
                throw new InvalidOperationException("Paste the job posting text before analyzing.");
            }

            return new JobContextOperationInput(
                jobSet,
                workspace.SelectedLlmModel,
                workspace.SelectedThinkingLevel,
                JobUri: null,
                CompanyUrls: Array.Empty<Uri>(),
                JobPostingText: jobSet.JobPostingText,
                CompanyContextText: jobSet.CompanyContextText);
        }

        if (!Uri.TryCreate(jobSet.JobUrl, UriKind.Absolute, out var jobUri))
        {
            throw new InvalidOperationException("Please enter a valid absolute job URL.");
        }

        var companyUrls = jobSet.CompanyUrlsText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null)
            .Where(static uri => uri is not null)
            .Cast<Uri>()
            .ToArray();

        return new JobContextOperationInput(
            jobSet,
            workspace.SelectedLlmModel,
            workspace.SelectedThinkingLevel,
            jobUri,
            companyUrls,
            JobPostingText: null,
            CompanyContextText: null);
    }

    private TechnologyGapOperationInput CaptureTechnologyGapInput(StartTechnologyGapOperationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.JobSetId))
        {
            throw new InvalidOperationException("A job set id is required.");
        }

        if (!workspace.IsLlmReady)
        {
            throw new InvalidOperationException("Load a profile and complete Start / Setup before running the technology gap check.");
        }

        var candidateProfile = workspace.CandidateProfile
            ?? throw new InvalidOperationException("Load a profile and complete Start / Setup before running the technology gap check.");

        var jobSet = workspace.JobSets.FirstOrDefault(job => job.Id.Equals(request.JobSetId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"The job set '{request.JobSetId}' was not found.");

        if (jobSet.JobPosting is null)
        {
            throw new InvalidOperationException("Load a profile and analyze a target job before running the technology gap check.");
        }

        return new TechnologyGapOperationInput(
            jobSet,
            candidateProfile,
            workspace.SelectedLlmModel,
            workspace.SelectedThinkingLevel);
    }

    private FitReviewOperationInput CaptureFitReviewInput(StartFitReviewOperationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.JobSetId))
        {
            throw new InvalidOperationException("A job set id is required.");
        }

        var candidateProfile = workspace.CandidateProfile
            ?? throw new InvalidOperationException("Load a profile and analyze a target job before running the fit review.");

        var jobSet = workspace.JobSets.FirstOrDefault(job => job.Id.Equals(request.JobSetId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"The job set '{request.JobSetId}' was not found.");

        if (jobSet.JobPosting is null)
        {
            throw new InvalidOperationException("Load a profile and analyze a target job before running the fit review.");
        }

        return new FitReviewOperationInput(
            jobSet,
            candidateProfile,
            workspace.SelectedLlmModel,
            workspace.SelectedThinkingLevel,
            workspace.IsLlmReady);
    }

    private RefreshAllOperationInput CaptureRefreshAllInput(StartRefreshAllOperationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.JobSetId))
        {
            throw new InvalidOperationException("A job set id is required.");
        }

        if (!workspace.IsLlmReady)
        {
            throw new InvalidOperationException("Complete Start / Setup first: check Ollama, choose a session model, and choose a thinking level before refreshing all analysis.");
        }

        var jobSet = workspace.JobSets.FirstOrDefault(job => job.Id.Equals(request.JobSetId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"The job set '{request.JobSetId}' was not found.");

        if (jobSet.InputMode == JobSetInputMode.PasteText)
        {
            if (string.IsNullOrWhiteSpace(jobSet.JobPostingText))
            {
                throw new InvalidOperationException("Paste the job posting text before refreshing all analysis.");
            }

            return new RefreshAllOperationInput(
                jobSet,
                workspace.CandidateProfile,
                workspace.SelectedLlmModel,
                workspace.SelectedThinkingLevel,
                JobUri: null,
                CompanyUrls: Array.Empty<Uri>(),
                JobPostingText: jobSet.JobPostingText,
                CompanyContextText: jobSet.CompanyContextText,
                InputMode: JobSetInputMode.PasteText);
        }

        if (!Uri.TryCreate(jobSet.JobUrl, UriKind.Absolute, out var jobUri))
        {
            throw new InvalidOperationException("Please enter a valid absolute job URL.");
        }

        Uri[] companyUrls;
        if (string.IsNullOrWhiteSpace(jobSet.CompanyUrlsText))
        {
            companyUrls = Array.Empty<Uri>();
        }
        else
        {
            companyUrls = jobSet.CompanyUrlsText
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null)
                .Where(static uri => uri is not null)
                .Cast<Uri>()
                .ToArray();

            if (companyUrls.Length == 0)
            {
                throw new InvalidOperationException("Add at least one absolute company URL or clear the company context field.");
            }
        }

        return new RefreshAllOperationInput(
            jobSet,
            workspace.CandidateProfile,
            workspace.SelectedLlmModel,
            workspace.SelectedThinkingLevel,
            jobUri,
            companyUrls,
            JobPostingText: null,
            CompanyContextText: null,
            InputMode: JobSetInputMode.LinkToUrls);
    }

    private async Task RunDraftGenerationAsync(LlmOperationState state, DraftGenerationOperationInput input)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var fitRefreshService = scope.ServiceProvider.GetRequiredService<JobFitWorkspaceRefreshService>();
            var fitEnhancementService = scope.ServiceProvider.GetRequiredService<LlmFitEnhancementService>();
            var draftGenerationService = scope.ServiceProvider.GetRequiredService<IDraftGenerationService>();
            var currentJobSet = GetJobSetOrThrow(input.JobSet.Id);
            var fitReviewFingerprint = BuildFitReviewFingerprint(
                input.CandidateProfile,
                currentJobSet,
                workspace.ApplicantDifferentiatorProfile,
                input.SelectedModel,
                input.SelectedThinkingLevel,
                useLlmEnhancement: true);

            if (workspace.IsJobSetFitReviewCurrent(input.JobSet.Id, fitReviewFingerprint, requiresLlmEnhancement: true))
            {
                PublishStage(
                    state,
                    input.JobSet.Id,
                    "Reusing current fit review",
                    $"Fit review is already current for {input.JobSet.Title}; skipping the preflight refresh.",
                    activeSubtask: JobSetSubtask.FitReview);
            }
            else
            {
                try
                {
                    await RefreshFitReviewCoreAsync(
                        state,
                        input.JobSet.Id,
                        input.JobSet.Title,
                        input.CandidateProfile,
                        input.SelectedModel,
                        input.SelectedThinkingLevel,
                        useLlmEnhancement: true,
                        resetEvidenceSelections: false,
                        fitReviewFingerprint,
                        fitRefreshService,
                        fitEnhancementService,
                        state.Cancellation.Token);
                }
                catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    operations.Error("Draft generation pre-flight fit review failed.", exception.Message);
                }
            }

            var generationJobSet = GetJobSetOrThrow(input.JobSet.Id);

            PublishStage(
                state,
                input.JobSet.Id,
                "Generating targeted drafts",
                $"{input.DocumentKinds.Count} document type(s) queued for {input.JobSet.Title}.",
                input.SelectedModel,
                JobSetSubtask.DraftGeneration);

            var result = await draftGenerationService.GenerateAsync(
                new DraftGenerationRequest(
                    input.CandidateProfile,
                    generationJobSet.JobPosting!,
                    generationJobSet.CompanyProfile?.Summary,
                    generationJobSet.AdditionalInstructions,
                    input.DocumentKinds,
                    input.ExportToFiles,
                    input.SelectedModel,
                    input.SelectedThinkingLevel,
                    generationJobSet.OutputFolderName,
                    generationJobSet.OutputLanguage,
                    generationJobSet.JobFitAssessment,
                    input.ApplicantDifferentiatorProfile,
                    generationJobSet.EvidenceSelection,
                    generationJobSet.TechnologyGapAssessment,
                    generationJobSet.InputLanguage.ToPromptHint(),
                    input.PersonalContact),
                update => HandleProgress(state, input.JobSet.Id, update),
                state.Cancellation.Token);

            workspace.SetJobSetGeneratedDocuments(input.JobSet.Id, result.Documents, result.Exports);

            var completedSnapshot = state.GetSnapshot() with
            {
                Status = "completed",
                UpdatedAt = timeProvider.GetUtcNow(),
                Message = "Draft generation completed",
                Detail = $"Generated {result.Documents.Count} document(s) for {input.JobSet.Title}.",
                Completed = true
            };

            Publish(state, "completed", completedSnapshot);
            operations.Success("Draft generation completed.", completedSnapshot.Detail);
        }
        catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
        {
            if (IsTimedOut(state))
            {
                var detail = BuildTimeoutDetail("Draft generation");
                workspace.MarkJobSetFailed(input.JobSet.Id, detail);

                var timedOutSnapshot = state.GetSnapshot() with
                {
                    Status = "failed",
                    UpdatedAt = timeProvider.GetUtcNow(),
                    Message = "Draft generation timed out",
                    Detail = detail,
                    Error = detail
                };

                Publish(state, "failed", timedOutSnapshot);
                operations.Error("Draft generation timed out.", detail);
                return;
            }

            workspace.ResetJobSetProgress(input.JobSet.Id, "Draft generation was cancelled for this job set.");

            var cancelledSnapshot = state.GetSnapshot() with
            {
                Status = "cancelled",
                UpdatedAt = timeProvider.GetUtcNow(),
                Message = "Draft generation cancelled",
                Detail = $"Generation was cancelled for {input.JobSet.Title}.",
                Cancelled = true
            };

            Publish(state, "cancelled", cancelledSnapshot);
            operations.Info("Draft generation cancelled.", cancelledSnapshot.Detail);
        }
        catch (Exception exception)
        {
            workspace.MarkJobSetFailed(input.JobSet.Id, $"Draft generation failed: {exception.Message}");

            var failedSnapshot = state.GetSnapshot() with
            {
                Status = "failed",
                UpdatedAt = timeProvider.GetUtcNow(),
                Message = "Draft generation failed",
                Detail = exception.Message,
                Error = exception.Message
            };

            Publish(state, "failed", failedSnapshot);
            operations.Error("Draft generation failed.", exception.Message);
        }
        finally
        {
            Complete(state);

            lock (gate)
            {
                activeOperationsByJobSet.Remove(input.JobSet.Id);
            }
        }
    }

    private async Task RunJobContextAnalysisAsync(LlmOperationState state, JobContextOperationInput input)
    {
        var companyContextWarnings = new List<OperationWarning>();

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var jobResearchService = scope.ServiceProvider.GetRequiredService<IJobResearchService>();

            JobPostingAnalysis analysis;
            CompanyResearchProfile? companyProfile = null;
            var hadExistingCompanyProfile = input.JobSet.CompanyProfile is not null;

            if (input.JobSet.InputMode == JobSetInputMode.PasteText)
            {
                analysis = await jobResearchService.AnalyzeTextAsync(
                    input.JobPostingText!,
                    input.SelectedModel,
                    input.SelectedThinkingLevel,
                    update => HandleProgress(state, input.JobSet.Id, update),
                    input.JobSet.InputLanguage.ToPromptHint(),
                    state.Cancellation.Token);

                workspace.SetJobSetJobPosting(input.JobSet.Id, analysis);

                if (!string.IsNullOrWhiteSpace(input.CompanyContextText))
                {
                    try
                    {
                        companyProfile = await jobResearchService.BuildCompanyProfileFromTextAsync(
                            input.CompanyContextText,
                            input.SelectedModel,
                            input.SelectedThinkingLevel,
                            update => HandleProgress(state, input.JobSet.Id, update),
                            input.JobSet.InputLanguage.ToPromptHint(),
                            state.Cancellation.Token);

                        workspace.SetJobSetCompanyProfile(input.JobSet.Id, companyProfile);
                    }
                    catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        companyContextWarnings.Add(CreateWarning(
                            "CompanyContextRefreshFailed",
                            "company-context-refresh",
                            $"Company context could not be refreshed from pasted text: {exception.Message}"));
                    }
                }
            }
            else
            {
                analysis = await jobResearchService.AnalyzeAsync(
                    input.JobUri!,
                    input.SelectedModel,
                    input.SelectedThinkingLevel,
                    update => HandleProgress(state, input.JobSet.Id, update),
                    input.JobSet.InputLanguage.ToPromptHint(),
                    state.Cancellation.Token);

                workspace.SetJobSetJobPosting(input.JobSet.Id, analysis);

                var companyUrls = input.CompanyUrls;
                if (companyUrls.Count == 0)
                {
                    var companyUrlDiscoveryFailed = false;

                    try
                    {
                        companyUrls = await jobResearchService.DiscoverCompanyContextUrlsAsync(
                            input.JobUri!,
                            analysis.CompanyName,
                            state.Cancellation.Token);

                        if (companyUrls.Count > 0)
                        {
                            workspace.UpdateJobSetInputs(
                                input.JobSet.Id,
                                input.JobSet.JobUrl,
                                string.Join(Environment.NewLine, companyUrls.Select(static uri => uri.AbsoluteUri)),
                                input.JobSet.JobPostingText,
                                input.JobSet.CompanyContextText);
                        }
                    }
                    catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        companyUrlDiscoveryFailed = true;
                        companyContextWarnings.Add(CreateWarning(
                            "CompanyUrlDiscoveryFailed",
                            "company-url-discovery",
                            $"Automatic company URL discovery failed: {exception.Message}"));
                        companyUrls = Array.Empty<Uri>();
                    }

                    if (!companyUrlDiscoveryFailed && companyUrls.Count == 0)
                    {
                        companyContextWarnings.Add(CreateWarning(
                            "NoCompanyUrlsDiscovered",
                            "company-url-discovery",
                            "No public company URLs were discovered from the job posting."));
                    }
                }

                if (companyUrls.Count > 0)
                {
                    try
                    {
                        var companyProfileResult = await jobResearchService.BuildCompanyProfileAsync(
                            companyUrls,
                            input.SelectedModel,
                            input.SelectedThinkingLevel,
                            update => HandleProgress(state, input.JobSet.Id, update),
                            input.JobSet.InputLanguage.ToPromptHint(),
                            state.Cancellation.Token);

                        companyProfile = companyProfileResult.Profile;
                        workspace.SetJobSetCompanyProfile(input.JobSet.Id, companyProfile);

                        if (companyProfileResult.HasSkippedSources)
                        {
                            companyContextWarnings.Add(CreateWarning(
                                "SomeCompanyUrlsSkipped",
                                "company-context-refresh",
                                $"Company context used {companyProfileResult.SuccessfulSourceCount} readable source page(s) and skipped {companyProfileResult.SkippedSourceCount}. {BuildSkippedSourceSummary(companyProfileResult.SkippedSourceDetails)}",
                                companyProfileResult.AttemptedSourceCount,
                                companyProfileResult.SuccessfulSourceCount,
                                companyProfileResult.SkippedSourceCount));
                        }
                    }
                    catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        companyContextWarnings.Add(CreateWarning(
                            "CompanyContextRefreshFailed",
                            "company-context-refresh",
                            $"Company context could not be refreshed: {exception.Message}"));
                    }
                }
            }

            var detail = BuildJobContextCompletionDetail(companyProfile is not null, companyContextWarnings, hadExistingCompanyProfile);

            workspace.ResetJobSetProgress(input.JobSet.Id, detail);

            var completedSnapshot = state.GetSnapshot() with
            {
                Status = "completed",
                UpdatedAt = timeProvider.GetUtcNow(),
                Message = companyContextWarnings.Count == 0 ? "Job and company context updated" : "Job context updated with company warning",
                Detail = detail,
                Completed = true,
                Warnings = companyContextWarnings.Count == 0 ? null : companyContextWarnings.ToArray()
            };

            workspace.SetJobSetLatestIngestionWarnings(input.JobSet.Id, completedSnapshot.WarningItems);
            Publish(state, "completed", completedSnapshot);
            operations.Success(companyContextWarnings.Count == 0 ? "Job and company context updated." : "Job context updated with company warning.", detail);
        }
        catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
        {
            if (IsTimedOut(state))
            {
                var detail = BuildTimeoutDetail("Job and company context analysis");
                workspace.MarkJobSetFailed(input.JobSet.Id, detail);

                var timedOutSnapshot = state.GetSnapshot() with
                {
                    Status = "failed",
                    UpdatedAt = timeProvider.GetUtcNow(),
                    Message = "Job and company context timed out",
                    Detail = detail,
                    Error = detail,
                    Warnings = companyContextWarnings.Count == 0 ? null : companyContextWarnings.ToArray()
                };

                workspace.SetJobSetLatestIngestionWarnings(input.JobSet.Id, timedOutSnapshot.WarningItems);
                Publish(state, "failed", timedOutSnapshot);
                operations.Error("Job and company context timed out.", detail);
                return;
            }

            workspace.ResetJobSetProgress(input.JobSet.Id, "Job and company context analysis was cancelled for this job set.");

            var cancelledSnapshot = state.GetSnapshot() with
            {
                Status = "cancelled",
                UpdatedAt = timeProvider.GetUtcNow(),
                Message = "Job and company context cancelled",
                Detail = $"Analysis was cancelled for {input.JobSet.Title}.",
                Cancelled = true,
                Warnings = companyContextWarnings.Count == 0 ? null : companyContextWarnings.ToArray()
            };

            workspace.SetJobSetLatestIngestionWarnings(input.JobSet.Id, cancelledSnapshot.WarningItems);
            Publish(state, "cancelled", cancelledSnapshot);
            operations.Info("Job and company context cancelled.", cancelledSnapshot.Detail);
        }
        catch (Exception exception)
        {
            workspace.MarkJobSetFailed(input.JobSet.Id, $"Job and company context analysis failed: {exception.Message}");

            var failedSnapshot = state.GetSnapshot() with
            {
                Status = "failed",
                UpdatedAt = timeProvider.GetUtcNow(),
                Message = "Job and company context failed",
                Detail = exception.Message,
                Error = exception.Message,
                Warnings = companyContextWarnings.Count == 0 ? null : companyContextWarnings.ToArray()
            };

            workspace.SetJobSetLatestIngestionWarnings(input.JobSet.Id, failedSnapshot.WarningItems);
            Publish(state, "failed", failedSnapshot);
            operations.Error("Job and company context failed.", exception.Message);
        }
        finally
        {
            Complete(state);

            lock (gate)
            {
                activeOperationsByJobSet.Remove(input.JobSet.Id);
            }
        }
    }

    private static string BuildJobContextCompletionDetail(bool hasFreshCompanyProfile, IReadOnlyList<OperationWarning> warnings, bool hadExistingCompanyProfile)
    {
        var detail = hasFreshCompanyProfile
            ? "The job tab now has the latest target role context and company context. Fit review and generation are still idle until you run them."
            : "The job tab now has the latest target role context. Fit review and generation are still idle until you run them.";

        if (warnings.Count == 0)
        {
            return detail;
        }

        var retentionDetail = hadExistingCompanyProfile
            ? "Existing company context was kept."
            : "No company context is available yet.";

        return $"{detail} {retentionDetail} Warning: {BuildWarningSummary(warnings)}";
    }

    private async Task RunTechnologyGapAnalysisAsync(LlmOperationState state, TechnologyGapOperationInput input)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var gapAnalysisService = scope.ServiceProvider.GetRequiredService<LlmTechnologyGapAnalysisService>();

            var assessment = await gapAnalysisService.AnalyzeAsync(
                input.CandidateProfile,
                input.JobSet.JobPosting!,
                input.JobSet.CompanyProfile,
                input.SelectedModel,
                input.SelectedThinkingLevel,
                update => HandleProgress(state, input.JobSet.Id, update),
                input.JobSet.InputLanguage.ToPromptHint(),
                state.Cancellation.Token);

            workspace.SetJobSetTechnologyGapAssessment(input.JobSet.Id, assessment);
            workspace.ResetJobSetProgress(input.JobSet.Id, "Technology gap analysis completed. Markdown generation not started for this job set.");

            var detail = assessment.HasGaps
                ? $"Detected {assessment.DetectedTechnologies.Count} technology signal(s) with {assessment.PossiblyUnderrepresentedTechnologies.Count} potential gap(s)."
                : $"Detected {assessment.DetectedTechnologies.Count} technology signal(s) with no obvious gaps.";
            var completedSnapshot = state.GetSnapshot() with
            {
                Status = "completed",
                UpdatedAt = timeProvider.GetUtcNow(),
                Message = "Technology gap analysis completed",
                Detail = detail,
                Completed = true
            };

            Publish(state, "completed", completedSnapshot);
            operations.Success("Technology gap analysis completed.", detail);
        }
        catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
        {
            if (IsTimedOut(state))
            {
                var detail = BuildTimeoutDetail("Technology gap analysis");
                workspace.MarkJobSetFailed(input.JobSet.Id, detail);

                var timedOutSnapshot = state.GetSnapshot() with
                {
                    Status = "failed",
                    UpdatedAt = timeProvider.GetUtcNow(),
                    Message = "Technology gap analysis timed out",
                    Detail = detail,
                    Error = detail
                };

                Publish(state, "failed", timedOutSnapshot);
                operations.Error("Technology gap analysis timed out.", detail);
                return;
            }

            workspace.ResetJobSetProgress(input.JobSet.Id, "Technology gap analysis was cancelled for this job set.");

            var cancelledSnapshot = state.GetSnapshot() with
            {
                Status = "cancelled",
                UpdatedAt = timeProvider.GetUtcNow(),
                Message = "Technology gap analysis cancelled",
                Detail = $"Technology gap analysis was cancelled for {input.JobSet.Title}.",
                Cancelled = true
            };

            Publish(state, "cancelled", cancelledSnapshot);
            operations.Info("Technology gap analysis cancelled.", cancelledSnapshot.Detail);
        }
        catch (Exception exception)
        {
            workspace.MarkJobSetFailed(input.JobSet.Id, $"Technology gap analysis failed: {exception.Message}");

            var failedSnapshot = state.GetSnapshot() with
            {
                Status = "failed",
                UpdatedAt = timeProvider.GetUtcNow(),
                Message = "Technology gap analysis failed",
                Detail = exception.Message,
                Error = exception.Message
            };

            Publish(state, "failed", failedSnapshot);
            operations.Error("Technology gap analysis failed.", exception.Message);
        }
        finally
        {
            Complete(state);

            lock (gate)
            {
                activeOperationsByJobSet.Remove(input.JobSet.Id);
            }
        }
    }

    private async Task RunFitReviewAnalysisAsync(LlmOperationState state, FitReviewOperationInput input)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var fitRefreshService = scope.ServiceProvider.GetRequiredService<JobFitWorkspaceRefreshService>();
            var fitEnhancementService = scope.ServiceProvider.GetRequiredService<LlmFitEnhancementService>();
            var currentJobSet = GetJobSetOrThrow(input.JobSet.Id);
            var fitReviewFingerprint = BuildFitReviewFingerprint(
                input.CandidateProfile,
                currentJobSet,
                workspace.ApplicantDifferentiatorProfile,
                input.SelectedModel,
                input.SelectedThinkingLevel,
                input.UseLlmEnhancement);

            var assessment = await RefreshFitReviewCoreAsync(
                state,
                input.JobSet.Id,
                input.JobSet.Title,
                input.CandidateProfile,
                input.SelectedModel,
                input.SelectedThinkingLevel,
                input.UseLlmEnhancement,
                resetEvidenceSelections: true,
                fitReviewFingerprint,
                fitRefreshService,
                fitEnhancementService,
                state.Cancellation.Token);

            workspace.ResetJobSetProgress(input.JobSet.Id, "Fit review updated for this job tab. Markdown generation not started for this tab.");

            var completedSnapshot = state.GetSnapshot() with
            {
                Status = "completed",
                UpdatedAt = timeProvider.GetUtcNow(),
                Message = "Fit review updated",
                Detail = BuildFitReviewDetail(assessment),
                Completed = true,
                Sequence = state.GetSnapshot().Sequence + 1
            };

            Publish(state, "completed", completedSnapshot);
            operations.Success("Fit review updated.", completedSnapshot.Detail);
        }
        catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
        {
            if (IsTimedOut(state))
            {
                var detail = BuildTimeoutDetail("Fit review");
                workspace.MarkJobSetFailed(input.JobSet.Id, detail);

                var timedOutSnapshot = state.GetSnapshot() with
                {
                    Status = "failed",
                    UpdatedAt = timeProvider.GetUtcNow(),
                    Message = "Fit review timed out",
                    Detail = detail,
                    Error = detail,
                };

                Publish(state, "failed", timedOutSnapshot);
                operations.Error("Fit review timed out.", detail);
                return;
            }

            workspace.ResetJobSetProgress(input.JobSet.Id, "Fit review was cancelled for this job set.");

            var cancelledSnapshot = state.GetSnapshot() with
            {
                Status = "cancelled",
                UpdatedAt = timeProvider.GetUtcNow(),
                Message = "Fit review cancelled",
                Detail = $"Fit review was cancelled for {input.JobSet.Title}.",
                Cancelled = true,
                Sequence = state.GetSnapshot().Sequence + 1
            };

            Publish(state, "cancelled", cancelledSnapshot);
            operations.Info("Fit review cancelled.", cancelledSnapshot.Detail);
        }
        catch (Exception exception)
        {
            workspace.MarkJobSetFailed(input.JobSet.Id, $"Fit review failed: {exception.Message}");

            var failedSnapshot = state.GetSnapshot() with
            {
                Status = "failed",
                UpdatedAt = timeProvider.GetUtcNow(),
                Message = "Fit review failed",
                Detail = exception.Message,
                Error = exception.Message,
                Sequence = state.GetSnapshot().Sequence + 1
            };

            Publish(state, "failed", failedSnapshot);
            operations.Error("Fit review failed.", exception.Message);
        }
        finally
        {
            Complete(state);

            lock (gate)
            {
                activeOperationsByJobSet.Remove(input.JobSet.Id);
            }
        }
    }

    private async Task RunRefreshAllAnalysisAsync(LlmOperationState state, RefreshAllOperationInput input)
    {
        var maxAttempts = Math.Max(1, ollamaOptions.RetryAttempts);
        var retryDelay = TimeSpan.FromSeconds(Math.Max(0, ollamaOptions.RetryDelaySeconds));

        try
        {
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (attempt > 1)
                {
                    await Task.Delay(retryDelay, state.Cancellation.Token);
                    workspace.MarkJobSetRunning(
                        input.JobSet.Id,
                        $"Retrying (attempt {attempt} of {maxAttempts}).",
                        JobSetSubtask.JobContext);
                    PublishStage(
                        state,
                        input.JobSet.Id,
                        "Retrying analysis",
                        $"Retrying analysis for {input.JobSet.Title} (attempt {attempt} of {maxAttempts}).",
                        input.SelectedModel,
                        JobSetSubtask.JobContext);
                }

                try
                {
                    await ExecuteRefreshAllAnalysisAsync(state, input);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception) when (attempt < maxAttempts)
                {
                    // transient failure — will retry after delay
                }
            }
        }
        catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
        {
            if (IsTimedOut(state))
            {
                var detail = BuildTimeoutDetail("Refresh all analysis");
                workspace.MarkJobSetFailed(input.JobSet.Id, detail);

                var timedOutSnapshot = state.GetSnapshot() with
                {
                    Status = "failed",
                    UpdatedAt = timeProvider.GetUtcNow(),
                    Message = "Refresh all analysis timed out",
                    Detail = detail,
                    Error = detail,
                    Warnings = state.GetSnapshot().Warnings,
                };

                workspace.SetJobSetLatestIngestionWarnings(input.JobSet.Id, timedOutSnapshot.WarningItems);
                Publish(state, "failed", timedOutSnapshot);
                operations.Error("Refresh all analysis timed out.", detail);
                return;
            }

            workspace.ResetJobSetProgress(input.JobSet.Id, "Refresh all analysis was cancelled for this job set.");

            var cancelledSnapshot = state.GetSnapshot() with
            {
                Status = "cancelled",
                UpdatedAt = timeProvider.GetUtcNow(),
                Message = "Refresh all analysis cancelled",
                Detail = $"Refresh all analysis was cancelled for {input.JobSet.Title}.",
                Cancelled = true,
                Warnings = state.GetSnapshot().Warnings,
                Sequence = state.GetSnapshot().Sequence + 1
            };

            workspace.SetJobSetLatestIngestionWarnings(input.JobSet.Id, cancelledSnapshot.WarningItems);
            Publish(state, "cancelled", cancelledSnapshot);
            operations.Info("Refresh all analysis cancelled.", cancelledSnapshot.Detail);
        }
        catch (Exception exception)
        {
            workspace.MarkJobSetFailed(input.JobSet.Id, $"Refresh all analysis failed: {exception.Message}");

            var failedSnapshot = state.GetSnapshot() with
            {
                Status = "failed",
                UpdatedAt = timeProvider.GetUtcNow(),
                Message = "Refresh all analysis failed",
                Detail = exception.Message,
                Error = exception.Message,
                Warnings = state.GetSnapshot().Warnings,
                Sequence = state.GetSnapshot().Sequence + 1
            };

            workspace.SetJobSetLatestIngestionWarnings(input.JobSet.Id, failedSnapshot.WarningItems);
            Publish(state, "failed", failedSnapshot);
            operations.Error("Refresh all analysis failed.", exception.Message);
        }
        finally
        {
            Complete(state);

            lock (gate)
            {
                activeOperationsByJobSet.Remove(input.JobSet.Id);
            }
        }
    }

    private async Task ExecuteRefreshAllAnalysisAsync(LlmOperationState state, RefreshAllOperationInput input)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var jobResearchService = scope.ServiceProvider.GetRequiredService<IJobResearchService>();
        var fitRefreshService = scope.ServiceProvider.GetRequiredService<JobFitWorkspaceRefreshService>();
        var fitEnhancementService = scope.ServiceProvider.GetRequiredService<LlmFitEnhancementService>();
        var gapAnalysisService = scope.ServiceProvider.GetRequiredService<LlmTechnologyGapAnalysisService>();
        var companyContextWarnings = new List<OperationWarning>();

        PublishStage(
            state,
            input.JobSet.Id,
            "Analyzing job and company context",
            input.InputMode == JobSetInputMode.PasteText
                ? $"Refreshing pasted job text for {input.JobSet.Title}."
                : $"Refreshing {input.JobUri} for {input.JobSet.Title}.",
            input.SelectedModel,
            JobSetSubtask.JobContext);

        if (input.InputMode == JobSetInputMode.PasteText)
        {
            var analysis = await jobResearchService.AnalyzeTextAsync(
                input.JobPostingText!,
                input.SelectedModel,
                input.SelectedThinkingLevel,
                update => HandleProgress(state, input.JobSet.Id, update),
                input.JobSet.InputLanguage.ToPromptHint(),
                state.Cancellation.Token);

            workspace.SetJobSetJobPosting(input.JobSet.Id, analysis);

            if (!string.IsNullOrWhiteSpace(input.CompanyContextText))
            {
                try
                {
                    var companyProfile = await jobResearchService.BuildCompanyProfileFromTextAsync(
                        input.CompanyContextText,
                        input.SelectedModel,
                        input.SelectedThinkingLevel,
                        update => HandleProgress(state, input.JobSet.Id, update),
                        input.JobSet.InputLanguage.ToPromptHint(),
                        state.Cancellation.Token);

                    workspace.SetJobSetCompanyProfile(input.JobSet.Id, companyProfile);
                }
                catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    companyContextWarnings.Add(CreateWarning(
                        "CompanyContextRefreshFailed",
                        "company-context-refresh",
                        $"Company context could not be refreshed from pasted text: {exception.Message}"));
                }
            }
        }
        else
        {
            var analysis = await jobResearchService.AnalyzeAsync(
                input.JobUri!,
                input.SelectedModel,
                input.SelectedThinkingLevel,
                update => HandleProgress(state, input.JobSet.Id, update),
                input.JobSet.InputLanguage.ToPromptHint(),
                state.Cancellation.Token);

            workspace.SetJobSetJobPosting(input.JobSet.Id, analysis);

            if (input.CompanyUrls.Count > 0)
            {
                try
                {
                    var companyProfileResult = await jobResearchService.BuildCompanyProfileAsync(
                        input.CompanyUrls,
                        input.SelectedModel,
                        input.SelectedThinkingLevel,
                        update => HandleProgress(state, input.JobSet.Id, update),
                        input.JobSet.InputLanguage.ToPromptHint(),
                        state.Cancellation.Token);

                    var companyProfile = companyProfileResult.Profile;
                    workspace.SetJobSetCompanyProfile(input.JobSet.Id, companyProfile);

                    if (companyProfileResult.HasSkippedSources)
                    {
                        companyContextWarnings.Add(CreateWarning(
                            "SomeCompanyUrlsSkipped",
                            "company-context-refresh",
                            $"Company context used {companyProfileResult.SuccessfulSourceCount} readable source page(s) and skipped {companyProfileResult.SkippedSourceCount}. {BuildSkippedSourceSummary(companyProfileResult.SkippedSourceDetails)}",
                            companyProfileResult.AttemptedSourceCount,
                            companyProfileResult.SuccessfulSourceCount,
                            companyProfileResult.SkippedSourceCount));
                    }
                }
                catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    companyContextWarnings.Add(CreateWarning(
                        "CompanyContextRefreshFailed",
                        "company-context-refresh",
                        $"Company context could not be refreshed: {exception.Message}"));
                }
            }
        }

        state.Cancellation.Token.ThrowIfCancellationRequested();

        if (input.CandidateProfile is not null)
        {
            var latestJobSet = GetJobSetOrThrow(input.JobSet.Id);
            var fitReviewFingerprint = BuildFitReviewFingerprint(
                input.CandidateProfile,
                latestJobSet,
                workspace.ApplicantDifferentiatorProfile,
                input.SelectedModel,
                input.SelectedThinkingLevel,
                useLlmEnhancement: true);

            await RefreshFitReviewCoreAsync(
                state,
                input.JobSet.Id,
                input.JobSet.Title,
                input.CandidateProfile,
                input.SelectedModel,
                input.SelectedThinkingLevel,
                useLlmEnhancement: true,
                resetEvidenceSelections: true,
                fitReviewFingerprint,
                fitRefreshService,
                fitEnhancementService,
                state.Cancellation.Token);

            PublishStage(
                state,
                input.JobSet.Id,
                "Analyzing technology gaps",
                $"Comparing profile evidence against {input.JobSet.Title}.",
                input.SelectedModel,
                JobSetSubtask.TechnologyGap);

            var latestJobSetAfterFitReview = GetJobSetOrThrow(input.JobSet.Id);
            var technologyGapAssessment = await gapAnalysisService.AnalyzeAsync(
                input.CandidateProfile,
                latestJobSetAfterFitReview.JobPosting!,
                latestJobSetAfterFitReview.CompanyProfile,
                input.SelectedModel,
                input.SelectedThinkingLevel,
                update => HandleProgress(state, input.JobSet.Id, update),
                input.JobSet.InputLanguage.ToPromptHint(),
                state.Cancellation.Token);

            workspace.SetJobSetTechnologyGapAssessment(input.JobSet.Id, technologyGapAssessment);
            workspace.ResetJobSetProgress(input.JobSet.Id, "Job, company, fit review, technology gap and evidence are current for this tab.");
        }
        else
        {
            workspace.ResetJobSetProgress(
                input.JobSet.Id,
                BuildRefreshAllCompletionDetail(
                    hasCandidateProfile: false,
                    hasAvailableCompanyProfile: GetJobSetOrThrow(input.JobSet.Id).CompanyProfile is not null,
                    companyContextWarnings));
        }

        var completedDetail = BuildRefreshAllCompletionDetail(
            input.CandidateProfile is not null,
            GetJobSetOrThrow(input.JobSet.Id).CompanyProfile is not null,
            companyContextWarnings);
        var completedSnapshot = state.GetSnapshot() with
        {
            Status = "completed",
            UpdatedAt = timeProvider.GetUtcNow(),
            Message = companyContextWarnings.Count == 0 ? "All analysis refreshed" : "All analysis refreshed with company warning",
            Detail = completedDetail,
            Completed = true,
            Warnings = companyContextWarnings.Count == 0 ? null : companyContextWarnings.ToArray(),
            Sequence = state.GetSnapshot().Sequence + 1
        };

        workspace.SetJobSetLatestIngestionWarnings(input.JobSet.Id, completedSnapshot.WarningItems);
        Publish(state, "completed", completedSnapshot);
        operations.Success(companyContextWarnings.Count == 0 ? "All analysis refreshed." : "All analysis refreshed with company warning.", completedDetail);
    }

    private static string BuildRefreshAllCompletionDetail(bool hasCandidateProfile, bool hasAvailableCompanyProfile, IReadOnlyList<OperationWarning> warnings)
    {
        var detail = hasCandidateProfile
            ? "Job, company, fit review, technology gap and evidence are current for this tab."
            : "Job and company context are current. Load a profile to refresh fit review, evidence ranking, and technology gaps.";

        if (warnings.Count == 0)
        {
            return detail;
        }

        var retentionDetail = hasAvailableCompanyProfile
            ? "Existing company context was kept."
            : "The refresh continued without company context.";

        return $"{detail} {retentionDetail} Warning: {BuildWarningSummary(warnings)}";
    }

    private static OperationWarning CreateWarning(
        string code,
        string scope,
        string message,
        int? attemptedCount = null,
        int? successfulCount = null,
        int? skippedCount = null)
        => new(code, scope, message, attemptedCount, successfulCount, skippedCount);

    private static string BuildWarningSummary(IReadOnlyList<OperationWarning> warnings)
        => string.Join(" ", warnings.Select(static warning => warning.Message));

    private static string BuildSkippedSourceSummary(IReadOnlyList<string> skippedSourceDetails)
    {
        if (skippedSourceDetails.Count == 0)
        {
            return string.Empty;
        }

        var summarizedSources = skippedSourceDetails.Take(2).ToArray();
        var summary = string.Join(" ", summarizedSources);
        return skippedSourceDetails.Count > summarizedSources.Length
            ? $"Skipped source details: {summary} Additional skipped sources: {skippedSourceDetails.Count - summarizedSources.Length}."
            : $"Skipped source details: {summary}";
    }

    private async Task<JobFitAssessment> RefreshFitReviewCoreAsync(
        LlmOperationState state,
        string jobSetId,
        string jobSetTitle,
        CandidateProfile candidateProfile,
        string selectedModel,
        string selectedThinkingLevel,
        bool useLlmEnhancement,
        bool resetEvidenceSelections,
        string fitReviewFingerprint,
        JobFitWorkspaceRefreshService fitRefreshService,
        LlmFitEnhancementService fitEnhancementService,
        CancellationToken cancellationToken)
    {
        PublishStage(
            state,
            jobSetId,
            "Refreshing deterministic fit review",
            $"Refreshing deterministic fit signals for {jobSetTitle}.",
            activeSubtask: JobSetSubtask.FitReview);

        if (!fitRefreshService.RefreshJobSet(jobSetId, resetSelections: resetEvidenceSelections))
        {
            throw new InvalidOperationException("Load a profile and analyze a target job before running the fit review.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var refreshedJobSet = GetJobSetOrThrow(jobSetId);
        if (useLlmEnhancement && refreshedJobSet.JobFitAssessment.Requirements.Any(static requirement => requirement.Match != JobRequirementMatch.Strong))
        {
            workspace.MarkLlmWorkStarted();
            PublishStage(
                state,
                jobSetId,
                "Enhancing fit review with LLM",
                $"Semantic evidence matching is running for {jobSetTitle}.",
                selectedModel,
                JobSetSubtask.FitReview);

            var enhanced = await fitEnhancementService.EnhanceAsync(
                refreshedJobSet.JobFitAssessment,
                candidateProfile,
                refreshedJobSet.JobPosting!,
                refreshedJobSet.CompanyProfile,
                selectedModel,
                selectedThinkingLevel,
                update => HandleProgress(state, jobSetId, update),
                refreshedJobSet.InputLanguage.ToPromptHint(),
                cancellationToken);

            workspace.SetJobSetJobFitAssessment(jobSetId, enhanced);
            fitRefreshService.RefreshJobSetEvidence(jobSetId, resetSelections: resetEvidenceSelections);

            if (enhanced.IsLlmEnhanced)
            {
                var upgradedCount = enhanced.Requirements.Count(static requirement => requirement.IsLlmEnhanced);
                operations.Info($"LLM enhanced {upgradedCount} requirement(s).", "Semantic evidence matching found additional supporting evidence.");
            }
            else
            {
                operations.Info("LLM found no additional evidence.", "The semantic evidence pass did not identify upgrades beyond keyword matching.");
            }
        }

        var finalAssessment = GetJobSetOrThrow(jobSetId).JobFitAssessment;
        workspace.RecordJobSetFitReviewRefresh(jobSetId, fitReviewFingerprint, finalAssessment.IsLlmEnhanced);

        return finalAssessment;
    }

    private void HandleProgress(LlmOperationState state, string jobSetId, LlmProgressUpdate update)
    {
        operations.UpdateCurrent(update);
        workspace.MarkJobSetRunning(jobSetId, update.Detail ?? update.Message);

        var currentSnapshot = state.GetSnapshot();
        var nextSequence = update.Sequence > currentSnapshot.Sequence
            ? update.Sequence
            : currentSnapshot.Sequence + 1;

        var progressSnapshot = currentSnapshot with
        {
            Status = update.Completed ? "completed" : "running",
            UpdatedAt = timeProvider.GetUtcNow(),
            Message = update.Message,
            Detail = update.Detail,
            Model = update.Model,
            Elapsed = update.Elapsed,
            PromptTokens = update.PromptTokens,
            CompletionTokens = update.CompletionTokens,
            EstimatedRemaining = update.EstimatedRemaining,
            ResponseContent = update.ResponseContent,
            ThinkingPreview = update.ThinkingPreview,
            ThinkingContent = update.ThinkingContent,
            Sequence = nextSequence,
            Completed = update.Completed
        };

        Publish(state, update.Completed ? "progress-completed" : "progress", progressSnapshot);
    }

    private void PublishStage(LlmOperationState state, string jobSetId, string message, string detail, string? model = null, JobSetSubtask? activeSubtask = null)
    {
        operations.UpdateCurrent(message, detail);
        workspace.MarkJobSetRunning(jobSetId, detail, activeSubtask);

        var currentSnapshot = state.GetSnapshot();
        var snapshot = currentSnapshot with
        {
            Status = "running",
            UpdatedAt = timeProvider.GetUtcNow(),
            Message = message,
            Detail = detail,
            Model = model ?? currentSnapshot.Model,
            Sequence = currentSnapshot.Sequence + 1
        };

        Publish(state, "progress", snapshot);
    }

    private JobSetSessionState GetJobSetOrThrow(string jobSetId)
        => workspace.JobSets.FirstOrDefault(jobSet => jobSet.Id.Equals(jobSetId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"The job set '{jobSetId}' was not found.");

    private static string BuildFitReviewDetail(JobFitAssessment assessment)
    {
        var detail = $"{assessment.Requirements.Count} requirement(s) scored, {assessment.StrongMatchCount} strong match(es), {assessment.GapCount} gap(s).";
        if (!assessment.IsLlmEnhanced)
        {
            return detail;
        }

        var upgradedCount = assessment.Requirements.Count(static requirement => requirement.IsLlmEnhanced);
        return $"{detail} LLM upgraded {upgradedCount} requirement(s).";
    }

    private static string BuildFitReviewFingerprint(
        CandidateProfile candidateProfile,
        JobSetSessionState jobSet,
        ApplicantDifferentiatorProfile differentiatorProfile,
        string selectedModel,
        string selectedThinkingLevel,
        bool useLlmEnhancement)
    {
        var builder = new StringBuilder();
        AppendFingerprint(builder, candidateProfile.Name.ToString());
        AppendFingerprint(builder, candidateProfile.Headline);
        AppendFingerprint(builder, candidateProfile.Summary);
        AppendFingerprint(builder, candidateProfile.Industry);
        AppendFingerprint(builder, candidateProfile.Location);
        AppendFingerprint(builder, candidateProfile.PublicProfileUrl);
        AppendFingerprint(builder, candidateProfile.PrimaryEmail);

        foreach (var experience in candidateProfile.Experience)
        {
            AppendFingerprint(builder, experience.CompanyName);
            AppendFingerprint(builder, experience.Title);
            AppendFingerprint(builder, experience.Description);
            AppendFingerprint(builder, experience.Location);
            AppendFingerprint(builder, experience.Period.ToString());
        }

        foreach (var project in candidateProfile.Projects)
        {
            AppendFingerprint(builder, project.Title);
            AppendFingerprint(builder, project.Description);
            AppendFingerprint(builder, project.Url?.ToString());
            AppendFingerprint(builder, project.Period.ToString());
        }

        foreach (var skill in candidateProfile.Skills.OrderBy(static skill => skill.Name, StringComparer.OrdinalIgnoreCase))
        {
            AppendFingerprint(builder, skill.Name);
        }

        foreach (var certification in candidateProfile.Certifications)
        {
            AppendFingerprint(builder, certification.Name);
            AppendFingerprint(builder, certification.Authority);
            AppendFingerprint(builder, certification.Url?.ToString());
            AppendFingerprint(builder, certification.Period.ToString());
        }

        foreach (var recommendation in candidateProfile.Recommendations)
        {
            AppendFingerprint(builder, recommendation.Author.ToString());
            AppendFingerprint(builder, recommendation.Company);
            AppendFingerprint(builder, recommendation.JobTitle);
            AppendFingerprint(builder, recommendation.Text);
            AppendFingerprint(builder, recommendation.VisibilityStatus);
            AppendFingerprint(builder, recommendation.CreatedOn?.ToString());
        }

        foreach (var signal in candidateProfile.ManualSignals.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            AppendFingerprint(builder, signal.Key);
            AppendFingerprint(builder, signal.Value);
        }

        AppendFingerprint(builder, jobSet.JobPosting?.RoleTitle);
        AppendFingerprint(builder, jobSet.JobPosting?.CompanyName);
        AppendFingerprint(builder, jobSet.JobPosting?.Summary);
        AppendFingerprint(builder, string.Join('|', jobSet.JobPosting?.MustHaveThemes ?? Array.Empty<string>()));
        AppendFingerprint(builder, string.Join('|', jobSet.JobPosting?.NiceToHaveThemes ?? Array.Empty<string>()));
        AppendFingerprint(builder, string.Join('|', jobSet.JobPosting?.CulturalSignals ?? Array.Empty<string>()));
        AppendFingerprint(builder, jobSet.CompanyProfile?.Name);
        AppendFingerprint(builder, jobSet.CompanyProfile?.Summary);
        AppendFingerprint(builder, string.Join('|', jobSet.CompanyProfile?.GuidingPrinciples ?? Array.Empty<string>()));
        AppendFingerprint(builder, string.Join('|', jobSet.CompanyProfile?.CulturalSignals ?? Array.Empty<string>()));
        AppendFingerprint(builder, string.Join('|', jobSet.CompanyProfile?.Differentiators ?? Array.Empty<string>()));

        foreach (var line in differentiatorProfile.ToSummaryLines())
        {
            AppendFingerprint(builder, line);
        }

        if (useLlmEnhancement)
        {
            AppendFingerprint(builder, selectedModel);
            AppendFingerprint(builder, selectedThinkingLevel);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash);
    }

    private static void AppendFingerprint(StringBuilder builder, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            builder.AppendLine("<empty>");
            return;
        }

        builder.AppendLine(string.Join(' ', value.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToLowerInvariant());
    }

    private void Publish(LlmOperationState state, string eventType, LlmOperationSnapshot snapshot)
    {
        state.SetSnapshot(snapshot);
        state.Events.Writer.TryWrite(new LlmOperationEvent(eventType, snapshot));
    }

    private static void Complete(LlmOperationState state)
    {
        state.Events.Writer.TryComplete();
        state.Dispose();
    }

    private static IReadOnlyList<DocumentKind> ParseDocumentKinds(IReadOnlyList<string> documentKinds)
    {
        if (documentKinds.Count == 0)
        {
            throw new InvalidOperationException("Select at least one document type.");
        }

        return documentKinds
            .Select(static value => Enum.TryParse<DocumentKind>(value, ignoreCase: true, out var parsed)
                ? parsed
                : throw new InvalidOperationException($"The document type '{value}' is not supported."))
            .Distinct()
            .ToArray();
    }

    private sealed class LlmOperationState(LlmOperationSnapshot snapshot, DateTimeOffset? timeoutAt)
    {
        private readonly object gate = new();
        private LlmOperationSnapshot snapshot = snapshot;
        private Timer? timeoutTimer;
        private volatile bool timedOut;

        public Channel<LlmOperationEvent> Events { get; } = Channel.CreateUnbounded<LlmOperationEvent>();

        public CancellationTokenSource Cancellation { get; } = new();

        public DateTimeOffset? TimeoutAt { get; } = timeoutAt;

        public bool TimedOut => timedOut;

        public void StartTimeoutCountdown(TimeSpan timeout)
        {
            timeoutTimer = new Timer(_ =>
            {
                timedOut = true;

                try
                {
                    Cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }, null, timeout, Timeout.InfiniteTimeSpan);
        }

        public LlmOperationSnapshot GetSnapshot()
        {
            lock (gate)
            {
                return snapshot;
            }
        }

        public void SetSnapshot(LlmOperationSnapshot next)
        {
            lock (gate)
            {
                snapshot = next;
            }
        }

        public void Dispose()
        {
            timeoutTimer?.Dispose();
            Cancellation.Dispose();
        }
    }

    private sealed record DraftGenerationOperationInput(
        JobSetSessionState JobSet,
        CandidateProfile CandidateProfile,
        ApplicantDifferentiatorProfile ApplicantDifferentiatorProfile,
        string SelectedModel,
        string SelectedThinkingLevel,
        IReadOnlyList<DocumentKind> DocumentKinds,
        bool ExportToFiles,
        PersonalContactInfo? PersonalContact);

    private sealed record JobContextOperationInput(
        JobSetSessionState JobSet,
        string SelectedModel,
        string SelectedThinkingLevel,
        Uri? JobUri,
        IReadOnlyList<Uri> CompanyUrls,
        string? JobPostingText,
        string? CompanyContextText);

    private sealed record TechnologyGapOperationInput(
        JobSetSessionState JobSet,
        CandidateProfile CandidateProfile,
        string SelectedModel,
        string SelectedThinkingLevel);

    private sealed record FitReviewOperationInput(
        JobSetSessionState JobSet,
        CandidateProfile CandidateProfile,
        string SelectedModel,
        string SelectedThinkingLevel,
        bool UseLlmEnhancement);

    private sealed record RefreshAllOperationInput(
        JobSetSessionState JobSet,
        CandidateProfile? CandidateProfile,
        string SelectedModel,
        string SelectedThinkingLevel,
        Uri? JobUri,
        IReadOnlyList<Uri> CompanyUrls,
        string? JobPostingText,
        string? CompanyContextText,
        JobSetInputMode InputMode);
}

public sealed record StartDraftGenerationOperationRequest(
    string JobSetId,
    IReadOnlyList<string> DocumentKinds,
    bool ExportToFiles = true,
    PersonalContactInfo? PersonalContact = null);

public sealed record StartJobContextOperationRequest(string JobSetId);

public sealed record StartTechnologyGapOperationRequest(string JobSetId);

public sealed record StartFitReviewOperationRequest(string JobSetId);

public sealed record StartRefreshAllOperationRequest(string JobSetId);

public sealed record LlmOperationStartResult(
    string OperationId,
    string StatusPath,
    string EventsPath,
    string CancelPath);

public sealed record LlmOperationSnapshot(
    string OperationId,
    string Kind,
    string Status,
    string? JobSetId,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    string Message,
    string? Detail,
    string? Model,
    TimeSpan? Elapsed = null,
    bool Completed = false,
    bool Cancelled = false,
    string? Error = null,
    long? PromptTokens = null,
    long? CompletionTokens = null,
    TimeSpan? EstimatedRemaining = null,
    string? ResponseContent = null,
    string? ThinkingPreview = null,
    string? ThinkingContent = null,
    long Sequence = 0,
    IReadOnlyList<OperationWarning>? Warnings = null)
{
    public bool IsTerminal => Status is "completed" or "failed" or "cancelled";

    public IReadOnlyList<OperationWarning> WarningItems => Warnings ?? Array.Empty<OperationWarning>();
}

public sealed record OperationWarning(
    string Code,
    string Scope,
    string Message,
    int? AttemptedCount = null,
    int? SuccessfulCount = null,
    int? SkippedCount = null);

public sealed record LlmOperationEvent(string EventType, LlmOperationSnapshot Snapshot);
