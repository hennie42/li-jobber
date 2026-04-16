using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
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
        workspace.MarkJobSetRunning(input.JobSet.Id, "Draft generation is running for this tab.");
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
        workspace.MarkJobSetRunning(input.JobSet.Id, "Job and company context analysis is running for this tab.");
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
        workspace.MarkJobSetRunning(input.JobSet.Id, "Technology gap analysis is running for this tab.");
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

        workspace.MarkJobSetRunning(input.JobSet.Id, "Fit review is running for this tab.");
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
        workspace.MarkJobSetRunning(input.JobSet.Id, "Refresh all analysis is running for this tab.");
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
            state.Cancellation.CancelAfter(operationTimeout.Value);
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
        => state.TimeoutAt is { } timeoutAt && timeProvider.GetUtcNow() >= timeoutAt;

    private string BuildTimeoutDetail(string operationLabel)
    {
        var timeout = GetOperationTimeout();
        if (timeout is null)
        {
            return $"{operationLabel} timed out.";
        }

        return $"{operationLabel} exceeded the {FormatDuration(timeout.Value)} limit. Lower the thinking level or choose a faster model and try again.";
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
            request.ExportToFiles);
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

        if (companyUrls.Length == 0)
        {
            throw new InvalidOperationException("Add at least one absolute company URL.");
        }

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

            var generationJobSet = GetJobSetOrThrow(input.JobSet.Id);

            PublishStage(
                state,
                input.JobSet.Id,
                "Generating targeted drafts",
                $"{input.DocumentKinds.Count} document type(s) queued for {input.JobSet.Title}.",
                input.SelectedModel);

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
                    generationJobSet.TechnologyGapAssessment),
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
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var jobResearchService = scope.ServiceProvider.GetRequiredService<IJobResearchService>();

            JobPostingAnalysis analysis;
            CompanyResearchProfile? companyProfile = null;

            if (input.JobSet.InputMode == JobSetInputMode.PasteText)
            {
                analysis = await jobResearchService.AnalyzeTextAsync(
                    input.JobPostingText!,
                    input.SelectedModel,
                    input.SelectedThinkingLevel,
                    update => HandleProgress(state, input.JobSet.Id, update),
                    state.Cancellation.Token);

                workspace.SetJobSetJobPosting(input.JobSet.Id, analysis);

                if (!string.IsNullOrWhiteSpace(input.CompanyContextText))
                {
                    companyProfile = await jobResearchService.BuildCompanyProfileFromTextAsync(
                        input.CompanyContextText,
                        input.SelectedModel,
                        input.SelectedThinkingLevel,
                        update => HandleProgress(state, input.JobSet.Id, update),
                        state.Cancellation.Token);

                    workspace.SetJobSetCompanyProfile(input.JobSet.Id, companyProfile);
                }
            }
            else
            {
                analysis = await jobResearchService.AnalyzeAsync(
                    input.JobUri!,
                    input.SelectedModel,
                    input.SelectedThinkingLevel,
                    update => HandleProgress(state, input.JobSet.Id, update),
                    state.Cancellation.Token);

                workspace.SetJobSetJobPosting(input.JobSet.Id, analysis);

                companyProfile = await jobResearchService.BuildCompanyProfileAsync(
                    input.CompanyUrls,
                    input.SelectedModel,
                    input.SelectedThinkingLevel,
                    update => HandleProgress(state, input.JobSet.Id, update),
                    state.Cancellation.Token);

                workspace.SetJobSetCompanyProfile(input.JobSet.Id, companyProfile);
            }

            var detail = companyProfile is null
                ? "The job tab now has the latest target role context. Fit review and generation are still idle until you run them."
                : "The job tab now has the latest target role context and company context. Fit review and generation are still idle until you run them.";
            var completedSnapshot = state.GetSnapshot() with
            {
                Status = "completed",
                UpdatedAt = timeProvider.GetUtcNow(),
                Message = "Job and company context updated",
                Detail = detail,
                Completed = true
            };

            Publish(state, "completed", completedSnapshot);
            operations.Success("Job and company context updated.", detail);
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
                    Error = detail
                };

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
                Cancelled = true
            };

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
                Error = exception.Message
            };

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

            var assessment = await RefreshFitReviewCoreAsync(
                state,
                input.JobSet.Id,
                input.JobSet.Title,
                input.CandidateProfile,
                input.SelectedModel,
                input.SelectedThinkingLevel,
                input.UseLlmEnhancement,
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
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var jobResearchService = scope.ServiceProvider.GetRequiredService<IJobResearchService>();
            var fitRefreshService = scope.ServiceProvider.GetRequiredService<JobFitWorkspaceRefreshService>();
            var fitEnhancementService = scope.ServiceProvider.GetRequiredService<LlmFitEnhancementService>();
            var gapAnalysisService = scope.ServiceProvider.GetRequiredService<LlmTechnologyGapAnalysisService>();

            PublishStage(
                state,
                input.JobSet.Id,
                "Analyzing job and company context",
                input.InputMode == JobSetInputMode.PasteText
                    ? $"Refreshing pasted job text for {input.JobSet.Title}."
                    : $"Refreshing {input.JobUri} for {input.JobSet.Title}.",
                input.SelectedModel);

            if (input.InputMode == JobSetInputMode.PasteText)
            {
                var analysis = await jobResearchService.AnalyzeTextAsync(
                    input.JobPostingText!,
                    input.SelectedModel,
                    input.SelectedThinkingLevel,
                    update => HandleProgress(state, input.JobSet.Id, update),
                    state.Cancellation.Token);

                workspace.SetJobSetJobPosting(input.JobSet.Id, analysis);

                if (!string.IsNullOrWhiteSpace(input.CompanyContextText))
                {
                    var companyProfile = await jobResearchService.BuildCompanyProfileFromTextAsync(
                        input.CompanyContextText,
                        input.SelectedModel,
                        input.SelectedThinkingLevel,
                        update => HandleProgress(state, input.JobSet.Id, update),
                        state.Cancellation.Token);

                    workspace.SetJobSetCompanyProfile(input.JobSet.Id, companyProfile);
                }
            }
            else
            {
                var analysis = await jobResearchService.AnalyzeAsync(
                    input.JobUri!,
                    input.SelectedModel,
                    input.SelectedThinkingLevel,
                    update => HandleProgress(state, input.JobSet.Id, update),
                    state.Cancellation.Token);

                workspace.SetJobSetJobPosting(input.JobSet.Id, analysis);

                if (input.CompanyUrls.Count > 0)
                {
                    var companyProfile = await jobResearchService.BuildCompanyProfileAsync(
                        input.CompanyUrls,
                        input.SelectedModel,
                        input.SelectedThinkingLevel,
                        update => HandleProgress(state, input.JobSet.Id, update),
                        state.Cancellation.Token);

                    workspace.SetJobSetCompanyProfile(input.JobSet.Id, companyProfile);
                }
            }

            state.Cancellation.Token.ThrowIfCancellationRequested();

            if (input.CandidateProfile is not null)
            {
                await RefreshFitReviewCoreAsync(
                    state,
                    input.JobSet.Id,
                    input.JobSet.Title,
                    input.CandidateProfile,
                    input.SelectedModel,
                    input.SelectedThinkingLevel,
                    useLlmEnhancement: true,
                    fitRefreshService,
                    fitEnhancementService,
                    state.Cancellation.Token);

                PublishStage(
                    state,
                    input.JobSet.Id,
                    "Analyzing technology gaps",
                    $"Comparing profile evidence against {input.JobSet.Title}.",
                    input.SelectedModel);

                var latestJobSet = GetJobSetOrThrow(input.JobSet.Id);
                var technologyGapAssessment = await gapAnalysisService.AnalyzeAsync(
                    input.CandidateProfile,
                    latestJobSet.JobPosting!,
                    latestJobSet.CompanyProfile,
                    input.SelectedModel,
                    input.SelectedThinkingLevel,
                    update => HandleProgress(state, input.JobSet.Id, update),
                    state.Cancellation.Token);

                workspace.SetJobSetTechnologyGapAssessment(input.JobSet.Id, technologyGapAssessment);
                workspace.ResetJobSetProgress(input.JobSet.Id, "Job, company, fit review, technology gap and evidence are current for this tab.");
            }
            else
            {
                workspace.ResetJobSetProgress(input.JobSet.Id, "Job and company context are current. Load a profile to refresh fit review, evidence ranking, and technology gaps.");
            }

            var completedDetail = input.CandidateProfile is null
                ? "Job and company context are current. Load a profile to refresh fit review, evidence ranking, and technology gaps."
                : "Job, company, fit review, technology gap and evidence are current for this tab.";
            var completedSnapshot = state.GetSnapshot() with
            {
                Status = "completed",
                UpdatedAt = timeProvider.GetUtcNow(),
                Message = "All analysis refreshed",
                Detail = completedDetail,
                Completed = true,
                Sequence = state.GetSnapshot().Sequence + 1
            };

            Publish(state, "completed", completedSnapshot);
            operations.Success("All analysis refreshed.", completedDetail);
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
                };

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
                Sequence = state.GetSnapshot().Sequence + 1
            };

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
                Sequence = state.GetSnapshot().Sequence + 1
            };

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

    private async Task<JobFitAssessment> RefreshFitReviewCoreAsync(
        LlmOperationState state,
        string jobSetId,
        string jobSetTitle,
        CandidateProfile candidateProfile,
        string selectedModel,
        string selectedThinkingLevel,
        bool useLlmEnhancement,
        JobFitWorkspaceRefreshService fitRefreshService,
        LlmFitEnhancementService fitEnhancementService,
        CancellationToken cancellationToken)
    {
        PublishStage(
            state,
            jobSetId,
            "Analyzing job fit",
            $"Refreshing deterministic fit signals for {jobSetTitle}.");

        if (!fitRefreshService.RefreshJobSet(jobSetId))
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
                selectedModel);

            var enhanced = await fitEnhancementService.EnhanceAsync(
                refreshedJobSet.JobFitAssessment,
                candidateProfile,
                refreshedJobSet.JobPosting!,
                refreshedJobSet.CompanyProfile,
                selectedModel,
                selectedThinkingLevel,
                update => HandleProgress(state, jobSetId, update),
                cancellationToken);

            workspace.SetJobSetJobFitAssessment(jobSetId, enhanced);
            fitRefreshService.RefreshJobSetEvidence(jobSetId);

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

        return GetJobSetOrThrow(jobSetId).JobFitAssessment;
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

    private void PublishStage(LlmOperationState state, string jobSetId, string message, string detail, string? model = null)
    {
        operations.UpdateCurrent(message, detail);
        workspace.MarkJobSetRunning(jobSetId, detail);

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

    private void Publish(LlmOperationState state, string eventType, LlmOperationSnapshot snapshot)
    {
        state.SetSnapshot(snapshot);
        state.Events.Writer.TryWrite(new LlmOperationEvent(eventType, snapshot));
    }

    private static void Complete(LlmOperationState state)
    {
        state.Events.Writer.TryComplete();
        state.Cancellation.Dispose();
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

        public Channel<LlmOperationEvent> Events { get; } = Channel.CreateUnbounded<LlmOperationEvent>();

        public CancellationTokenSource Cancellation { get; } = new();

        public DateTimeOffset? TimeoutAt { get; } = timeoutAt;

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
    }

    private sealed record DraftGenerationOperationInput(
        JobSetSessionState JobSet,
        CandidateProfile CandidateProfile,
        ApplicantDifferentiatorProfile ApplicantDifferentiatorProfile,
        string SelectedModel,
        string SelectedThinkingLevel,
        IReadOnlyList<DocumentKind> DocumentKinds,
        bool ExportToFiles);

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
    bool ExportToFiles = true);

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
    long Sequence = 0)
{
    public bool IsTerminal => Status is "completed" or "failed" or "cancelled";
}

public sealed record LlmOperationEvent(string EventType, LlmOperationSnapshot Snapshot);