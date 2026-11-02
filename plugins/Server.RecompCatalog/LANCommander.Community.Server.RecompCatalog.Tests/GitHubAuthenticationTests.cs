using LANCommander.Community.Server.RecompCatalog.Infrastructure;
using LANCommander.Community.Server.RecompCatalog.Providers;

namespace LANCommander.Community.Server.RecompCatalog.Tests;

public class GitHubAuthenticationTests
{
    [Fact]
    public void PrefersGhTokenAndReportsItsSource()
    {
        var values = new Dictionary<string, string?>
        {
            ["GH_TOKEN"] = " gh-token ",
            ["GITHUB_TOKEN"] = "github-token",
        };

        var authentication = GitHubAuthentication.FromEnvironment(
            name => values.GetValueOrDefault(name));

        Assert.Equal("gh-token", authentication.Token);
        Assert.Equal("GH_TOKEN", authentication.Source);
        Assert.True(authentication.IsAuthenticated);
    }

    [Fact]
    public void SupportsCustomEnvironmentVariable()
    {
        var authentication = GitHubAuthentication.FromEnvironment(
            name => name == "LC_GITHUB_TOKEN" ? "custom-token" : null,
            "LC_GITHUB_TOKEN");

        Assert.Equal("custom-token", authentication.Token);
        Assert.Equal("LC_GITHUB_TOKEN", authentication.Source);
    }
}
