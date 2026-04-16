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

namespace LiCvWriter.Infrastructure.Research;

public sealed class HttpJobResearchService(HttpClient httpClient, ILlmClient llmClient, OllamaOptions ollamaOptions) : IJobResearchService
{
    private const int MaxJobContextCharacters = 8_000;
    private const int MaxCompanyContextCharacters = 12_000;
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36";
    private const string HtmlAcceptHeader = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
    private const string AcceptLanguageHeader = "en-US,en;q=0.9";

    public async Task<JobPostingAnalysis> AnalyzeAsync(
        Uri jobPostingUrl,
        string? selectedModel = null,
        string? selectedThinkingLevel = null,
        Action<LlmProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await ValidatePublicHttpsUriAsync(jobPostingUrl, cancellationToken);
        var html = await FetchHtmlAsync(jobPostingUrl, cancellationToken);
        var title = ExtractMatch(html, "<title[^>]*>(.*?)</title>") ?? jobPostingUrl.Host;
        var heading = ExtractMatch(html, "<h1[^>]*>(.*?)</h1>") ?? title;
        var text = StripHtml(html);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("The job page did not contain enough readable text to parse.");
        }

        var resolvedModel = ResolveModel(selectedModel);
        var response = await llmClient.GenerateAsync(
            new LlmRequest(
                resolvedModel,
                BuildJobSystemPrompt(),
                [new LlmChatMessage("user", BuildJobUserPrompt(jobPostingUrl, title, heading, text))],
                UseChatEndpoint: ollamaOptions.UseChatEndpoint,
                Stream: true,
                Think: ResolveThinkingLevel(selectedThinkingLevel),
                KeepAlive: ollamaOptions.KeepAlive,
                Temperature: 0.1),
            progress is null ? null : update => progress(update with
            {
                Message = "Parsing job posting",
                Detail = string.IsNullOrWhiteSpace(update.Detail)
                    ? $"Structured job parsing is running via {update.Model}."
                    : update.Detail
            }),
            cancellationToken);

        return ParseJobPostingAnalysis(response.Content, jobPostingUrl, heading, text);
    }

    public async Task<CompanyResearchProfile> BuildCompanyProfileAsync(
        IEnumerable<Uri> sourceUrls,
        string? selectedModel = null,
        string? selectedThinkingLevel = null,
        Action<LlmProgressUpdate>? progress = null,
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
                BuildCompanySystemPrompt(),
                [new LlmChatMessage("user", BuildCompanyUserPrompt(sourceDocuments))],
                UseChatEndpoint: ollamaOptions.UseChatEndpoint,
                Stream: true,
                Think: ResolveThinkingLevel(selectedThinkingLevel),
                KeepAlive: ollamaOptions.KeepAlive,
                Temperature: 0.1),
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
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobPostingText))
        {
            throw new InvalidOperationException("The pasted job posting text is empty.");
        }

        var resolvedModel = ResolveModel(selectedModel);
        var response = await llmClient.GenerateAsync(
            new LlmRequest(
                resolvedModel,
                BuildJobSystemPrompt(),
                [new LlmChatMessage("user", BuildPastedJobUserPrompt(jobPostingText))],
                UseChatEndpoint: ollamaOptions.UseChatEndpoint,
                Stream: true,
                Think: ResolveThinkingLevel(selectedThinkingLevel),
                KeepAlive: ollamaOptions.KeepAlive,
                Temperature: 0.1),
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
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(companyContextText))
        {
            throw new InvalidOperationException("The pasted company context text is empty.");
        }

        var response = await llmClient.GenerateAsync(
            new LlmRequest(
                ResolveModel(selectedModel),
                BuildCompanySystemPrompt(),
                [new LlmChatMessage("user", BuildPastedCompanyUserPrompt(companyContextText))],
                UseChatEndpoint: ollamaOptions.UseChatEndpoint,
                Stream: true,
                Think: ResolveThinkingLevel(selectedThinkingLevel),
                KeepAlive: ollamaOptions.KeepAlive,
                Temperature: 0.1),
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

        return new JobContextSignal(
            category,
            requirement,
            importance,
            ResolveSourceLabel(ReadOptionalString(element, "sourceUrl"), defaultSourceLabel),
            Clip(sourceSnippet, 260),
            Math.Clamp(confidence.Value, 1, 100),
            NormalizeAliases(requirement, aliases));
    }

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

    private string ResolveModel(string? selectedModel)
        => string.IsNullOrWhiteSpace(selectedModel) ? ollamaOptions.Model : selectedModel.Trim();

    private string ResolveThinkingLevel(string? selectedThinkingLevel)
        => string.IsNullOrWhiteSpace(selectedThinkingLevel) ? ollamaOptions.Think : selectedThinkingLevel.Trim();

    private static string BuildJobSystemPrompt()
        => """
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
- Return valid JSON only. No markdown fences.
- Use concise normalized requirement labels.
- When helpful, include short aliases that reflect alternative phrasings or concrete terms appearing in the source text.
- Include only requirements and culture signals that are clearly grounded in the supplied page.
- Every requirement entry must include a supporting sourceSnippet and a confidence from 1 to 100.
- Keep summary under 550 characters.
- Do not invent employers, technologies, or company values that are not in the page.
""";

    private static string BuildCompanySystemPrompt()
        => """
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
- Return valid JSON only. No markdown fences.
- guidingPrinciples should capture the clearest company values or operating principles.
- differentiators should capture what makes the company, team, or role context distinctive.
- When helpful, include short aliases that reflect equivalent language used in the source text.
- requirements should only include fit-relevant company context that is clearly supported by a sourceSnippet.
- Every requirement entry must include a supporting sourceSnippet and a confidence from 1 to 100.
- Keep summary under 650 characters.
""";

    private static string BuildJobUserPrompt(Uri jobPostingUrl, string title, string heading, string text)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Source URL: {jobPostingUrl}");
        builder.AppendLine($"HTML title hint: {title}");
        builder.AppendLine($"Main heading hint: {heading}");
        builder.AppendLine();
        builder.AppendLine("Page text:");
        builder.AppendLine(Clip(text, MaxJobContextCharacters));
        return builder.ToString();
    }

    private static string BuildCompanyUserPrompt(IEnumerable<(Uri Url, string Text)> sourceDocuments)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Company source documents:");

        foreach (var (url, text) in sourceDocuments)
        {
            builder.AppendLine();
            builder.AppendLine($"Source URL: {url}");
            builder.AppendLine(Clip(text, MaxCompanyContextCharacters / Math.Max(1, sourceDocuments.Count())));
        }

        return Clip(builder.ToString(), MaxCompanyContextCharacters);
    }

    private static string BuildPastedJobUserPrompt(string jobPostingText)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Source: pasted text (no URL available)");
        builder.AppendLine();
        builder.AppendLine("Job posting text:");
        builder.AppendLine(Clip(jobPostingText, MaxJobContextCharacters));
        return builder.ToString();
    }

    private static string BuildPastedCompanyUserPrompt(string companyContextText)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Company source documents:");
        builder.AppendLine();
        builder.AppendLine("Source: pasted text (no URL available)");
        builder.AppendLine(Clip(companyContextText, MaxCompanyContextCharacters));
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

    private static string StripHtml(string html)
    {
        var withLineBreaks = Regex.Replace(html, "<(br|/p|/div|/section|/article|/li|/ul|/ol|/h[1-6])[^>]*>", "\n", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var noScript = Regex.Replace(withLineBreaks, "<(script|style)[^>]*>.*?</\\1>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var withoutTags = Regex.Replace(noScript, "<[^>]+>", " ", RegexOptions.Singleline);
        var normalizedSpaces = Regex.Replace(withoutTags, "[ \t]+", " ");
        return WebUtility.HtmlDecode(Regex.Replace(normalizedSpaces, @"\s*\n\s*", "\n")).Trim();
    }
}
