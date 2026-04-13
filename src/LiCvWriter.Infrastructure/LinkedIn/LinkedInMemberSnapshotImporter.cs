using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;

namespace LiCvWriter.Infrastructure.LinkedIn;

public sealed class LinkedInMemberSnapshotImporter(HttpClient httpClient, LinkedInAuthOptions options, TimeProvider timeProvider)
{
    private const int VersionFallbackMonths = 36;
    private static readonly Uri SnapshotEndpoint = new("https://api.linkedin.com/rest/memberSnapshotData?q=criteria");
    private static readonly IReadOnlyDictionary<string, SnapshotDomainRoute> SupportedDomainRoutes = new Dictionary<string, SnapshotDomainRoute>(StringComparer.OrdinalIgnoreCase)
    {
        ["PROFILE"] = SnapshotDomainRoute.Typed(LinkedInExportFileMap.Profile),
        ["POSITIONS"] = SnapshotDomainRoute.Typed(LinkedInExportFileMap.Positions),
        ["EDUCATION"] = SnapshotDomainRoute.Typed(LinkedInExportFileMap.Education),
        ["SKILLS"] = SnapshotDomainRoute.Typed(LinkedInExportFileMap.Skills),
        ["CERTIFICATIONS"] = SnapshotDomainRoute.Typed(LinkedInExportFileMap.Certifications),
        ["PROJECTS"] = SnapshotDomainRoute.Typed(LinkedInExportFileMap.Projects),
        ["RECOMMENDATIONS"] = SnapshotDomainRoute.Typed(LinkedInExportFileMap.Recommendations),
        ["VOLUNTEERING_EXPERIENCES"] = SnapshotDomainRoute.Enrichment(LinkedInExportFileMap.VolunteeringExperiences),
        ["LANGUAGES"] = SnapshotDomainRoute.Enrichment(LinkedInExportFileMap.Languages),
        ["PUBLICATIONS"] = SnapshotDomainRoute.Enrichment(LinkedInExportFileMap.Publications),
        ["PATENTS"] = SnapshotDomainRoute.Enrichment(LinkedInExportFileMap.Patents),
        ["HONORS"] = SnapshotDomainRoute.Enrichment(LinkedInExportFileMap.Honors),
        ["COURSES"] = SnapshotDomainRoute.Enrichment(LinkedInExportFileMap.Courses),
        ["ORGANIZATIONS"] = SnapshotDomainRoute.Enrichment(LinkedInExportFileMap.Organizations)
    };

    private static readonly IReadOnlySet<string> IgnoredDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ACCOUNT_HISTORY",
        "ACTOR_SAVE_ITEM",
        "ADS_CLICKED",
        "ADS_LAN",
        "AD_TARGETING",
        "ALL_COMMENTS",
        "ALL_LIKES",
        "ALL_VOTES",
        "ARTICLES",
        "CAUSES_YOU_CARE_ABOUT",
        "COMPANY_FOLLOWS",
        "CONNECTIONS",
        "CONTACTS",
        "EMAIL_ADDRESSES",
        "ENDORSEMENTS",
        "Events",
        "easyapply-blocking",
        "GROUPS",
        "IDENTITY_CREDENTIALS_AND_ASSETS",
        "INBOX",
        "INFERENCE_TAKEOUT",
        "INSTANT_REPOSTS",
        "INVITATIONS",
        "JOB_APPLICATIONS",
        "JOB_APPLICANT_SAVED_ANSWERS",
        "JOB_POSTINGS",
        "JOB_SEEKER_PREFERENCES",
        "LEARNING",
        "LEARNING_COACH_AI_TAKEOUT",
        "LEARNING_COACH_INBOX",
        "LEARNING_ROLEPLAY_INBOX",
        "login",
        "MARKETPLACE_ENGAGEMENTS",
        "MARKETPLACE_OPPORTUNITIES",
        "MARKETPLACE_PROVIDERS",
        "MEMBER_FOLLOWING",
        "MEMBER_SHARE_INFO",
        "PHONE_NUMBERS",
        "PROFILE_SUMMARY",
        "RECEIPTS",
        "RECEIPTS_LBP",
        "REGISTRATION",
        "REVIEWS",
        "RICH_MEDIA",
        "SAVED_JOBS",
        "SAVED_JOB_ALERTS",
        "SEARCHES",
        "SECURITY_CHALLENGE_PIPE",
        "TALENT_QUESTION_SAVED_RESPONSE",
        "TEST_SCORES",
        "TRUSTED_GRAPH",
        "WHATSAPP_NUMBERS"
    };

    public async Task<LinkedInExportImportResult> ImportAsync(
        string accessToken,
        Func<string, CancellationToken, Task<LinkedInExportImportResult>> importExportAsync,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ArgumentException("A LinkedIn DMA access token is required.", nameof(accessToken));
        }

        ArgumentNullException.ThrowIfNull(importExportAsync);

        var exportRoot = Path.Combine(Path.GetTempPath(), $"licvwriter-linkedin-dma-{Guid.NewGuid():N}");
        Directory.CreateDirectory(exportRoot);

        try
        {
            var warnings = new List<string>();
            onProgress?.Invoke("Fetching snapshot pages from LinkedIn API");
            var domainData = await FetchSnapshotDataAsync(NormalizeAccessToken(accessToken), warnings, onProgress, cancellationToken);
            onProgress?.Invoke("Writing imported domain data");
            WriteMappedCsvFiles(exportRoot, domainData, warnings);

            onProgress?.Invoke("Parsing imported profile data");
            var imported = await importExportAsync(exportRoot, cancellationToken);
            var mergedWarnings = imported.Warnings
                .Concat(warnings)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            return imported with
            {
                Inspection = imported.Inspection with { RootPath = "LinkedIn DMA member snapshot API", Warnings = mergedWarnings },
                Warnings = mergedWarnings,
                SourceDescription = "LinkedIn DMA member snapshot API"
            };
        }
        finally
        {
            try
            {
                if (Directory.Exists(exportRoot))
                {
                    Directory.Delete(exportRoot, recursive: true);
                }
            }
            catch
            {
                // Ignore temp cleanup failures.
            }
        }
    }

    private async Task<Dictionary<string, List<Dictionary<string, string>>>> FetchSnapshotDataAsync(
        string accessToken,
        List<string> warnings,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        var data = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        Uri? nextUri = SnapshotEndpoint;
        var pageCount = 0;
        var versionCandidates = GetApiVersionCandidates().ToArray();
        var selectedVersionIndex = 0;
        var selectedVersion = versionCandidates[0];

        while (nextUri is not null)
        {
            using var request = BuildSnapshotRequest(nextUri, accessToken, selectedVersion);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                if (pageCount == 0
                    && response.StatusCode == HttpStatusCode.UpgradeRequired
                    && HasNonexistentVersionError(payload)
                    && selectedVersionIndex < versionCandidates.Length - 1)
                {
                    selectedVersionIndex++;
                    selectedVersion = versionCandidates[selectedVersionIndex];
                    continue;
                }

                if (pageCount == 0
                    && response.StatusCode == HttpStatusCode.UpgradeRequired
                    && HasNonexistentVersionError(payload))
                {
                    throw new InvalidOperationException(
                        $"LinkedIn DMA member snapshot request failed because no active API version was found. Attempted Linkedin-Version values: {string.Join(", ", versionCandidates.Take(selectedVersionIndex + 1))}. Raw response: {payload}".Trim());
                }

                if (response.StatusCode == HttpStatusCode.NotFound && payload.Contains("No data found for this memberId", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                throw new InvalidOperationException(
                    $"LinkedIn DMA member snapshot request failed with {(int)response.StatusCode} {response.ReasonPhrase}. {payload}".Trim());
            }

            pageCount++;
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            foreach (var element in EnumerateElements(root))
            {
                var domain = ReadString(element, "snapshotDomain");
                if (string.IsNullOrWhiteSpace(domain))
                {
                    warnings.Add($"A LinkedIn DMA snapshot page returned an element without snapshotDomain on page {pageCount}.");
                    continue;
                }

                if (!data.TryGetValue(domain, out var records))
                {
                    records = new List<Dictionary<string, string>>();
                    data[domain] = records;
                    onProgress?.Invoke($"Importing domain: {domain}");
                }

                foreach (var record in EnumerateSnapshotData(element))
                {
                    records.Add(record);
                }
            }

            nextUri = ReadNextPageUri(root);
        }

        return data;
    }

    private HttpRequestMessage BuildSnapshotRequest(Uri nextUri, string accessToken, string apiVersion)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, nextUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Linkedin-Version", apiVersion);
        request.Headers.TryAddWithoutValidation("X-Restli-Protocol-Version", "2.0.0");
        return request;
    }

    private IEnumerable<string> GetApiVersionCandidates()
    {
        var candidates = new List<string>();
        AddCandidate(candidates, NormalizeApiVersion(options.PortabilityApiVersion));

        var now = timeProvider.GetUtcNow();
        var currentMonth = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        for (var offset = 0; offset < VersionFallbackMonths; offset++)
        {
            AddCandidate(candidates, currentMonth.AddMonths(-offset).ToString("yyyyMM"));
        }

        if (candidates.Count == 0)
        {
            candidates.Add(currentMonth.ToString("yyyyMM"));
        }

        return candidates;
    }

    private static void AddCandidate(List<string> candidates, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidates.Contains(candidate, StringComparer.Ordinal))
        {
            return;
        }

        candidates.Add(candidate);
    }

    private static string? NormalizeApiVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digitsOnly = new string(value.Where(char.IsDigit).ToArray());
        if (digitsOnly.Length >= 6)
        {
            return digitsOnly[..6];
        }

        return null;
    }

    private static bool HasNonexistentVersionError(string payload)
    {
        if (payload.Contains("NONEXISTENT_VERSION", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                && string.Equals(code.GetString(), "NONEXISTENT_VERSION", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void WriteMappedCsvFiles(
        string exportRoot,
        IReadOnlyDictionary<string, List<Dictionary<string, string>>> domainData,
        List<string> warnings)
    {
        var writtenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedDomains = new List<string>();

        foreach (var pair in domainData)
        {
            if (pair.Value.Count == 0)
            {
                continue;
            }

            if (!TryResolveDomainRoute(pair.Key, out var route))
            {
                skippedDomains.Add(pair.Key);
                continue;
            }

            if (route.Disposition == SnapshotDomainDisposition.Ignored)
            {
                continue;
            }

            WriteCsv(Path.Combine(exportRoot, route.FileName), pair.Value);
            writtenFiles.Add(route.FileName);
        }

        if (skippedDomains.Count > 0)
        {
            var unsupportedDomains = skippedDomains
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static domain => domain, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var preview = unsupportedDomains.Take(8).ToArray();
            var suffix = unsupportedDomains.Length > preview.Length ? ", ..." : string.Empty;
            warnings.Add($"LinkedIn DMA snapshot skipped unsupported domains: {string.Join(", ", preview)}{suffix}");
        }

        foreach (var requiredFile in LinkedInExportFileMap.FirstClassFiles)
        {
            if (!writtenFiles.Contains(requiredFile))
            {
                warnings.Add($"LinkedIn DMA snapshot did not include data for {requiredFile}.");
            }
        }

        if (writtenFiles.Count == 0)
        {
            warnings.Add("LinkedIn DMA snapshot did not include any domains that LI CV Writer currently maps into a candidate profile.");
        }
    }

    private static bool TryResolveDomainRoute(string domain, out SnapshotDomainRoute route)
    {
        if (SupportedDomainRoutes.TryGetValue(domain, out route!))
        {
            return true;
        }

        if (IgnoredDomains.Contains(domain))
        {
            route = SnapshotDomainRoute.Ignored;
            return true;
        }

        route = default!;
        return false;
    }

    private static IEnumerable<JsonElement> EnumerateElements(JsonElement root)
    {
        if (!root.TryGetProperty("elements", out var elements))
        {
            yield break;
        }

        if (elements.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in elements.EnumerateArray())
            {
                yield return element;
            }

            yield break;
        }

        if (elements.ValueKind == JsonValueKind.Object)
        {
            yield return elements;
        }
    }

    private static IEnumerable<Dictionary<string, string>> EnumerateSnapshotData(JsonElement element)
    {
        if (!element.TryGetProperty("snapshotData", out var snapshotData) || snapshotData.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in snapshotData.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var record = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in item.EnumerateObject())
            {
                record[property.Name] = ConvertValueToString(property.Value);
            }

            yield return record;
        }
    }

    private static Uri? ReadNextPageUri(JsonElement root)
    {
        if (!root.TryGetProperty("paging", out var paging) || paging.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!paging.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var link in links.EnumerateArray())
        {
            if (!string.Equals(ReadString(link, "rel"), "next", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var href = ReadString(link, "href");
            if (string.IsNullOrWhiteSpace(href))
            {
                return null;
            }

            return Uri.TryCreate(href, UriKind.Absolute, out var absoluteUri)
                ? absoluteUri
                : new Uri(new Uri("https://api.linkedin.com"), href);
        }

        return null;
    }

    private static void WriteCsv(string path, IReadOnlyList<Dictionary<string, string>> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        var headers = records
            .SelectMany(static record => record.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', headers.Select(EscapeCsv)));

        foreach (var record in records)
        {
            builder.AppendLine(string.Join(',', headers.Select(header => EscapeCsv(record.TryGetValue(header, out var value) ? value : string.Empty))));
        }

        File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
    }

    private static string EscapeCsv(string? value)
    {
        var normalized = value ?? string.Empty;
        return normalized.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{normalized.Replace("\"", "\"\"")}\""
            : normalized;
    }

    private static string NormalizeAccessToken(string accessToken)
    {
        var normalized = accessToken.Trim();
        const string bearerPrefix = "Bearer ";

        return normalized.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[bearerPrefix.Length..].Trim()
            : normalized;
    }

    private static string ConvertValueToString(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Null => string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.ToString(),
            _ => value.GetRawText()
        };

    private static string? ReadString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private sealed record SnapshotDomainRoute(string FileName, SnapshotDomainDisposition Disposition)
    {
        public static SnapshotDomainRoute Typed(string fileName) => new(fileName, SnapshotDomainDisposition.Typed);

        public static SnapshotDomainRoute Enrichment(string fileName) => new(fileName, SnapshotDomainDisposition.Enrichment);

        public static SnapshotDomainRoute Ignored { get; } = new(string.Empty, SnapshotDomainDisposition.Ignored);
    }

    private enum SnapshotDomainDisposition
    {
        Typed,
        Enrichment,
        Ignored
    }
}