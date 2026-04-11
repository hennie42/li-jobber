using System.Net;
using System.Net.Http;
using LiCvWriter.Application.Abstractions;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Infrastructure.Research;

namespace LiCvWriter.Tests.Infrastructure;

public sealed class HttpJobResearchServiceTests
{
    [Fact]
    public async Task AnalyzeAsync_UsesStructuredLlmOutputAndPreservesSourceBackedSignals()
    {
        var handler = new StubHttpMessageHandler(_ =>
            CreateHtmlResponse(
            """
            <html>
              <head><title>Lead AI Architect</title></head>
              <body>
                <h1>Lead AI Architect</h1>
                <section>
                  <p>Must have Azure and Kubernetes experience.</p>
                  <p>Must have architecture and stakeholder management experience.</p>
                  <p>Nice to have generative AI and RAG delivery experience.</p>
                  <p>We value trust, collaboration, and knowledge sharing.</p>
                </section>
              </body>
            </html>
            """));

        var client = new HttpClient(handler);
        var llmClient = new FakeLlmClient(
            """
            {
              "roleTitle": "Lead AI Architect",
              "companyName": "Contoso",
              "summary": "Lead the delivery of practical Azure and AI architecture for enterprise clients.",
              "requirements": [
                {
                  "category": "Must have",
                  "requirement": "Azure",
                  "aliases": ["azure platform", "cloud architecture"],
                  "sourceSnippet": "Must have Azure and Kubernetes experience.",
                  "confidence": 97,
                  "sourceUrl": "https://jobs.example.test/lead-ai-architect"
                },
                {
                  "category": "Must have",
                  "requirement": "Kubernetes",
                  "sourceSnippet": "Must have Azure and Kubernetes experience.",
                  "confidence": 95,
                  "sourceUrl": "https://jobs.example.test/lead-ai-architect"
                },
                {
                  "category": "Nice to have",
                  "requirement": "Generative AI",
                  "sourceSnippet": "Nice to have generative AI and RAG delivery experience.",
                  "confidence": 88,
                  "sourceUrl": "https://jobs.example.test/lead-ai-architect"
                },
                {
                  "category": "Culture",
                  "requirement": "Trust",
                  "sourceSnippet": "We value trust, collaboration, and knowledge sharing.",
                  "confidence": 92,
                  "sourceUrl": "https://jobs.example.test/lead-ai-architect"
                }
              ]
            }
            """);
        var service = new HttpJobResearchService(client, llmClient, new OllamaOptions());

        var result = await service.AnalyzeAsync(new Uri("https://jobs.example.test/lead-ai-architect"), "session-model", "high");

        Assert.Equal("Lead AI Architect", result.RoleTitle);
        Assert.Equal("Contoso", result.CompanyName);
        Assert.Contains("Azure", result.MustHaveThemes);
        Assert.Contains("Kubernetes", result.MustHaveThemes);
        Assert.Contains("Generative AI", result.NiceToHaveThemes);
        Assert.Contains("Trust", result.CulturalSignals);
        Assert.Contains(result.Signals, signal => signal.Requirement == "Azure" && signal.Confidence == 97 && signal.SourceLabel == "jobs.example.test");
        Assert.Contains(result.Signals, signal => signal.Requirement == "Azure" && signal.EffectiveAliases.Contains("azure platform"));
        Assert.Equal("session-model", llmClient.LastRequest!.Model);
        Assert.Equal("high", llmClient.LastRequest.Think);
    }

    [Fact]
    public async Task BuildCompanyProfileAsync_UsesStructuredLlmOutputForGuidingPrinciplesAndSignals()
    {
        var handler = new StubHttpMessageHandler(_ =>
            CreateHtmlResponse(
            """
            <html>
              <body>
                <h1>About Contoso</h1>
                <p>We share knowledge openly, invest in mentoring, and work closely with clients.</p>
                <p>Our teams care about trust, pragmatic delivery, and long-term partnerships.</p>
              </body>
            </html>
            """));

        var client = new HttpClient(handler);
        var llmClient = new FakeLlmClient(
            """
            {
              "name": "Contoso",
              "summary": "Contoso emphasizes trust, mentoring, and pragmatic delivery for client work.",
              "guidingPrinciples": ["Trust", "Knowledge sharing", "Pragmatism"],
              "differentiators": ["Mentoring culture", "Client-facing delivery"],
              "requirements": [
                {
                  "category": "Culture",
                  "requirement": "Knowledge sharing",
                  "sourceSnippet": "We share knowledge openly, invest in mentoring, and work closely with clients.",
                  "confidence": 93,
                  "sourceUrl": "https://company.example.test/about"
                },
                {
                  "category": "Nice to have",
                  "requirement": "Client leadership",
                  "aliases": ["stakeholder management", "executive communication"],
                  "sourceSnippet": "We share knowledge openly, invest in mentoring, and work closely with clients.",
                  "confidence": 84,
                  "sourceUrl": "https://company.example.test/about"
                }
              ]
            }
            """);
        var service = new HttpJobResearchService(client, llmClient, new OllamaOptions());

        var result = await service.BuildCompanyProfileAsync([new Uri("https://company.example.test/about")], "session-model", "low");

        Assert.Equal("Contoso", result.Name);
        Assert.Contains("Trust", result.GuidingPrinciples);
        Assert.Contains("Mentoring culture", result.Differentiators);
        Assert.Contains("Knowledge sharing", result.CulturalSignals);
        Assert.Contains(result.Signals, signal => signal.Requirement == "Client leadership" && signal.SourceLabel == "company.example.test");
        Assert.Contains(result.Signals, signal => signal.Requirement == "Client leadership" && signal.EffectiveAliases.Contains("stakeholder management"));
    }

      [Fact]
      public async Task AnalyzeAsync_SendsBrowserLikeHeaders()
      {
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler(request =>
        {
          capturedRequest = request;
          return CreateHtmlResponse(
            """
            <html>
              <head><title>Lead AI Architect</title></head>
              <body><h1>Lead AI Architect</h1><p>Must have Azure experience.</p></body>
            </html>
            """);
        });

        var service = new HttpJobResearchService(
          new HttpClient(handler),
          new FakeLlmClient(
            """
            {
              "roleTitle": "Lead AI Architect",
              "companyName": "Contoso",
              "summary": "Lead Azure delivery.",
              "requirements": [
              {
                "category": "Must have",
                "requirement": "Azure",
                "sourceSnippet": "Must have Azure experience.",
                "confidence": 95,
                "sourceUrl": "https://jobs.example.test/lead-ai-architect"
              }
              ]
            }
            """),
          new OllamaOptions());

        await service.AnalyzeAsync(new Uri("https://jobs.example.test/lead-ai-architect"));

        Assert.NotNull(capturedRequest);
        Assert.Contains(capturedRequest!.Headers.UserAgent, product => product.ToString().Contains("Mozilla/5.0", StringComparison.Ordinal));
        Assert.Contains(capturedRequest.Headers.Accept, value => string.Equals(value.MediaType, "text/html", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(capturedRequest.Headers.AcceptLanguage, value => string.Equals(value.Value, "en-US", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(capturedRequest.Headers.AcceptEncoding, value => string.Equals(value.Value, "gzip", StringComparison.OrdinalIgnoreCase));
        Assert.True(capturedRequest.Headers.TryGetValues("Upgrade-Insecure-Requests", out var upgradeValues));
        Assert.Contains("1", upgradeValues!);
      }

      [Fact]
      public async Task AnalyzeAsync_WhenSiteReturnsForbidden_ThrowsHelpfulError()
      {
        var service = new HttpJobResearchService(
          new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))),
          new FakeLlmClient("{}"),
          new OllamaOptions());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
          service.AnalyzeAsync(new Uri("https://jobs.example.test/lead-ai-architect")));

        Assert.Contains("blocked access", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("403", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("jobs.example.test/lead-ai-architect", exception.Message, StringComparison.OrdinalIgnoreCase);
      }

      [Fact]
      public async Task AnalyzeTextAsync_ParsesJobPostingFromPastedText()
      {
        var llmClient = new FakeLlmClient(
            """
            {
              "roleTitle": "Senior Backend Engineer",
              "companyName": "Acme Corp",
              "summary": "Build scalable backend services for the Acme platform.",
              "requirements": [
                {
                  "category": "Must have",
                  "requirement": ".NET",
                  "sourceSnippet": "Must have strong .NET experience.",
                  "confidence": 96
                },
                {
                  "category": "Nice to have",
                  "requirement": "Kafka",
                  "sourceSnippet": "Experience with Kafka or similar event streaming.",
                  "confidence": 80
                }
              ]
            }
            """);
        var service = new HttpJobResearchService(
            new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("HTTP should not be called in text mode"))),
            llmClient,
            new OllamaOptions());

        var result = await service.AnalyzeTextAsync(
            "Senior Backend Engineer at Acme Corp. Must have strong .NET experience. Experience with Kafka or similar event streaming.",
            "session-model",
            "medium");

        Assert.Equal("Senior Backend Engineer", result.RoleTitle);
        Assert.Equal("Acme Corp", result.CompanyName);
        Assert.Null(result.SourceUrl);
        Assert.Contains(".NET", result.MustHaveThemes);
        Assert.Contains("Kafka", result.NiceToHaveThemes);
        Assert.Contains(result.Signals, signal => signal.SourceLabel == "pasted text");
      }

      [Fact]
      public async Task AnalyzeTextAsync_WhenTextIsEmpty_Throws()
      {
        var service = new HttpJobResearchService(
            new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException())),
            new FakeLlmClient("{}"),
            new OllamaOptions());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AnalyzeTextAsync(string.Empty));

        Assert.Contains("empty", exception.Message, StringComparison.OrdinalIgnoreCase);
      }

      [Fact]
      public async Task BuildCompanyProfileFromTextAsync_ParsesCompanyContextFromPastedText()
      {
        var llmClient = new FakeLlmClient(
            """
            {
              "name": "Acme Corp",
              "summary": "Acme Corp builds next-generation platform services.",
              "guidingPrinciples": ["Innovation", "Customer focus"],
              "differentiators": ["Platform scale"],
              "requirements": [
                {
                  "category": "Culture",
                  "requirement": "Innovation",
                  "sourceSnippet": "We foster innovation and experimentation.",
                  "confidence": 90
                }
              ]
            }
            """);
        var service = new HttpJobResearchService(
            new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException("HTTP should not be called in text mode"))),
            llmClient,
            new OllamaOptions());

        var result = await service.BuildCompanyProfileFromTextAsync(
            "Acme Corp builds next-generation platform services. We foster innovation and experimentation.",
            "session-model");

        Assert.Equal("Acme Corp", result.Name);
        Assert.Empty(result.SourceUrls);
        Assert.Contains("Innovation", result.GuidingPrinciples);
        Assert.Contains("Platform scale", result.Differentiators);
      }

      [Fact]
      public async Task BuildCompanyProfileFromTextAsync_WhenTextIsEmpty_Throws()
      {
        var service = new HttpJobResearchService(
            new HttpClient(new StubHttpMessageHandler(_ => throw new InvalidOperationException())),
            new FakeLlmClient("{}"),
            new OllamaOptions());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.BuildCompanyProfileFromTextAsync("   "));

        Assert.Contains("empty", exception.Message, StringComparison.OrdinalIgnoreCase);
      }

      private static HttpResponseMessage CreateHtmlResponse(string html)
        => new(HttpStatusCode.OK)
        {
          Content = new StringContent(html)
        };

      private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
          => Task.FromResult(responseFactory(request));
    }

      private sealed class FakeLlmClient(string content) : ILlmClient
      {
        public LlmRequest? LastRequest { get; private set; }

        public Task<OllamaModelAvailability> VerifyModelAvailabilityAsync(CancellationToken cancellationToken = default)
          => throw new NotSupportedException();

        public Task<LlmResponse> GenerateAsync(
          LlmRequest request,
          Action<LlmProgressUpdate>? progress = null,
          CancellationToken cancellationToken = default)
        {
          LastRequest = request;
          return Task.FromResult(new LlmResponse(request.Model, content, null, true, 12, 34, TimeSpan.FromSeconds(2)));
        }
      }
}