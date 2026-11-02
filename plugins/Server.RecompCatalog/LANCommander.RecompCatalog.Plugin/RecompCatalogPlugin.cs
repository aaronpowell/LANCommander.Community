using System.Reflection;
using LANCommander.RecompCatalog.Core;
using LANCommander.RecompCatalog.Infrastructure;
using LANCommander.RecompCatalog.Plugin;
using LANCommander.SDK.Plugins;
using LANCommander.Server.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

[assembly: LANCommanderPlugin(
    typeof(RecompCatalogPlugin),
    Id = RecompCatalogPlugin.PluginId,
    MinHostVersion = "2.2.0-alpha.3",
    Hosts = PluginHost.Server)]

namespace LANCommander.RecompCatalog.Plugin;

/// <summary>
/// Server plugin entry point for browsing compatible recompilation catalogs and importing
/// GitHub release assets into LANCommander.
/// </summary>
public sealed class RecompCatalogPlugin : IPlugin
{
    internal const string PluginId = "dev.aaronpowell.lancommander.recompcatalog";

    private static readonly string AssemblyVersion =
        typeof(RecompCatalogPlugin).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(RecompCatalogPlugin).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    public string Id => PluginId;
    public string Name => "Recomp Catalog";
    public string Version => AssemblyVersion;
    public string Author => "Aaron Powell";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IServerRouteAssemblyExtension>(
            new RecompCatalogRouteAssemblyExtension(typeof(RecompCatalogPlugin).Assembly));

        services.AddSingleton<IServerNavigationExtension>(
            new RecompCatalogNavigationExtension());

        services.AddSingleton(serviceProvider =>
            RecompCatalogSettings.FromConfiguration(
                serviceProvider.GetRequiredService<IConfiguration>()));
        services.AddSingleton<RecompCatalogHttpClient>();
        services.AddSingleton<ICatalogClient>(serviceProvider =>
            new CatalogClient(
                serviceProvider.GetRequiredService<RecompCatalogHttpClient>().Client));
        services.AddSingleton<IReleaseClient>(serviceProvider =>
            new GitHubReleaseClient(
                serviceProvider.GetRequiredService<RecompCatalogHttpClient>().Client,
                GitHubAuthentication.FromEnvironment().Token));
        services.AddSingleton<CatalogBrowserService>();
        services.AddSingleton<CatalogImportGate>();
        services.AddScoped<CatalogImportWorkflow>();
        services.AddScoped<ICatalogRomHost, CatalogRomHost>();
        services.AddScoped<ICatalogHostIntegration, CatalogHostIntegration>();
    }

    public Task InitializeAsync(PluginContext context, CancellationToken cancellationToken)
    {
        context.Logger.LogInformation(
            "{Name} v{Version} initialized on host {Host}; page available at {Route}",
            Name, Version, context.Host, RecompCatalogNavigationExtension.Route);

        return Task.CompletedTask;
    }
}
