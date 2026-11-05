using System.Reflection;
using System.Runtime.Loader;
using LANCommander.Launcher.Models;
using LANCommander.Launcher.Services;
using LANCommander.SDK.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using LauncherGame = LANCommander.Launcher.Data.Models.Game;
using System.Text.Json;
using LANCommander.Launcher.Plugins.Extensions;
using LANCommander.Community.Launcher.MQTT;
using NuGet.Versioning;

namespace LANCommander.Community.Launcher.MQTT.HostLoader.Tests;

public sealed class PluginLoadContextTests
{
    [Fact]
    public async Task Host_loader_discovers_package_and_skips_transport_in_headless_mode()
    {
        var pluginPath = Environment.GetEnvironmentVariable("LANCOMMANDER_MQTT_PLUGIN_PATH");
        Assert.False(string.IsNullOrWhiteSpace(pluginPath));
        var directory = Path.GetDirectoryName(Path.GetFullPath(pluginPath!))!;
        var services = new ServiceCollection().AddLogging();
        var loader = new PluginLoaderService();
        loader.DiscoverAndConfigure(services, PluginHost.Launcher,
            Path.GetDirectoryName(directory)!, "2.2.0-beta.3", NullLogger.Instance);
        var loaded = Assert.Single(loader.Plugins);
        Assert.Equal("com.aaronpowell.lancommander.homeassistant", loaded.Plugin.Id);
        Assert.Contains(services, descriptor => descriptor.ServiceType.FullName == typeof(HomeAssistantRuntime).FullName
            && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ISettingsPageExtension)
            && descriptor.Lifetime == ServiceLifetime.Singleton);
        await using var provider = services.BuildServiceProvider();
        Assert.True(await loader.InitializeAsync(loaded, provider));
    }

    [Fact]
    public void Plugin_entrypoint_and_host_service_types_bind_to_host_assemblies()
    {
        var pluginPath = Environment.GetEnvironmentVariable("LANCOMMANDER_MQTT_PLUGIN_PATH");
        Assert.False(string.IsNullOrWhiteSpace(pluginPath), "Set LANCOMMANDER_MQTT_PLUGIN_PATH to the built plugin assembly.");

        var hostAssemblies = new[]
        {
            typeof(IPlugin).Assembly,
            typeof(GameService).Assembly,
            typeof(LauncherGame).Assembly,
            typeof(ListItem).Assembly
        };
        var context = new PluginLoadContext(pluginPath!);
        var pluginAssembly = context.LoadFromAssemblyPath(Path.GetFullPath(pluginPath!));
        var manifest = pluginAssembly.GetCustomAttribute<LANCommanderPluginAttribute>();
        var pluginDirectory = Path.GetDirectoryName(pluginPath!)!;

        Assert.NotNull(manifest);
        Assert.Equal("LANCommander.Community.Launcher.MQTT.Plugin", pluginAssembly.GetName().Name);
        Assert.Equal("2.2.0-beta.3", manifest.MinHostVersion);
        Assert.Equal("2.2.0-beta.3", manifest.MaxHostVersion);
        Assert.True(typeof(IPlugin).IsAssignableFrom(manifest!.EntryPoint));
        var plugin = Activator.CreateInstance(manifest.EntryPoint) as IPlugin;
        Assert.Equal("com.aaronpowell.lancommander.homeassistant", plugin?.Id);
        Assert.Equal(
            pluginAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            plugin?.Version);
        var expectedVersion = Environment.GetEnvironmentVariable("LANCOMMANDER_MQTT_PACKAGE_VERSION");
        Assert.False(string.IsNullOrEmpty(expectedVersion));
        Assert.Equal(expectedVersion, plugin?.Version);
        var packageVersion = NuGetVersion.Parse(expectedVersion!);
        Assert.Equal(new Version(packageVersion.Major, packageVersion.Minor, packageVersion.Patch, 0),
            pluginAssembly.GetName().Version);

        var mqttAssembly = context.LoadFromAssemblyName(new AssemblyName("MQTTnet"));
        Assert.Equal(Path.Combine(pluginDirectory, "MQTTnet.dll"), mqttAssembly.Location);
        var templateExport = Environment.GetEnvironmentVariable("LANCOMMANDER_HA_TEMPLATE_EXPORT");
        if (!string.IsNullOrEmpty(templateExport))
        {
            var transportAssembly = context.LoadFromAssemblyName(new AssemblyName("LANCommander.Community.Launcher.MQTT.Transport"));
            var gameType = transportAssembly.GetType("LANCommander.Community.Launcher.MQTT.Transport.FavoriteGame", throwOnError: true)!;
            var favorites = Array.CreateInstance(gameType, 1);
            favorites.SetValue(Activator.CreateInstance(gameType, Guid.NewGuid(), "Template test game"), 0);
            var discoveryType = transportAssembly.GetType("LANCommander.Community.Launcher.MQTT.Transport.HomeAssistantDiscovery", throwOnError: true)!;
            var coreAssembly = context.LoadFromAssemblyName(new AssemblyName("LANCommander.Community.Launcher.MQTT.Core"));
            var catalogType = coreAssembly.GetType("LANCommander.Community.Launcher.MQTT.Core.CatalogGame", throwOnError: true)!;
            var catalog = Array.CreateInstance(catalogType, 1);
            catalog.SetValue(Activator.CreateInstance(catalogType, Guid.NewGuid(), "Duplicate title [test]", false, false, "not_installed", null), 0);
            var publications = (System.Collections.IEnumerable)discoveryType.GetMethod("Create")!
                .Invoke(null, ["lc_template_test", "test/launcher", favorites, catalog])!;
            var documents = publications.Cast<object>().Select(publication =>
                JsonDocument.Parse((byte[])publication.GetType().GetProperty("Payload")!.GetValue(publication)!).RootElement.Clone());
            File.WriteAllText(templateExport, JsonSerializer.Serialize(documents));
        }

        Assert.Equal(
            new[]
            {
                "LANCommander.Community.Launcher.MQTT.Core.dll",
                "LANCommander.Community.Launcher.MQTT.Plugin.dll",
                "LANCommander.Community.Launcher.MQTT.Transport.dll",
                "MQTTnet.dll"
            },
            Directory.EnumerateFiles(pluginDirectory, "*.dll")
                .Select(Path.GetFileName)
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase));

        foreach (var hostAssembly in hostAssemblies)
        {
            var resolved = context.LoadFromAssemblyName(hostAssembly.GetName());
            Assert.Same(hostAssembly, resolved);
            Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(resolved));
        }

        var hostAssemblyNames = hostAssemblies
            .Select(assembly => assembly.GetName().Name + ".dll")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bundledHostAssemblies = Directory.EnumerateFiles(Path.GetDirectoryName(pluginPath!)!, "*.dll")
            .Select(Path.GetFileName)
            .Where(file => file is not null && hostAssemblyNames.Contains(file))
            .ToArray();

        Assert.Empty(bundledHostAssemblies);
        Assert.True(File.Exists(Path.Combine(pluginDirectory, "homeassistant.example.json")));
        Assert.False(File.Exists(Path.Combine(pluginDirectory, "homeassistant.json")));
    }
}
