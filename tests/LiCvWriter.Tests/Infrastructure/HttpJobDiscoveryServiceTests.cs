using System.Net;
using System.Net.Http;
using System.Text;
using LiCvWriter.Application.Models;
using LiCvWriter.Application.Options;
using LiCvWriter.Infrastructure.Research;

namespace LiCvWriter.Tests.Infrastructure;

public sealed class HttpJobDiscoveryServiceTests
{
    [Fact]
    public async Task DiscoverAsync_WithJobindexMarkup_ReturnsSuggestionsFromResultCards()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                <html>
                  <body>
                    <div class="jobsearch-result">
                      <div class="PaidJob">
                        <div class="PaidJob-inner">
                          <a href="https://www.esoft.com/"><img alt="Esoft A/S" /></a>
                          <h4><a href="https://esoft.hr-on.com/show-job/327354?linkref=671319&amp;locale=en_US">Software Engineer</a></h4>
                          <div class="jobad-element-area"><span class="jix_robotjob--area">Odense C</span></div>
                          <p>We are looking for a software engineer to join our R&amp;D team in Odense.</p>
                          <p>Your focus is the engineering side of our ML systems.</p>
                        </div>
                        <div class="jix-toolbar__pubdate">Indrykket: <time datetime="2026-05-01">01-05-2026</time></div>
                      </div>
                    </div>
                    <div class="jobsearch-result">
                      <div class="PaidJob">
                        <div class="PaidJob-inner">
                          <a href="https://karnovgroup.dk/"><img alt="Karnov Group Denmark A/S" /></a>
                          <h4><a href="/jobannonce/h1661330/software-engineer">Software Engineer</a></h4>
                          <div class="jobad-element-area"><span class="jix_robotjob--area">Copenhagen</span></div>
                          <p>Se video om Karnov Group Denmark A/S som arbejdsplads</p>
                          <p>You will help embed LLM capabilities into mature production workflows.</p>
                        </div>
                        <div class="jix-toolbar__pubdate">Indrykket: <time datetime="2026-05-02">02-05-2026</time></div>
                      </div>
                    </div>
                  </body>
                </html>
                """,
                System.Text.Encoding.UTF8,
                "text/html")
        });
        var client = new HttpClient(handler);
        var service = new HttpJobDiscoveryService(client, new JobDiscoveryOptions { ShortlistLimit = 10 });

        var result = await service.DiscoverAsync(new JobDiscoverySearchPlan(
            "jobindex",
            "Jobindex",
            "software architect",
            "Copenhagen",
            new Uri("https://www.jobindex.dk/jobsoegning?q=software")));

        Assert.Equal(2, result.Count);
        Assert.Equal("Software Engineer", result[0].Title);
        Assert.Equal("Esoft A/S", result[0].CompanyName);
        Assert.Equal("Odense C", result[0].Location);
        Assert.Contains("ML systems", result[0].Summary);
        Assert.Equal("01-05-2026", result[0].PostedLabel);
        Assert.Equal("https://www.jobindex.dk/jobannonce/h1661330/software-engineer", result[1].DetailUrl.AbsoluteUri);
        Assert.DoesNotContain("Se video", result[1].Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiscoverAsync_WithPublicHttpsOnly_SkipsNonHttpsJobLinks()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                <html>
                  <body>
                    <div class="jobsearch-result">
                      <div class="PaidJob">
                        <div class="PaidJob-inner">
                          <a href="https://contoso.example"><img alt="Contoso" /></a>
                          <h4><a href="http://jobs.example.test/software-architect">Software Architect</a></h4>
                          <div class="jobad-element-area"><span class="jix_robotjob--area">Aarhus</span></div>
                          <p>Architect role.</p>
                        </div>
                      </div>
                    </div>
                  </body>
                </html>
                """,
                System.Text.Encoding.UTF8,
                "text/html")
        });
        var client = new HttpClient(handler);
        var service = new HttpJobDiscoveryService(client, new JobDiscoveryOptions { PublicHttpsOnly = true });

        var result = await service.DiscoverAsync(new JobDiscoverySearchPlan(
            "jobindex",
            "Jobindex",
            "architect",
            string.Empty,
            new Uri("https://www.jobindex.dk/jobsoegning?q=architect")));

        Assert.Empty(result);
    }

    [Fact]
    public async Task DiscoverAsync_WithEmbeddedHtmlPayload_ReturnsSuggestionsFromEscapedResultMarkup()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "results": [
                    {
                      "html": "<div id=\"jobad-wrapper-h1661380\"><div class=\"jobsearch-result\"><div class=\"PaidJob\"><div class=\"PaidJob-inner\"><a href=\"https://www.esoft.com/\"><img alt=\"Esoft A/S\" /></a><h4><a href=\"https://esoft.hr-on.com/show-job/327354?linkref=671319&amp;locale=en_US\">Software Engineer</a></h4><div class=\"jobad-element-area\"><span class=\"jix_robotjob--area\">Odense C</span></div><p>We are looking for a software engineer to join our R&amp;D team in Odense.</p><p>Your focus is the engineering side of our ML systems.</p></div><div class=\"jix-toolbar__pubdate\"><time datetime=\"2026-05-01\">01-05-2026</time></div></div></div>"
                    }
                  ]
                }
                """,
                System.Text.Encoding.UTF8,
                "text/html")
        });
        var client = new HttpClient(handler);
        var service = new HttpJobDiscoveryService(client, new JobDiscoveryOptions { ShortlistLimit = 10 });

        var result = await service.DiscoverAsync(new JobDiscoverySearchPlan(
            "jobindex",
            "Jobindex",
            "software architect",
            "Copenhagen",
            new Uri("https://www.jobindex.dk/jobsoegning?q=software")));

        var suggestion = Assert.Single(result);
        Assert.Equal("Software Engineer", suggestion.Title);
        Assert.Equal("Esoft A/S", suggestion.CompanyName);
        Assert.Equal("Odense C", suggestion.Location);
        Assert.Contains("ML systems", suggestion.Summary);
        Assert.Equal("01-05-2026", suggestion.PostedLabel);
    }

      [Fact]
      public async Task DiscoverAsync_WhenSearchPageRedirectsToPublicPage_FollowsRedirectAndParsesResults()
      {
        var requests = new List<string>();
        var handler = new StubHttpMessageHandler(request =>
        {
          requests.Add(request.RequestUri!.AbsoluteUri);

          return request.RequestUri.AbsoluteUri switch
          {
            "https://www.jobindex.dk/jobsoegning?q=software" => CreateRedirectResponse(HttpStatusCode.Redirect, "https://www.jobindex.dk/jobsoegning/redirected?q=software"),
            "https://www.jobindex.dk/jobsoegning/redirected?q=software" => new HttpResponseMessage(HttpStatusCode.OK)
            {
              Content = new StringContent(
                """
                <html>
                  <body>
                  <div class="jobsearch-result">
                    <div class="PaidJob">
                    <div class="PaidJob-inner">
                      <a href="https://contoso.example/"><img alt="Contoso" /></a>
                      <h4><a href="/jobannonce/h123/software-engineer">Software Engineer</a></h4>
                      <div class="jobad-element-area"><span class="jix_robotjob--area">Copenhagen</span></div>
                      <p>Build practical software systems.</p>
                    </div>
                    </div>
                  </div>
                  </body>
                </html>
                """,
                Encoding.UTF8,
                "text/html")
            },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
          };
        });

        var service = new HttpJobDiscoveryService(new HttpClient(handler), new JobDiscoveryOptions { ShortlistLimit = 10 });

        var result = await service.DiscoverAsync(new JobDiscoverySearchPlan(
          "jobindex",
          "Jobindex",
          "software",
          string.Empty,
          new Uri("https://www.jobindex.dk/jobsoegning?q=software")));

        var suggestion = Assert.Single(result);
        Assert.Equal("https://www.jobindex.dk/jobannonce/h123/software-engineer", suggestion.DetailUrl.AbsoluteUri);
        Assert.Equal(
          [
            "https://www.jobindex.dk/jobsoegning?q=software",
            "https://www.jobindex.dk/jobsoegning/redirected?q=software"
          ],
          requests);
      }

      [Fact]
      public async Task DiscoverAsync_WhenSearchPageRedirectsToPrivateHost_Throws()
      {
        var requests = new List<string>();
        var handler = new StubHttpMessageHandler(request =>
        {
          requests.Add(request.RequestUri!.AbsoluteUri);
          return CreateRedirectResponse(HttpStatusCode.Redirect, "https://localhost/results");
        });

        var service = new HttpJobDiscoveryService(new HttpClient(handler), new JobDiscoveryOptions { ShortlistLimit = 10 });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DiscoverAsync(new JobDiscoverySearchPlan(
          "jobindex",
          "Jobindex",
          "software",
          string.Empty,
          new Uri("https://www.jobindex.dk/jobsoegning?q=software"))));

        Assert.Contains("private", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["https://www.jobindex.dk/jobsoegning?q=software"], requests);
      }

      private static HttpResponseMessage CreateRedirectResponse(HttpStatusCode statusCode, string location)
      {
        var response = new HttpResponseMessage(statusCode);
        response.Headers.Location = Uri.TryCreate(location, UriKind.Absolute, out var absoluteUri)
          ? absoluteUri
          : new Uri(location, UriKind.Relative);
        return response;
      }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}