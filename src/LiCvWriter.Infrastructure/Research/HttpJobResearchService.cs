using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Application.Services;
using LiCvWriter.Core.Jobs;
using LiCvWriter.Infrastructure.Workflows;

namespace LiCvWriter.Infrastructure.Research;

public sealed class HttpJobResearchService(HttpClient httpClient, ILlmClient llmClient, OllamaOptions ollamaOptions) : IJobResearchService
{
    private const int MaxJobContextCharacters = 8_000;
    private const int MaxCompanyContextCharacters = 12_000;
    private const int ExtractionNumPredict = 4_096;
    private const int InferenceNumPredict = 2_048;
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36";
    private const string HtmlAcceptHeader = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
    private const string AcceptLanguageHeader = "en-US,en;q=0.9";
    private static readonly HashSet<string> GenericRequirementPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "links",
        "there is",
        "of course",
        "click here",
        "read more",
        "learn more",
        "home",
        "menu",
        "privacy",
        "terms",
        "cookies",
        "about",
        "contact"
    };

    private static readonly HashSet<string> NonDomainTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "to", "for", "with", "from", "there", "is", "of", "course",
        "click", "here", "read", "more", "learn", "home", "menu", "privacy", "terms", "cookies",
        "about", "contact", "we", "our", "you", "your", "this", "that", "it"
    };

    public async Task<JobPostingAnalysis> AnalyzeAsync(
        Uri jobPostingUrl,
        string? selectedModel = null,
        string? selectedThinkingLevel = null,
        Action<LlmProgressUpdate>? progress = null,
        string? sourceLanguageHint = null,
        CancellationToken cancellationToken = default)
    {
        await ValidatePublicHttpsUriAsync(jobPostingUrl, cancellationToken);
        var html = await FetchHtmlAsync(jobPostingUrl, cancellationToken);
        var title = ExtractMatch(html, "<title[^>]*>(.*?)</title>") ?? jobPostingUrl.Host;
        var heading = ExtractMatch(html, "<h1[^>]*>(.*?)</h1>") ?? title;
        var text = ExtractSemanticContent(html);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("The job page did not contain enough readable text to parse.");
        }

        var resolvedModel = ResolveModel(selectedModel);
        var extractionThinking = ResolveExtractionThinkingLevel(ResolveThinkingLevel(selectedThinkingLevel));
        var response = await llmClient.GenerateAsync(
            new LlmRequest(
                resolvedModel,
                BuildJobSystemPrompt(sourceLanguageHint),
                [new LlmChatMessage("user", BuildJobUserPrompt(jobPostingUrl, title, heading, text))],
                UseChatEndpoint: ollamaOptions.UseChatEndpoint,
                Stream: true,
                Think: extractionThinking,
                KeepAlive: ollamaOptions.KeepAlive,
                Temperature: 0.0,
                NumPredict: ExtractionNumPredict,
                ResponseFormat: LlmResponseFormat.Json,
                PromptId: LlmPromptCatalog.JobExtractJson,
                PromptVersion: LlmPromptCatalog.Version1),
            progress is null ? null : update => progress(update with
            {
                Message = "Parsing job posting",
                Detail = string.IsNullOrWhiteSpace(update.Detail)
                    ? $"Structured job parsing is running via {update.Model}."
                    : update.Detail
            }),
            cancellationToken);

        var analysis = ParseJobPostingAnalysis(response.Content, jobPostingUrl, heading, text);

        // Second pass: infer unstated requirements commonly expected for this role type.
        var inferred = await InferHiddenRequirementsAsync(analysis, resolvedModel, extractionThinking, progress, cancellationToken);
        if (inferred.Count > 0)
        {
            analysis = analysis with { InferredRequirements = inferred };
        }

        return analysis;
    }

    public async Task<CompanyResearchProfile> BuildCompanyProfileAsync(
        IEnumerable<Uri> sourceUrls,
        string? selectedModel = null,
        string? selectedThinkingLevel = null,
        Action<LlmProgressUpdate>? progress = null,
        string? sourceLanguageHint = null,
        CancellationToken cancellationToken = default)
    {
        var urls = sourceUrls.ToArray();
        if (urls.Length == 0)
        {
            throw new InvalidOperationException("At least one company context URL is required.");
        }

        var sourceDocuments = new List<(Uri Url, string Text)>();

        foreach (var url in urls)
        {
            await ValidatePublicHttpsUriAsync(url, cancellationToken);
            var html = await FetchHtmlAsync(url, cancellationToken);
            var text = StripHtml(html);
            if (!string.IsNullOrWhiteSpace(text))
            {
                sourceDocuments.Add((url, text));
            }
        }

        if (sourceDocuments.Count == 0)
        {
            throw new InvalidOperationException("The provided company pages did not contain enough readable text to parse.");
        }

        var response = await llmClient.GenerateAsync(
            new LlmRequest(
                ResolveModel(selectedModel),
                BuildCompanySystemPrompt(sourceLanguageHint),
                [new LlmChatMessage("user", BuildCompanyUserPrompt(sourceDocuments))],
                UseChatEndpoint: ollamaOptions.UseChatEndpoint,
                Stream: true,
                Think: ResolveExtractionThinkingLevel(ResolveThinkingLevel(selectedThinkingLevel)),
                KeepAlive: ollamaOptions.KeepAlive,
                Temperature: 0.0,
                NumPredict: ExtractionNumPredict,
                ResponseFormat: LlmResponseFormat.Json,
                PromptId: LlmPromptCatalog.CompanyExtractJson,
                PromptVersion: LlmPromptCatalog.Version1),
            progress is null ? null : update => progress(update with
            {
                Message = "Parsing company context",
                Detail = string.IsNullOrWhiteSpace(update.Detail)
                    ? $"Structured company parsing is running via {update.Model}."
                    : update.Detail
            }),
            cancellationToken);

        return ParseCompanyResearchProfile(response.Content, urls, sourceDocuments);
    }

    public async Task<JobPostingAnalysis> AnalyzeTextAsync(
        string jobPostingText,
        string? selectedModel = null,
        string? selectedThinkingLevel = null,
        Action<LlmProgressUpdate>? progress = null,
        string? sourceLanguageHint = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobPostingText))
        {
            throw new InvalidOperationException("The pasted job posting text is empty.");
        }

        var resolvedModel = ResolveModel(selectedModel);
        var extractionThinking = ResolveExtractionThinkingLevel(ResolveThinkingLevel(selectedThinkingLevel));
        var response = await llmClient.GenerateAsync(
            new LlmRequest(
                resolvedModel,
                BuildJobSystemPrompt(sourceLanguageHint),
                [new LlmChatMessage("user", BuildPastedJobUserPrompt(jobPostingText))],
                UseChatEndpoint: ollamaOptions.UseChatEndpoint,
                Stream: true,
                Think: extractionThinking,
                KeepAlive: ollamaOptions.KeepAlive,
                Temperature: 0.0,
                NumPredict: ExtractionNumPredict,
                ResponseFormat: LlmResponseFormat.Json,
                PromptId: LlmPromptCatalog.JobExtractJson,
                PromptVersion: LlmPromptCatalog.Version1),
            progress is null ? null : update => progress(update with
            {
                Message = "Parsing job posting",
                Detail = string.IsNullOrWhiteSpace(update.Detail)
                    ? $"Structured job parsing is running via {update.Model}."
                    : update.Detail
            }),
            cancellationToken);

        return ParseJobPostingAnalysis(response.Content, jobPostingUrl: null, fallbackRoleTitle: "Pasted job posting", jobText: jobPostingText);
    }

    public async Task<CompanyResearchProfile> BuildCompanyProfileFromTextAsync(
        string companyContextText,
        string? selectedModel = null,
        string? selectedThinkingLevel = null,
        Action<LlmProgressUpdate>? progress = null,
        string? sourceLanguageHint = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(companyContextText))
        {
            throw new InvalidOperationException("The pasted company context text is empty.");
        }

        var response = await llmClient.GenerateAsync(
            new LlmRequest(
                ResolveModel(selectedModel),
                BuildCompanySystemPrompt(sourceLanguageHint),
                [new LlmChatMessage("user", BuildPastedCompanyUserPrompt(companyContextText))],
                UseChatEndpoint: ollamaOptions.UseChatEndpoint,
                Stream: true,
                Think: ResolveExtractionThinkingLevel(ResolveThinkingLevel(selectedThinkingLevel)),
                KeepAlive: ollamaOptions.KeepAlive,
                Temperature: 0.0,
                NumPredict: ExtractionNumPredict,
                ResponseFormat: LlmResponseFormat.Json,
                PromptId: LlmPromptCatalog.CompanyExtractJson,
                PromptVersion: LlmPromptCatalog.Version1),
            progress is null ? null : update => progress(update with
            {
                Message = "Parsing company context",
                Detail = string.IsNullOrWhiteSpace(update.Detail)
                    ? $"Structured company parsing is running via {update.Model}."
                    : update.Detail
            }),
            cancellationToken);

        return ParseCompanyResearchProfile(response.Content, sourceUrls: Array.Empty<Uri>(), sourceDocuments: [(default!, companyContextText)]);
    }

    private static readonly JsonDocumentOptions LenientJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static JobPostingAnalysis ParseJobPostingAnalysis(
        string content,
        Uri? jobPostingUrl,
        string fallbackRoleTitle,
        string jobText)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(ExtractJsonObject(content), LenientJsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            if (TryParseFallbackJobPostingAnalysis(content, jobPostingUrl, fallbackRoleTitle, jobText, out var fallbackAnalysis))
            {
                return fallbackAnalysis;
            }

            var preview = content.Length > 200 ? content[..200] + "..." : content;
            throw new InvalidOperationException(
                $"The model did not return parseable JSON for the job analysis. Try again or check the session model. Response preview: {preview}",
                exception);
        }

        using (document)
        {
        var root = document.RootElement;

        var signals = ReadSignals(
            root,
            "requirements",
            defaultSourceLabel: jobPostingUrl?.Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase) ?? "pasted text");

        var roleTitle = ReadOptionalString(root, "roleTitle");
        var companyName = ReadOptionalString(root, "companyName");
        var summary = ReadOptionalString(root, "summary");

        return new JobPostingAnalysis
        {
            SourceUrl = jobPostingUrl,
            RoleTitle = string.IsNullOrWhiteSpace(roleTitle) ? fallbackRoleTitle : roleTitle,
            CompanyName = string.IsNullOrWhiteSpace(companyName)
                ? jobPostingUrl?.Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase) ?? "Unknown"
                : companyName,
            Summary = string.IsNullOrWhiteSpace(summary) ? Clip(jobText, 1_200) : Clip(summary, 1_200),
            MustHaveThemes = signals.Where(static signal => signal.Importance == JobRequirementImportance.MustHave).Select(static signal => signal.Requirement).ToArray(),
            NiceToHaveThemes = signals.Where(static signal => signal.Importance == JobRequirementImportance.NiceToHave).Select(static signal => signal.Requirement).ToArray(),
            CulturalSignals = signals.Where(static signal => signal.Importance == JobRequirementImportance.Cultural).Select(static signal => signal.Requirement).ToArray(),
            Signals = signals
        };
        } // using document
    }

    private static CompanyResearchProfile ParseCompanyResearchProfile(
        string content,
        IReadOnlyList<Uri> sourceUrls,
        IReadOnlyList<(Uri Url, string Text)> sourceDocuments)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(ExtractJsonObject(content), LenientJsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            if (TryParseFallbackCompanyResearchProfile(content, sourceUrls, sourceDocuments, out var fallbackProfile))
            {
                return fallbackProfile;
            }

            var preview = content.Length > 200 ? content[..200] + "..." : content;
            throw new InvalidOperationException(
                $"The model did not return parseable JSON for the company analysis. Try again or check the session model. Response preview: {preview}",
                exception);
        }

        using (document)
        {
        var root = document.RootElement;

        var signals = ReadSignals(root, "requirements", defaultSourceLabel: sourceUrls.Count > 0
            ? sourceUrls[0].Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase)
            : "pasted text");
        var summary = ReadOptionalString(root, "summary");
        var name = ReadOptionalString(root, "name");
        var guidingPrinciples = ReadStringArray(root, "guidingPrinciples", required: false);
        var differentiators = ReadStringArray(root, "differentiators", required: false);

        return new CompanyResearchProfile
        {
            Name = string.IsNullOrWhiteSpace(name)
                ? (sourceUrls.Count > 0
                    ? sourceUrls[0].Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase)
                    : "Unknown")
                : name,
            Summary = string.IsNullOrWhiteSpace(summary)
                ? Clip(string.Join(Environment.NewLine + Environment.NewLine, sourceDocuments.Select(static item => item.Text)), 2_000)
                : Clip(summary, 2_000),
            SourceUrls = sourceUrls,
            GuidingPrinciples = guidingPrinciples.Take(5).ToArray(),
            CulturalSignals = signals
                .Where(static signal => signal.Importance == JobRequirementImportance.Cultural)
                .Select(static signal => signal.Requirement)
                .Concat(guidingPrinciples)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Differentiators = differentiators.Take(6).ToArray(),
            Signals = signals
        };
        } // using document
    }

    private async Task<string> FetchHtmlAsync(Uri sourceUrl, CancellationToken cancellationToken)
    {
        using var request = CreateHtmlRequest(sourceUrl);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                $"The site blocked access to {sourceUrl} with 403 Forbidden. Some job and company pages reject automated requests even when the request looks like a browser.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var failureDetail = await ReadFailureDetailAsync(response, cancellationToken);
            throw new InvalidOperationException(
                $"Fetching {sourceUrl} failed with {(int)response.StatusCode} {response.ReasonPhrase}.{failureDetail}");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static async Task ValidatePublicHttpsUriAsync(Uri sourceUrl, CancellationToken cancellationToken)
    {
        if (!sourceUrl.IsAbsoluteUri)
        {
            throw new InvalidOperationException($"Only absolute public HTTPS URLs are allowed: {sourceUrl}");
        }

        if (!string.Equals(sourceUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Only public HTTPS URLs are allowed: {sourceUrl}");
        }

        if (string.IsNullOrWhiteSpace(sourceUrl.Host))
        {
            throw new InvalidOperationException($"The URL host is invalid: {sourceUrl}");
        }

        if (sourceUrl.IsLoopback || IsLocalHostName(sourceUrl.Host))
        {
            throw new InvalidOperationException($"Local or private hosts are not allowed: {sourceUrl}");
        }

        if (IPAddress.TryParse(sourceUrl.Host, out var parsedAddress))
        {
            if (IsPrivateAddress(parsedAddress))
            {
                throw new InvalidOperationException($"Local or private hosts are not allowed: {sourceUrl}");
            }

            return;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(sourceUrl.DnsSafeHost, cancellationToken);
        }
        catch (SocketException exception)
        {
            _ = exception;
            return;
        }

        if (addresses.Any(IsPrivateAddress))
        {
            throw new InvalidOperationException($"Local or private hosts are not allowed: {sourceUrl}");
        }
    }

    private static bool IsLocalHostName(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase);

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast
                || address.Equals(IPAddress.IPv6Loopback)
                || address.IsIPv6UniqueLocal;
        }

        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return true;
        }

        return bytes[0] switch
        {
            0 => true,
            10 => true,
            127 => true,
            169 when bytes[1] == 254 => true,
            172 when bytes[1] >= 16 && bytes[1] <= 31 => true,
            192 when bytes[1] == 168 => true,
            _ => false
        };
    }

    private static HttpRequestMessage CreateHtmlRequest(Uri sourceUrl)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, sourceUrl);
        request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
        request.Headers.Accept.ParseAdd(HtmlAcceptHeader);
        request.Headers.AcceptLanguage.ParseAdd(AcceptLanguageHeader);
        request.Headers.AcceptEncoding.ParseAdd("gzip");
        request.Headers.AcceptEncoding.ParseAdd("deflate");
        request.Headers.AcceptEncoding.ParseAdd("br");
        request.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        return request;
    }

    private static async Task<string> ReadFailureDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        var trimmed = Regex.Replace(body, @"\s+", " ").Trim();
        if (trimmed.Length > 240)
        {
            trimmed = trimmed[..240].TrimEnd() + "...";
        }

        return $" Response excerpt: {trimmed}";
    }

    private static JobContextSignal[] ReadSignals(JsonElement root, string propertyName, string defaultSourceLabel)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"The parser response did not include a valid '{propertyName}' array.");
        }

        var signals = property.EnumerateArray()
            .Select(item => TryReadSignal(item, defaultSourceLabel))
            .Where(static signal => signal is not null)
            .Cast<JobContextSignal>()
            .DistinctBy(static signal => $"{signal.Importance}:{signal.Requirement}", StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (property.GetArrayLength() > 0 && signals.Length == 0)
        {
            throw new InvalidOperationException($"The parser response contained '{propertyName}' entries, but none included a usable requirement, source snippet, and confidence.");
        }

        return signals;
    }

    private static JobContextSignal? TryReadSignal(JsonElement element, string defaultSourceLabel)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var requirement = ReadOptionalString(element, "requirement");
        var categoryValue = ReadOptionalString(element, "category");
        var aliases = ReadStringArray(element, "aliases", required: false);
        var sourceSnippet = ReadOptionalString(element, "sourceSnippet");
        var confidence = ReadOptionalInt(element, "confidence");

        if (string.IsNullOrWhiteSpace(requirement)
            || string.IsNullOrWhiteSpace(sourceSnippet)
            || confidence is null
            || !TryMapCategory(categoryValue, out var category, out var importance))
        {
            return null;
        }

        if (!IsRequirementLabelQualityAcceptable(requirement, sourceSnippet, aliases))
        {
            return null;
        }

        return new JobContextSignal(
            category,
            requirement,
            importance,
            ResolveSourceLabel(ReadOptionalString(element, "sourceUrl"), defaultSourceLabel),
            Clip(sourceSnippet, 260),
            Math.Clamp(confidence.Value, 1, 100),
            NormalizeAliases(requirement, aliases));
    }

    private static bool IsRequirementLabelQualityAcceptable(string requirement, string sourceSnippet, IReadOnlyList<string> aliases)
    {
        var normalizedRequirement = NormalizeRequirementText(requirement);
        if (string.IsNullOrWhiteSpace(normalizedRequirement))
        {
            return false;
        }

        if (GenericRequirementPhrases.Contains(normalizedRequirement))
        {
            return false;
        }

        if (normalizedRequirement.StartsWith("there is", StringComparison.OrdinalIgnoreCase)
            || normalizedRequirement.StartsWith("of course", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var requirementTokens = Tokenize(normalizedRequirement)
            .Where(token => !NonDomainTokens.Contains(token))
            .ToArray();
        if (requirementTokens.Length == 0)
        {
            return false;
        }

        var snippetTokens = Tokenize(sourceSnippet)
            .Where(token => !NonDomainTokens.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var requirementMatchesSnippet = requirementTokens.Any(requirementToken =>
            snippetTokens.Any(snippetToken => TokensOverlap(requirementToken, snippetToken)));
        var aliasesMatchSnippet = aliases
            .SelectMany(static alias => Tokenize(alias))
            .Where(token => !NonDomainTokens.Contains(token))
            .Any(aliasToken => snippetTokens.Any(snippetToken => TokensOverlap(aliasToken, snippetToken)));

        return requirementMatchesSnippet || aliasesMatchSnippet;
    }

    private static bool TokensOverlap(string left, string right)
    {
        if (left.Equals(right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (left.Length < 4 || right.Length < 4)
        {
            return false;
        }

        return left.StartsWith(right, StringComparison.OrdinalIgnoreCase)
            || right.StartsWith(left, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRequirementText(string value)
        => Regex.Replace(value.Trim(), @"\s+", " ");

    private static string[] Tokenize(string value)
        => Regex.Matches(value.ToLowerInvariant(), @"[a-z0-9][a-z0-9\+#\./-]*")
            .Select(static match => match.Value)
            .Where(static token => !string.IsNullOrWhiteSpace(token))
            .ToArray();

    private static string[] NormalizeAliases(string requirement, IEnumerable<string> aliases)
        => aliases
            .Where(static alias => !string.IsNullOrWhiteSpace(alias))
            .Select(static alias => alias.Trim())
            .Where(alias => !alias.Equals(requirement, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();

    private static bool TryMapCategory(string? value, out string category, out JobRequirementImportance importance)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "must have":
            case "must-have":
            case "required":
                category = "Must have";
                importance = JobRequirementImportance.MustHave;
                return true;
            case "nice to have":
            case "nice-to-have":
            case "preferred":
            case "bonus":
                category = "Nice to have";
                importance = JobRequirementImportance.NiceToHave;
                return true;
            case "culture":
            case "cultural":
            case "values":
                category = "Culture";
                importance = JobRequirementImportance.Cultural;
                return true;
            default:
                category = string.Empty;
                importance = default;
                return false;
        }
    }

    private static string[] ReadStringArray(JsonElement root, string propertyName, bool required)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            if (required)
            {
                throw new InvalidOperationException($"The parser response did not include a '{propertyName}' array.");
            }

            return Array.Empty<string>();
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"The parser response did not include a valid '{propertyName}' array.");
        }

        return property.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()?.Trim())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()!;
    }

    private static int? ReadOptionalInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(property.GetString(), out var number) => number,
            _ => null
        };
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string ResolveSourceLabel(string? sourceUrl, string defaultSourceLabel)
    {
        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return uri.Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return defaultSourceLabel;
    }

    /// <summary>
    /// Second LLM pass: infers implicit requirements that are not stated in the job posting
    /// but are commonly expected for this type of role at this seniority level.
    /// </summary>
    private async Task<IReadOnlyList<string>> InferHiddenRequirementsAsync(
        JobPostingAnalysis analysis,
        string model,
        string thinkingLevel,
        Action<LlmProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var explicitRequirements = analysis.MustHaveThemes
            .Concat(analysis.NiceToHaveThemes)
            .Concat(analysis.CulturalSignals)
            .ToArray();

        if (explicitRequirements.Length == 0)
        {
            return Array.Empty<string>();
        }

        var systemPrompt = $$"""
You identify implicit/hidden requirements that are commonly expected but unstated in a job posting.

Return a JSON array of short requirement labels only. Example: ["Networking fundamentals", "Cost optimization", "Security basics"]

Rules:
- Return valid JSON array only. No markdown fences, no explanation.
    - {{PromptConstraints.SourceTextBoundary}}
- Include only requirements that are genuinely implicit for this type of role.
- Do not repeat any of the explicitly stated requirements.
- Limit to 3-5 of the most important unstated requirements.
- Use concise normalized labels (2-4 words each).
""";

        var userPrompt = $"""
Role: {analysis.RoleTitle} at {analysis.CompanyName}
Summary: {analysis.Summary}
Explicit requirements already identified: {string.Join(", ", explicitRequirements)}

What implicit requirements are likely expected but unstated for this role?
""";

        try
        {
            var response = await llmClient.GenerateAsync(
                new LlmRequest(
                    model,
                    systemPrompt,
                    [new LlmChatMessage("user", userPrompt)],
                    UseChatEndpoint: ollamaOptions.UseChatEndpoint,
                    Stream: true,
                    Think: ResolveExtractionThinkingLevel(thinkingLevel),
                    KeepAlive: ollamaOptions.KeepAlive,
                    Temperature: 0.0,
                    NumPredict: InferenceNumPredict,
                    ResponseFormat: LlmResponseFormat.Json,
                    PromptId: LlmPromptCatalog.HiddenRequirementsJson,
                    PromptVersion: LlmPromptCatalog.Version1),
                progress is null ? null : update => progress(update with
                {
                    Message = "Inferring hidden requirements",
                    Detail = string.IsNullOrWhiteSpace(update.Detail)
                        ? "Analyzing implicit role expectations."
                        : update.Detail
                }),
                cancellationToken);

            var content = ExtractJsonArray(response.Content);
            if (string.IsNullOrWhiteSpace(content))
            {
                return Array.Empty<string>();
            }

            using var doc = JsonDocument.Parse(content, LenientJsonOptions);
            return doc.RootElement.EnumerateArray()
                .Where(static e => e.ValueKind == JsonValueKind.String)
                .Select(static e => e.GetString()?.Trim())
                .Where(static s => !string.IsNullOrWhiteSpace(s))
                .Where(label => !explicitRequirements.Any(existing => existing.Equals(label, StringComparison.OrdinalIgnoreCase)))
                .Take(5)
                .ToArray()!;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string? ExtractJsonArray(string content)
    {
        var trimmed = content.Trim();
        trimmed = Regex.Replace(trimmed, @"<think>.*?</think>", string.Empty, RegexOptions.Singleline).Trim();
        trimmed = Regex.Replace(trimmed, @"```(?:json)?\s*\n?(.*?)\n?\s*```", "$1", RegexOptions.Singleline).Trim();
        var start = trimmed.IndexOf('[');
        var end = trimmed.LastIndexOf(']');
        return start >= 0 && end > start ? trimmed[start..(end + 1)] : null;
    }

    private string ResolveModel(string? selectedModel)
        => string.IsNullOrWhiteSpace(selectedModel) ? ollamaOptions.Model : selectedModel.Trim();

    private string ResolveThinkingLevel(string? selectedThinkingLevel)
        => string.IsNullOrWhiteSpace(selectedThinkingLevel) ? ollamaOptions.Think : selectedThinkingLevel.Trim();

    /// <summary>
    /// Caps the thinking level to "low" for structured extraction tasks.
    /// Deep reasoning produces degenerate repetition loops on JSON-output prompts
    /// without improving extraction quality.
    /// </summary>
    internal static string ResolveExtractionThinkingLevel(string thinkingLevel)
        => thinkingLevel is "high" or "medium" ? "low" : thinkingLevel;

    private static string BuildJobSystemPrompt(string? sourceLanguageHint = null)
    {
        var languageLine = string.IsNullOrWhiteSpace(sourceLanguageHint)
            ? string.Empty
            : $"The source job posting is written in {sourceLanguageHint}; extract values verbatim where helpful but produce normalized labels in {sourceLanguageHint}.\n";
        return languageLine + $$"""
You extract structured job requirements from a single job posting.

Return JSON only with this exact shape:
{
  "roleTitle": "string",
  "companyName": "string",
  "summary": "2-4 concise sentences",
  "requirements": [
    {
      "category": "Must have|Nice to have|Culture",
      "requirement": "short normalized label",
            "aliases": ["short alternative phrasing from the page"],
      "sourceSnippet": "verbatim or near-verbatim text from the page",
      "confidence": 1,
      "sourceUrl": "https://example.test/path"
    }
  ]
}

Rules:
- {{PromptConstraints.JsonOnlyOutput}}
- {{PromptConstraints.SourceTextBoundary}}
- Use concise normalized requirement labels.
- When helpful, include short aliases that reflect alternative phrasings or concrete terms appearing in the source text.
- Include only requirements and culture signals that are clearly grounded in the supplied page.
- Never emit conversational, navigation, or filler labels like "Links", "There is", "Of course", "Home", "Menu", or "Contact".
- Every requirement entry must include a supporting sourceSnippet and a confidence from 1 to 100.
- Keep summary under 550 characters.
- Do not invent employers, technologies, or company values that are not in the page.
""";
    }

    private static string BuildCompanySystemPrompt(string? sourceLanguageHint = null)
    {
        var languageLine = string.IsNullOrWhiteSpace(sourceLanguageHint)
            ? string.Empty
            : $"The source company pages are written in {sourceLanguageHint}; extract values verbatim where helpful but produce normalized labels in {sourceLanguageHint}.\n";
        return languageLine + $$"""
You extract structured company context from one or more company source pages.

Return JSON only with this exact shape:
{
  "name": "string",
  "summary": "2-4 concise sentences",
  "guidingPrinciples": ["Principle"],
  "differentiators": ["Differentiator"],
  "requirements": [
    {
      "category": "Nice to have|Culture",
      "requirement": "short normalized label",
            "aliases": ["short alternative phrasing from the sources"],
      "sourceSnippet": "verbatim or near-verbatim text from the sources",
      "confidence": 1,
      "sourceUrl": "https://example.test/path"
    }
  ]
}

Rules:
- {{PromptConstraints.JsonOnlyOutput}}
- {{PromptConstraints.SourceTextBoundary}}
- guidingPrinciples should capture the clearest company values or operating principles.
- differentiators should capture what makes the company, team, or role context distinctive.
- When helpful, include short aliases that reflect equivalent language used in the source text.
- requirements should only include fit-relevant company context that is clearly supported by a sourceSnippet.
- Never emit conversational, navigation, or filler labels like "Links", "There is", "Of course", "Home", "Menu", or "Contact".
- Every requirement entry must include a supporting sourceSnippet and a confidence from 1 to 100.
- Keep summary under 650 characters.
""";
    }

    private static string BuildJobUserPrompt(Uri jobPostingUrl, string title, string heading, string text)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Source URL: {jobPostingUrl}");
        builder.AppendLine($"HTML title hint: {title}");
        builder.AppendLine($"Main heading hint: {heading}");
        builder.AppendLine();
        builder.AppendLine(PromptConstraints.FormatSourceBlock("job posting page text", Clip(text, MaxJobContextCharacters)));
        return builder.ToString();
    }

    private static string BuildCompanyUserPrompt(IEnumerable<(Uri Url, string Text)> sourceDocuments)
    {
        var documents = sourceDocuments.ToArray();
        var builder = new StringBuilder();
        builder.AppendLine("Company source documents:");

        foreach (var (url, text) in documents)
        {
            builder.AppendLine();
            builder.AppendLine($"Source URL: {url}");
            builder.AppendLine(PromptConstraints.FormatSourceBlock(
                "company source page",
                Clip(text, MaxCompanyContextCharacters / Math.Max(1, documents.Length))));
        }

        return Clip(builder.ToString(), MaxCompanyContextCharacters);
    }

    private static string BuildPastedJobUserPrompt(string jobPostingText)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Source: pasted text (no URL available)");
        builder.AppendLine();
        builder.AppendLine(PromptConstraints.FormatSourceBlock("pasted job posting text", Clip(jobPostingText, MaxJobContextCharacters)));
        return builder.ToString();
    }

    private static string BuildPastedCompanyUserPrompt(string companyContextText)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Company source documents:");
        builder.AppendLine();
        builder.AppendLine("Source: pasted text (no URL available)");
        builder.AppendLine(PromptConstraints.FormatSourceBlock("pasted company context text", Clip(companyContextText, MaxCompanyContextCharacters)));
        return builder.ToString();
    }

    private static string ExtractJsonObject(string content)
    {
        var trimmed = content.Trim();

        trimmed = Regex.Replace(trimmed, @"<think>.*?</think>", string.Empty, RegexOptions.Singleline).Trim();

        trimmed = Regex.Replace(trimmed, @"```(?:json)?\s*\n?(.*?)\n?\s*```", "$1", RegexOptions.Singleline).Trim();

        // Remove trailing commas before ] or } (common LLM output quirk)
        trimmed = Regex.Replace(trimmed, @",\s*([\]\}])", "$1");

        // Fix period used instead of colon between JSON key and value ("key". "value" → "key": "value")
        trimmed = Regex.Replace(trimmed, @"(""\w+"")\s*\.\s*("")", "$1: $2");

        var balancedObject = TryExtractBalancedJsonObject(trimmed);
        if (!string.IsNullOrWhiteSpace(balancedObject))
        {
            return balancedObject;
        }

        return trimmed;
    }

    private static string? TryExtractBalancedJsonObject(string content)
    {
        var start = content.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var isEscaped = false;

        for (var index = start; index < content.Length; index++)
        {
            var current = content[index];

            if (isEscaped)
            {
                isEscaped = false;
                continue;
            }

            if (current == '\\')
            {
                isEscaped = true;
                continue;
            }

            if (current == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (current == '{')
            {
                depth++;
                continue;
            }

            if (current != '}')
            {
                continue;
            }

            depth--;
            if (depth == 0)
            {
                return content[start..(index + 1)];
            }
        }

        return null;
    }

    private static bool TryParseFallbackJobPostingAnalysis(
        string content,
        Uri? jobPostingUrl,
        string fallbackRoleTitle,
        string jobText,
        out JobPostingAnalysis analysis)
    {
        var roleTitle = ExtractLooseJsonStringValue(content, "roleTitle");
        var companyName = ExtractLooseJsonStringValue(content, "companyName");
        var summary = ExtractLooseJsonStringValue(content, "summary");
        var extractedSignals = JobSignalExtractor.Extract(jobText);

        var hasUsefulStructuredFields = !string.IsNullOrWhiteSpace(roleTitle)
            || !string.IsNullOrWhiteSpace(companyName)
            || !string.IsNullOrWhiteSpace(summary);

        if (!hasUsefulStructuredFields && extractedSignals.MustHaveThemes.Count == 0 && extractedSignals.NiceToHaveThemes.Count == 0 && extractedSignals.CulturalSignals.Count == 0)
        {
            analysis = null!;
            return false;
        }

        var sourceLabel = jobPostingUrl?.Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase) ?? "pasted text";
        var signals = BuildFallbackSignals(jobText, sourceLabel, extractedSignals);

        analysis = new JobPostingAnalysis
        {
            SourceUrl = jobPostingUrl,
            RoleTitle = string.IsNullOrWhiteSpace(roleTitle) ? fallbackRoleTitle : roleTitle,
            CompanyName = string.IsNullOrWhiteSpace(companyName)
                ? jobPostingUrl?.Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase) ?? "Unknown"
                : companyName,
            Summary = string.IsNullOrWhiteSpace(summary) ? Clip(jobText, 1_200) : Clip(summary, 1_200),
            MustHaveThemes = extractedSignals.MustHaveThemes,
            NiceToHaveThemes = extractedSignals.NiceToHaveThemes,
            CulturalSignals = extractedSignals.CulturalSignals,
            Signals = signals
        };

        return true;
    }

    private static bool TryParseFallbackCompanyResearchProfile(
        string content,
        IReadOnlyList<Uri> sourceUrls,
        IReadOnlyList<(Uri Url, string Text)> sourceDocuments,
        out CompanyResearchProfile profile)
    {
        var name = ExtractLooseJsonStringValue(content, "name");
        var summary = ExtractLooseJsonStringValue(content, "summary");

        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(summary))
        {
            profile = null!;
            return false;
        }

        var fallbackName = string.IsNullOrWhiteSpace(name)
            ? (sourceUrls.Count > 0
                ? sourceUrls[0].Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase)
                : "Unknown")
            : name;

        var fallbackSummary = string.IsNullOrWhiteSpace(summary)
            ? Clip(string.Join(Environment.NewLine + Environment.NewLine, sourceDocuments.Select(static item => item.Text)), 2_000)
            : Clip(summary, 2_000);

        profile = new CompanyResearchProfile
        {
            Name = fallbackName,
            Summary = fallbackSummary,
            SourceUrls = sourceUrls,
            GuidingPrinciples = Array.Empty<string>(),
            CulturalSignals = Array.Empty<string>(),
            Differentiators = Array.Empty<string>(),
            Signals = Array.Empty<JobContextSignal>()
        };

        return true;
    }

    private static JobContextSignal[] BuildFallbackSignals(string sourceText, string sourceLabel, JobSignalExtraction extraction)
    {
        var chunks = Regex.Split(sourceText, @"(?:\r?\n)+|(?<=[\.!?;:])\s+")
            .Select(static chunk => chunk.Trim())
            .Where(static chunk => !string.IsNullOrWhiteSpace(chunk))
            .ToArray();

        return extraction.MustHaveThemes
            .Select(requirement => BuildFallbackSignal(requirement, "Must have", JobRequirementImportance.MustHave, chunks, sourceLabel, 68))
            .Concat(extraction.NiceToHaveThemes.Select(requirement => BuildFallbackSignal(requirement, "Nice to have", JobRequirementImportance.NiceToHave, chunks, sourceLabel, 58)))
            .Concat(extraction.CulturalSignals.Select(requirement => BuildFallbackSignal(requirement, "Culture", JobRequirementImportance.Cultural, chunks, sourceLabel, 54)))
            .DistinctBy(static signal => $"{signal.Importance}:{signal.Requirement}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static JobContextSignal BuildFallbackSignal(
        string requirement,
        string category,
        JobRequirementImportance importance,
        IReadOnlyList<string> chunks,
        string sourceLabel,
        int confidence)
    {
        var aliases = JobSignalExtractor.GetAliases(requirement);
        var sourceSnippet = chunks.FirstOrDefault(chunk => aliases.Any(alias => JobSignalExtractor.ContainsTermOrOverlap(JobSignalExtractor.NormalizeText(chunk), alias)))
            ?? chunks.FirstOrDefault()
            ?? requirement;

        return new JobContextSignal(
            category,
            requirement,
            importance,
            sourceLabel,
            Clip(sourceSnippet, 260),
            confidence,
            aliases.Where(alias => !alias.Equals(requirement, StringComparison.OrdinalIgnoreCase)).ToArray());
    }

    private static string? ExtractLooseJsonStringValue(string content, string propertyName)
    {
        var match = Regex.Match(
            content,
            $"\"{Regex.Escape(propertyName)}\"\\s*[:.]\\s*\"(?<value>(?:\\\\.|[^\"\\\\])*)\"",
            RegexOptions.Singleline);

        if (!match.Success)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<string>($"\"{match.Groups["value"].Value}\"")?.Trim();
        }
        catch (JsonException)
        {
            return match.Groups["value"].Value.Trim();
        }
    }

    private static string Clip(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
        {
            return value.Trim();
        }

        return value[..maxLength].Trim() + "...";
    }

    private static string? ExtractMatch(string html, string pattern)
    {
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? StripHtml(match.Groups[1].Value).Trim() : null;
    }

    /// <summary>
    /// Regex patterns matching boilerplate HTML elements to strip before extraction.
    /// </summary>
    private static readonly Regex BoilerplateTagPattern = new(
        @"<(nav|header|footer|aside|noscript|iframe)[^>]*>.*?</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex CookieBannerPattern = new(
        @"<div[^>]*(cookie|consent|gdpr|privacy-banner|cc-banner)[^>]*>.*?</div>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Extracts semantically meaningful content from HTML, prioritizing requirement-dense
    /// sections and structured lists while dropping navigation, footers, and cookie banners.
    /// Falls back to <see cref="StripHtml"/> if semantic extraction yields too little content.
    /// </summary>
    private static string ExtractSemanticContent(string html)
    {
        // Strip script, style, and boilerplate regions first.
        var cleaned = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        cleaned = BoilerplateTagPattern.Replace(cleaned, string.Empty);
        cleaned = CookieBannerPattern.Replace(cleaned, string.Empty);

        // Extract semantic sections: split on headings (h1-h6), keep content blocks.
        var sections = Regex.Split(cleaned, @"(?=<h[1-6][^>]*>)", RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .Select(section =>
            {
                var headingMatch = Regex.Match(section, @"<h[1-6][^>]*>(.*?)</h[1-6]>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                var heading = headingMatch.Success ? StripHtml(headingMatch.Groups[1].Value).Trim() : string.Empty;
                var body = StripHtml(section);
                return new { Heading = heading, Body = body, Length = body.Length };
            })
            .Where(section => !string.IsNullOrWhiteSpace(section.Body) && section.Length > 20)
            .ToArray();

        if (sections.Length == 0)
        {
            return StripHtml(html);
        }

        // Score sections: requirement-dense headings get priority.
        var requirementHeadingKeywords = new[] {
            "requirement", "qualification", "krav", "erfaring", "competenc", "skill",
            "responsibility", "about the role", "what we", "you will", "du vil",
            "what you", "om stillingen", "vi søger", "we are looking"
        };

        var prioritized = sections
            .Select(section =>
            {
                var score = section.Length;
                var headingLower = section.Heading.ToLowerInvariant();

                // Boost sections whose headings indicate requirements.
                if (requirementHeadingKeywords.Any(keyword => headingLower.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                {
                    score += 5000;
                }

                // Boost sections containing list items (ul/li patterns in text).
                var listItemCount = Regex.Matches(section.Body, @"^\s*[-•●◦]\s", RegexOptions.Multiline).Count;
                score += listItemCount * 200;

                return new { section.Heading, section.Body, Score = score };
            })
            .OrderByDescending(s => s.Score)
            .ToArray();

        // Assemble up to the char budget, taking highest-scoring sections first.
        var result = new StringBuilder();
        foreach (var section in prioritized)
        {
            if (result.Length + section.Body.Length > MaxJobContextCharacters)
            {
                var remaining = MaxJobContextCharacters - result.Length;
                if (remaining > 100)
                {
                    if (!string.IsNullOrWhiteSpace(section.Heading))
                    {
                        result.AppendLine($"[{section.Heading}]");
                    }
                    result.AppendLine(section.Body[..remaining]);
                }
                break;
            }

            if (!string.IsNullOrWhiteSpace(section.Heading))
            {
                result.AppendLine($"[{section.Heading}]");
            }
            result.AppendLine(section.Body);
            result.AppendLine();
        }

        var semanticText = result.ToString().Trim();
        return semanticText.Length >= 200 ? semanticText : StripHtml(html);
    }

    private static string StripHtml(string html)
    {
        var withLineBreaks = Regex.Replace(html, "<(br|/p|/div|/section|/article|/li|/ul|/ol|/h[1-6])[^>]*>", "\n", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var noScript = Regex.Replace(withLineBreaks, "<(script|style)[^>]*>.*?</\\1>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var withoutTags = Regex.Replace(noScript, "<[^>]+>", " ", RegexOptions.Singleline);
        var normalizedSpaces = Regex.Replace(withoutTags, "[ \t]+", " ");
        return WebUtility.HtmlDecode(Regex.Replace(normalizedSpaces, @"\s*\n\s*", "\n")).Trim();
    }
}
