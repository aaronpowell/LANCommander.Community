using LANCommander.PluginManagement;
using LANCommander.SDK.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Frameworks;
using Xunit;

namespace LANCommander.Community.Launcher.MQTT.HostLoader.Tests;

public sealed class PluginPackageTests
{
    [Fact]
    public async Task Actual_package_is_discoverable_installs_loads_and_preserves_data_on_upgrade()
    {
        var packagePath = Environment.GetEnvironmentVariable("LANCOMMANDER_MQTT_PACKAGE_PATH");
        Assert.False(string.IsNullOrEmpty(packagePath), "Run eng/Test-LauncherPackage.ps1 against the actual nupkg.");
        var expectedVersion = Environment.GetEnvironmentVariable("LANCOMMANDER_MQTT_PACKAGE_VERSION");
        Assert.False(string.IsNullOrEmpty(expectedVersion));
        var testRoot = Environment.GetEnvironmentVariable("LANCOMMANDER_MQTT_PACKAGE_TEST_ROOT");
        Assert.False(string.IsNullOrEmpty(testRoot));
        // The script removes loaded DLLs after the test process releases Windows file locks.
        var root = Path.Combine(testRoot!, $"install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var feed = new PluginFeed("Test artifact", Path.GetDirectoryName(packagePath!)!);
        var feeds = new PluginFeedClient(() => [feed], () => true, NullLogger.Instance);
        var search = await feeds.SearchAsync("", PluginPackageTypes.Launcher);
        Assert.Empty(search.Errors);
        var listed = Assert.Single(search.Packages);
        Assert.Equal("LANCommander.Community.Launcher.MQTT.Plugin", listed.Id);
        Assert.Equal(expectedVersion, listed.Version);
        Assert.Empty((await feeds.SearchAsync("", PluginPackageTypes.Server)).Packages);
        var staging = new PluginStaging(root, Path.Combine(root, ".staging"));
        var manager = new PluginPackageManager(PluginHost.Launcher, feeds, new PluginLoaderService(),
            staging, NullLogger.Instance, "2.2.0-beta.4", NuGetFramework.Parse("net10.0"));
        await using (var stream = File.OpenRead(packagePath!))
        {
            var result = await manager.InstallAsync(stream);
            Assert.Equal(listed.Id, result.Folder);
            Assert.Equal(listed.Version, result.Package.Version);
        }
        Assert.True(manager.RestartRequired);
        staging.ApplyPending(NullLogger.Instance);
        Assert.False(manager.RestartRequired);
        var installed = Path.Combine(root, listed.Id);
        var data = Path.Combine(installed, "Data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "homeassistant.json"), "user settings");
        File.WriteAllText(Path.Combine(data, "discovery-topics.json"), "owned topics");
        await using (var stream = File.OpenRead(packagePath!))
            await manager.InstallAsync(stream);
        staging.ApplyPending(NullLogger.Instance);
        Assert.Equal("user settings", File.ReadAllText(Path.Combine(data, "homeassistant.json")));
        Assert.Equal("owned topics", File.ReadAllText(Path.Combine(data, "discovery-topics.json")));
        Assert.False(File.Exists(Path.Combine(installed, "homeassistant.json")));
        var loader = new PluginLoaderService();
        var services = new ServiceCollection().AddLogging();
        loader.DiscoverAndConfigure(services, PluginHost.Launcher, root, "2.2.0-beta.4", NullLogger.Instance);
        var plugin = Assert.Single(loader.Plugins);
        Assert.Equal("com.aaronpowell.lancommander.homeassistant", plugin.Plugin.Id);
        Assert.Equal(expectedVersion, plugin.Plugin.Version);
    }

    [Theory]
    [InlineData(PluginHost.Server, "2.2.0-beta.4")]
    [InlineData(PluginHost.Launcher, "2.2.0-alpha.4")]
    [InlineData(PluginHost.Launcher, "2.2.0-beta.3")]
    [InlineData(PluginHost.Launcher, "2.2.0-beta.5")]
    public void Actual_package_rejects_wrong_host_or_unverified_version(PluginHost host, string version)
    {
        var packagePath = Environment.GetEnvironmentVariable("LANCOMMANDER_MQTT_PACKAGE_PATH");
        Assert.False(string.IsNullOrEmpty(packagePath));
        var root = Path.Combine(Path.GetTempPath(), $"lc-mqtt-reject-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var stream = File.OpenRead(packagePath!);
            Assert.Throws<PluginPackageException>(() =>
                PluginPackageReader.Unpack(stream, root, host, version, NuGetFramework.Parse("net10.0")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
