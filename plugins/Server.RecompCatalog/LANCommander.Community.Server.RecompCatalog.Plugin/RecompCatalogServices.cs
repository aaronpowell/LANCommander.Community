using LANCommander.Community.Server.RecompCatalog.Core;
using LANCommander.Community.Server.RecompCatalog.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace LANCommander.Community.Server.RecompCatalog.Plugin;

internal sealed record RecompCatalogSettings(string? FeedUrl)
{
    internal const string FeedConfigurationKey = "Plugins:RecompCatalog:FeedUrl";
    internal const string DefaultFeedUrl =
        "https://raw.githubusercontent.com/tgeorgiadis/quiver-community-app-catalog/main/index.json";

    internal static RecompCatalogSettings FromConfiguration(IConfiguration configuration)
    {
        var configured = configuration[FeedConfigurationKey];
        return configured is null
            ? new RecompCatalogSettings(DefaultFeedUrl)
            : new RecompCatalogSettings(configured.Trim());
    }
}

internal sealed class RecompCatalogHttpClient : IDisposable
{
    public RecompCatalogHttpClient()
    {
        Client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    internal HttpClient Client { get; }

    public void Dispose() => Client.Dispose();
}
