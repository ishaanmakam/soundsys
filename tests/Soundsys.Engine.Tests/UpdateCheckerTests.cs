using System.Net;
using System.Text;
using Soundsys.Engine.Updates;

namespace Soundsys.Engine.Tests;

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("0.1.0", "0.1.0", 0)]
    [InlineData("v0.1.0", "0.1.0", 0)]
    [InlineData("0.1.0-alpha", "0.1.0", -1)]
    [InlineData("0.1.0-alpha", "0.1.0-beta", -1)]
    [InlineData("0.1.0-alpha.2", "0.1.0-alpha.10", -1)]
    [InlineData("0.1.0-alpha", "0.1.0-alpha.1", -1)]
    [InlineData("0.2.0-alpha", "0.1.9", 1)]
    [InlineData("1.0.0", "0.99.99", 1)]
    [InlineData("0.1.0+abc", "0.1.0", 0)]
    public void Compares_semantic_versions(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(SemVer.Compare(a, b)));
        Assert.Equal(-expected, Math.Sign(SemVer.Compare(b, a)));
    }

    private sealed class FakeHandler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private const string Releases = """
        [
          { "tag_name": "v0.3.0-beta", "prerelease": true, "draft": false, "html_url": "https://example/beta" },
          { "tag_name": "v0.2.0", "prerelease": false, "draft": false, "html_url": "https://example/020" },
          { "tag_name": "v9.9.9", "prerelease": false, "draft": true, "html_url": "https://example/draft" },
          { "tag_name": "v0.1.0-alpha", "prerelease": true, "draft": false, "html_url": "https://example/alpha" }
        ]
        """;

    [Fact]
    public async Task Prerelease_build_is_offered_the_newest_prerelease()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, Releases);
        var result = await new UpdateChecker(new HttpClient(handler)).CheckAsync("0.1.0-alpha");
        Assert.True(result.UpdateAvailable);
        Assert.Equal("0.3.0-beta", result.LatestVersion);
        Assert.Equal("https://example/beta", result.Url);
        Assert.Equal("soundsys-update-check", handler.Last!.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Stable_build_ignores_prereleases_and_drafts()
    {
        var result = await new UpdateChecker(new HttpClient(new FakeHandler(HttpStatusCode.OK, Releases))).CheckAsync("0.2.0");
        Assert.False(result.UpdateAvailable);
        Assert.Equal("You're up to date (0.2.0).", result.Message);
    }

    [Fact]
    public async Task Network_failure_is_a_plain_message()
    {
        var result = await new UpdateChecker(new HttpClient(new FakeHandler(HttpStatusCode.ServiceUnavailable, ""))).CheckAsync("0.1.0");
        Assert.False(result.UpdateAvailable);
        Assert.Equal("Couldn't check for updates right now.", result.Message);

        var garbage = await new UpdateChecker(new HttpClient(new FakeHandler(HttpStatusCode.OK, "{not json"))).CheckAsync("0.1.0");
        Assert.Equal("Couldn't check for updates. Are you online?", garbage.Message);
    }
}
