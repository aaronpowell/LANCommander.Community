using LANCommander.Community.Server.RecompCatalog.Core;
using LANCommander.Community.Server.RecompCatalog.Plugin;
using LANCommander.Server.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LANCommander.Community.Server.RecompCatalog.Tests;

public class PluginServiceRegistrationTests
{
    [Fact]
    public void BuildsAndResolvesBrowserServiceGraphWithValidationEnabled()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection([])
                .Build());

        // The server registers these contracts; the plugin must resolve with nothing else from the host.
        services.AddScoped<IGameProvider>(_ => new FakeGameProvider([]));
        services.AddScoped<IGameVersionProvider, FakeGameVersionProvider>();
        services.AddScoped<IRedistributableProvider>(_ => new FakeRedistributableProvider([]));
        services.AddScoped<IMetadataLookup, FakeMetadataLookup>();
        services.AddScoped<IPackageImporter, ThrowingPackageImporter>();

        new RecompCatalogPlugin().ConfigureServices(services);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        Assert.NotNull(provider.GetRequiredService<CatalogBrowserService>());
        Assert.NotNull(provider.GetRequiredService<IServerNavigationExtension>());
        Assert.NotNull(provider.GetRequiredService<IServerRouteAssemblyExtension>());
        using var scope = provider.CreateScope();
        Assert.IsType<CatalogHostIntegration>(scope.ServiceProvider.GetRequiredService<ICatalogHostIntegration>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<CatalogImportWorkflow>());
        Assert.IsType<CatalogRomHost>(scope.ServiceProvider.GetRequiredService<ICatalogRomHost>());
        using var otherScope = provider.CreateScope();
        Assert.Same(
            scope.ServiceProvider.GetRequiredService<CatalogImportGate>(),
            otherScope.ServiceProvider.GetRequiredService<CatalogImportGate>());
    }
}
