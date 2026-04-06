using System.Net;
using LiCvWriter.Application.Services;
using LiCvWriter.Infrastructure.Csv;
using LiCvWriter.Infrastructure.LinkedIn;
using LiCvWriter.Application.Options;

namespace LiCvWriter.Tests.LinkedIn;

public sealed class LinkedInExportImporterTests
{
    [Fact]
    public async Task ImportAsync_MapsExpectedCoreFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Profile.csv"), "First Name,Last Name,Headline,Summary,Industry,Geo Location\nAlex,Taylor,Headline,Summary,Consulting,Remote\n");
            await File.WriteAllTextAsync(Path.Combine(root, "Positions.csv"), "Company Name,Title,Description,Location,Started On,Finished On\nContoso Consulting,Senior Architect,Description,Remote,Oct 2025,\n");
            await File.WriteAllTextAsync(Path.Combine(root, "Education.csv"), "School Name,Start Date,End Date,Notes,Degree Name,Activities\nState University,1992,1996,,MSc Computer Science,\n");
            await File.WriteAllTextAsync(Path.Combine(root, "Skills.csv"), "Name\nAutomation\nArchitecture\n");
            await File.WriteAllTextAsync(Path.Combine(root, "Certifications.csv"), "Name,Url,Authority,Started On,Finished On,License Number\nCloud Architecture,http://example.com,Example Institute,Aug 2014,Aug 2017,123\n");
            await File.WriteAllTextAsync(Path.Combine(root, "Projects.csv"), "Title,Description,Url,Started On,Finished On\nProject X,Description,http://example.com,Sep 2016,Mar 2017\n");
            await File.WriteAllTextAsync(Path.Combine(root, "Recommendations_Received.csv"), "First Name,Last Name,Company,Job Title,Text,Creation Date,Status\nJordan,Morgan,Example Advisory,Managing Director,Strong recommendation,03/29/26, VISIBLE\n");

            var importer = new LinkedInExportImporter(new SimpleCsvParser(), new LinkedInPartialDateParser(), CreateSnapshotImporter());

            var result = await importer.ImportAsync(root);

            Assert.Equal("Alex Taylor", result.Profile.Name.FullName);
            Assert.Single(result.Profile.Experience);
            Assert.Equal(2, result.Profile.Skills.Count);
            Assert.Single(result.Profile.Certifications);
            Assert.Single(result.Profile.Projects);
            Assert.Single(result.Profile.Recommendations);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportAsync_SortsExperienceByRecency()
    {
        var root = Path.Combine(Path.GetTempPath(), $"licvwriter-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Profile.csv"), "First Name,Last Name\nAlex,Taylor\n");
            await File.WriteAllTextAsync(Path.Combine(root, "Positions.csv"), "Company Name,Title,Description,Location,Started On,Finished On\nContoso,Older role,Old description,Remote,Jan 2019,Dec 2021\nFabrikam,Current role,Current description,Remote,Jan 2023,\nNorthwind,Recent finished role,Recent description,Remote,Jan 2022,Dec 2022\n");
            await File.WriteAllTextAsync(Path.Combine(root, "Education.csv"), "School Name,Start Date,End Date,Notes,Degree Name,Activities\nState University,1992,1996,,MSc Computer Science,\n");
            await File.WriteAllTextAsync(Path.Combine(root, "Skills.csv"), "Name\nArchitecture\n");
            await File.WriteAllTextAsync(Path.Combine(root, "Certifications.csv"), "Name,Url,Authority,Started On,Finished On,License Number\nCloud Architecture,http://example.com,Example Institute,Aug 2014,Aug 2017,123\n");

            var importer = new LinkedInExportImporter(new SimpleCsvParser(), new LinkedInPartialDateParser(), CreateSnapshotImporter());

            var result = await importer.ImportAsync(root);

            Assert.Equal(["Current role", "Recent finished role", "Older role"], result.Profile.Experience.Select(static role => role.Title).ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportMemberSnapshotAsync_MapsPagedSnapshotDomainsIntoCandidateProfile()
    {
        var requests = new List<HttpRequestMessage>();
        using var httpClient = new HttpClient(new StubMessageHandler(request =>
        {
            requests.Add(CloneRequest(request));

            var responseBody = request.RequestUri?.Query.Contains("start=1", StringComparison.Ordinal) == true
                ? """
                    {
                        "paging": { "start": 1, "count": 10, "links": [], "total": 2 },
                        "elements": [
                            {
                                "snapshotDomain": "POSITIONS",
                                "snapshotData": [
                                    {
                                        "Company Name": "Contoso Consulting",
                                        "Title": "Principal Consultant",
                                        "Description": "Led delivery",
                                        "Location": "Remote",
                                        "Started On": "Jan 2024",
                                        "Finished On": ""
                                    }
                                ]
                            },
                            {
                                "snapshotDomain": "SKILLS",
                                "snapshotData": [
                                    { "Name": "Architecture" },
                                    { "Name": "Automation" }
                                ]
                            }
                        ]
                    }
                    """
                : """
                    {
                        "paging": {
                            "start": 0,
                            "count": 10,
                            "links": [
                                {
                                    "rel": "next",
                                    "href": "/rest/memberSnapshotData?q=criteria&start=1"
                                }
                            ],
                            "total": 2
                        },
                        "elements": [
                            {
                                "snapshotDomain": "PROFILE",
                                "snapshotData": [
                                    {
                                        "First Name": "Alex",
                                        "Last Name": "Taylor",
                                        "Headline": "Consultant",
                                        "Summary": "Builds local-first tools",
                                        "Industry": "Consulting",
                                        "Geo Location": "Remote"
                                    }
                                ]
                            }
                        ]
                    }
                    """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            };
        }));

        var snapshotImporter = new LinkedInMemberSnapshotImporter(httpClient, new LinkedInAuthOptions
        {
            PortabilityApiVersion = "202504"
        }, TimeProvider.System);
        var importer = new LinkedInExportImporter(new SimpleCsvParser(), new LinkedInPartialDateParser(), snapshotImporter);

        var result = await importer.ImportMemberSnapshotAsync("Bearer dma-token");

        Assert.Equal("Alex Taylor", result.Profile.Name.FullName);
        Assert.Equal("Consultant", result.Profile.Headline);
        Assert.Single(result.Profile.Experience);
        Assert.Equal(2, result.Profile.Skills.Count);
        Assert.Equal("LinkedIn DMA member snapshot API", result.SourceDescription);
        Assert.Equal("Bearer", requests[0].Headers.Authorization?.Scheme);
        Assert.Equal("dma-token", requests[0].Headers.Authorization?.Parameter);
        Assert.Equal("202504", requests[0].Headers.GetValues("Linkedin-Version").Single());
        Assert.Equal("2.0.0", requests[0].Headers.GetValues("X-Restli-Protocol-Version").Single());
    }

    [Fact]
    public async Task ImportMemberSnapshotAsync_WhenConfiguredVersionIsInactive_RetriesWithRecentMonthlyVersion()
    {
        var requests = new List<HttpRequestMessage>();
        var fallbackVersion = DateTime.UtcNow.ToString("yyyyMM");

        using var httpClient = new HttpClient(new StubMessageHandler(request =>
        {
            requests.Add(CloneRequest(request));
            var requestedVersion = request.Headers.GetValues("Linkedin-Version").Single();

            if (requestedVersion == "202504")
            {
                return new HttpResponseMessage(HttpStatusCode.UpgradeRequired)
                {
                    Content = new StringContent("{\"status\":426,\"code\":\"NONEXISTENT_VERSION\",\"message\":\"Requested version 20250401 is not active\"}")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                        "paging": { "start": 0, "count": 10, "links": [], "total": 1 },
                        "elements": [
                            {
                                "snapshotDomain": "PROFILE",
                                "snapshotData": [
                                    {
                                        "First Name": "Alex",
                                        "Last Name": "Taylor",
                                        "Headline": "Consultant"
                                    }
                                ]
                            }
                        ]
                    }
                    """)
            };
        }));

        var snapshotImporter = new LinkedInMemberSnapshotImporter(httpClient, new LinkedInAuthOptions
        {
            PortabilityApiVersion = "20250401"
        }, TimeProvider.System);
        var importer = new LinkedInExportImporter(new SimpleCsvParser(), new LinkedInPartialDateParser(), snapshotImporter);

        var result = await importer.ImportMemberSnapshotAsync("dma-token");

        Assert.Equal("Alex Taylor", result.Profile.Name.FullName);
        Assert.Equal(2, requests.Count);
        Assert.Equal("202504", requests[0].Headers.GetValues("Linkedin-Version").Single());
        Assert.Equal(fallbackVersion, requests[1].Headers.GetValues("Linkedin-Version").Single());
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("inactive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ImportMemberSnapshotAsync_WritesFormattedExperienceToConsole()
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();

        try
        {
            Console.SetOut(writer);

            using var httpClient = new HttpClient(new StubMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                        "paging": { "start": 0, "count": 10, "links": [], "total": 1 },
                        "elements": [
                            {
                                "snapshotDomain": "PROFILE",
                                "snapshotData": [
                                    {
                                        "First Name": "Alex",
                                        "Last Name": "Taylor"
                                    }
                                ]
                            },
                            {
                                "snapshotDomain": "POSITIONS",
                                "snapshotData": [
                                    {
                                        "Company Name": "Contoso Consulting",
                                        "Title": "Principal Consultant",
                                        "Description": "Led delivery\nShaped architecture",
                                        "Location": "Remote",
                                        "Started On": "Jan 2024",
                                        "Finished On": ""
                                    }
                                ]
                            }
                        ]
                    }
                    """)
            }));

            var snapshotImporter = new LinkedInMemberSnapshotImporter(httpClient, new LinkedInAuthOptions(), TimeProvider.System);
            var importer = new LinkedInExportImporter(new SimpleCsvParser(), new LinkedInPartialDateParser(), snapshotImporter);

            await importer.ImportMemberSnapshotAsync("dma-token");

            var output = writer.ToString();
            Assert.Contains("LinkedIn DMA Imported Experience", output, StringComparison.Ordinal);
            Assert.Contains("1. Principal Consultant @ Contoso Consulting", output, StringComparison.Ordinal);
            Assert.Contains("Period: Jan 2024 - Present", output, StringComparison.Ordinal);
            Assert.Contains("Location: Remote", output, StringComparison.Ordinal);
            Assert.Contains("Led delivery", output, StringComparison.Ordinal);
            Assert.Contains("Shaped architecture", output, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task ImportMemberSnapshotAsync_IgnoresConfiguredDomainsWhilePreservingOtherEnrichmentDomains()
    {
        using var httpClient = new HttpClient(new StubMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                    "paging": { "start": 0, "count": 10, "links": [], "total": 1 },
                    "elements": [
                        {
                            "snapshotDomain": "PROFILE",
                            "snapshotData": [
                                {
                                    "First Name": "Alex",
                                    "Last Name": "Taylor",
                                    "Headline": "Consultant"
                                }
                            ]
                        },
                        {
                            "snapshotDomain": "LANGUAGES",
                            "snapshotData": [
                                {
                                    "Name": "English",
                                    "Proficiency": "Native"
                                },
                                {
                                    "Name": "French",
                                    "Proficiency": "Professional working"
                                }
                            ]
                        },
                        {
                            "snapshotDomain": "LEARNING",
                            "snapshotData": [
                                {
                                    "Course Name": "Prompt Engineering for Developers",
                                    "Provider": "LinkedIn Learning",
                                    "Completed On": "2025-04-01"
                                }
                            ]
                        },
                        {
                            "snapshotDomain": "ARTICLES",
                            "snapshotData": [
                                {
                                    "Title": "Shipping Responsible AI Features",
                                    "Publisher": "Contoso Tech",
                                    "Published On": "2025-03-10"
                                }
                            ]
                        },
                        {
                            "snapshotDomain": "WHATSAPP_NUMBERS",
                            "snapshotData": [
                                {
                                    "Number": "+32 400 12 34 56",
                                    "Type": "mobile"
                                }
                            ]
                        },
                        {
                            "snapshotDomain": "VOLUNTEERING_EXPERIENCES",
                            "snapshotData": [
                                {
                                    "Role": "Mentor",
                                    "Organization": "Code Club",
                                    "Description": "Mentored youth coding workshops"
                                }
                            ]
                        }
                    ]
                }
                """)
        }));

        var snapshotImporter = new LinkedInMemberSnapshotImporter(httpClient, new LinkedInAuthOptions(), TimeProvider.System);
        var importer = new LinkedInExportImporter(new SimpleCsvParser(), new LinkedInPartialDateParser(), snapshotImporter);

        var result = await importer.ImportMemberSnapshotAsync("dma-token");
        var evidenceCatalog = new CandidateEvidenceService().BuildCatalog(result.Profile);

        Assert.Equal("Alex Taylor", result.Profile.Name.FullName);
        Assert.Empty(result.Profile.Experience);
        Assert.Contains("Languages", result.Profile.ManualSignals.Keys);
        Assert.Contains("English", result.Profile.ManualSignals["Languages"], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Learning", result.Profile.ManualSignals.Keys);
        Assert.DoesNotContain("Articles", result.Profile.ManualSignals.Keys);
        Assert.Contains("Volunteering experiences", result.Profile.ManualSignals.Keys);
        Assert.Contains("Code Club", result.Profile.ManualSignals["Volunteering experiences"], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.Inspection.DiscoveredFiles, file => string.Equals(file, "Learning.csv", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Inspection.DiscoveredFiles, file => string.Equals(file, "Articles.csv", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Inspection.DiscoveredFiles, file => file.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(evidenceCatalog, item => item.Title == "Languages");
        Assert.DoesNotContain(evidenceCatalog, item => item.Title == "Learning");
        Assert.DoesNotContain(evidenceCatalog, item => item.Title == "Articles");
        Assert.DoesNotContain(evidenceCatalog, item => item.Title.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(evidenceCatalog, item => item.Title == "Volunteering experiences");
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("LEARNING", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("ARTICLES", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("WHATSAPP_NUMBERS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ImportMemberSnapshotAsync_DoesNotMisrouteProfileSummaryIntoProfile()
    {
        using var httpClient = new HttpClient(new StubMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                    "paging": { "start": 0, "count": 10, "links": [], "total": 1 },
                    "elements": [
                        {
                            "snapshotDomain": "PROFILE",
                            "snapshotData": [
                                {
                                    "First Name": "Alex",
                                    "Last Name": "Taylor",
                                    "Headline": "Consultant",
                                    "Summary": "Grounded summary"
                                }
                            ]
                        },
                        {
                            "snapshotDomain": "PROFILE_SUMMARY",
                            "snapshotData": [
                                {
                                    "Headline": "AI generated profile summary",
                                    "Summary": "This should not overwrite the profile import."
                                }
                            ]
                        }
                    ]
                }
                """)
        }));

        var snapshotImporter = new LinkedInMemberSnapshotImporter(httpClient, new LinkedInAuthOptions(), TimeProvider.System);
        var importer = new LinkedInExportImporter(new SimpleCsvParser(), new LinkedInPartialDateParser(), snapshotImporter);

        var result = await importer.ImportMemberSnapshotAsync("dma-token");

        Assert.Equal("Alex Taylor", result.Profile.Name.FullName);
        Assert.Equal("Consultant", result.Profile.Headline);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("PROFILE_SUMMARY", StringComparison.OrdinalIgnoreCase));
    }

    private static LinkedInMemberSnapshotImporter CreateSnapshotImporter()
        => new(new HttpClient(new StubMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("No data found for this memberId")
        })), new LinkedInAuthOptions(), TimeProvider.System);

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    private sealed class StubMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responseFactory(request));
    }
}
