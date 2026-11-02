using System.Net;
using System.Text;
using LANCommander.Community.Server.RecompCatalog.Core;
using LANCommander.Community.Server.RecompCatalog.Infrastructure;
using LANCommander.Community.Server.RecompCatalog.Providers;

namespace LANCommander.Community.Server.RecompCatalog.Tests;

public class GitHubReleaseClientTests
{
    [Fact]
    public async Task UsesFixtureAndFiltersWindowsAssets()
    {
        var json = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "github-release.json"));
        var client = new GitHubReleaseClient(new HttpClient(
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            })));
        var entry = new CatalogEntry
        {
            Name = "Fixture Game",
            Repository = "fixture/game",
            FolderName = "FixtureGame",
            ReleaseAssetFilter = "portable",
        };

        var release = await client.GetLatestAsync(entry);

        Assert.Equal("v1.2.3", release.Tag);
        Assert.Equal("fixture-portable-win-x64.zip", Assert.Single(release.Assets).Name);
    }

    [Fact]
    public async Task SendsTokenOnlyOnGitHubApiRequest()
    {
        var json = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "github-release.json"));
        var authorization = new List<string?>();
        var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            authorization.Add(request.Headers.Authorization?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }));
        var client = new GitHubReleaseClient(httpClient, "secret-token");

        await client.GetLatestAsync(new CatalogEntry
        {
            Name = "Fixture Game",
            Repository = "fixture/game",
            FolderName = "FixtureGame",
        });
        using var unrelatedResponse = await httpClient.GetAsync("https://catalog.test/index.json");

        Assert.Equal("Bearer secret-token", authorization[0]);
        Assert.Null(authorization[1]);
    }

    [Fact]
    public async Task ReportsAnonymousRateLimitAndResetTime()
    {
        var reset = DateTimeOffset.Parse("2026-10-01T05:00:00Z");
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString());
        var client = new GitHubReleaseClient(new HttpClient(
            new StubHttpMessageHandler(_ => response)));

        var exception = await Assert.ThrowsAsync<GitHubRateLimitException>(() =>
            client.GetLatestAsync(new CatalogEntry
            {
                Name = "Fixture Game",
                Repository = "fixture/game",
                FolderName = "FixtureGame",
            }));

        Assert.False(exception.Authenticated);
        Assert.Equal(reset, exception.ResetsAt);
        Assert.Contains("GH_TOKEN or GITHUB_TOKEN", exception.Message);
        Assert.Contains("2026-10-01 05:00:00 UTC", exception.Message);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
