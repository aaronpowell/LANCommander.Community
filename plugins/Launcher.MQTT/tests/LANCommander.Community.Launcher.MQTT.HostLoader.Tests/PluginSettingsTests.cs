using System.Text.Json;
using LANCommander.Community.Launcher.MQTT;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LANCommander.Community.Launcher.MQTT.HostLoader.Tests;

public sealed class PluginSettingsTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Config_loads_broker_credentials_and_optional_tls(bool? configuredTls, bool expectedTls)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"lc-ha-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var values = new Dictionary<string, object?>
            {
                ["BrokerHost"] = "broker",
                ["BrokerPort"] = expectedTls ? 8883 : 1883,
                ["BrokerUsername"] = "test-user",
                ["BrokerPassword"] = "test-password",
                ["ClientId"] = "lc_test_1234",
                ["TopicPrefix"] = "test/launcher",
                ["Favorites"] = new[] { new { Id = Guid.NewGuid(), Title = "Test game" } }
            };
            if (configuredTls.HasValue)
                values["UseTls"] = configuredTls.Value;
            await File.WriteAllTextAsync(Path.Combine(directory, "homeassistant.json"), JsonSerializer.Serialize(values));

            var settings = await HomeAssistantPluginSettings.LoadAsync(directory, CancellationToken.None);
            Assert.Equal("selected", settings.ExposureMode);
            Assert.Equal(expectedTls, settings.CreateMqttSettings().UseTls);
            var credentials = settings.CreateMqttCredentials();
            Assert.Equal("test-user", credentials.Username);
            Assert.Equal("test-password", credentials.Password);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("selected")]
    [InlineData("all")]
    public async Task Settings_roundtrip_preserves_credentials_and_supports_large_or_empty_selection(string mode)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"lc-ha-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new HomeAssistantPluginSettings("broker", 1883, "lc_settings_test", "test/settings",
                Enumerable.Range(0, 100).Select(index =>
                    new LANCommander.Community.Launcher.MQTT.Transport.FavoriteGame(Guid.NewGuid(), $"Game {index}")).ToArray(),
                "test-user", "test-password", false, mode);
            await settings.SaveAsync(directory, CancellationToken.None);
            var loaded = await HomeAssistantPluginSettings.LoadAsync(directory, CancellationToken.None);
            Assert.Equal(settings.BrokerPassword, loaded.BrokerPassword);
            Assert.Equal(mode, loaded.ExposureMode);
            Assert.Equal(100, loaded.Favorites.Count);
            await (settings with { Favorites = [] }).SaveAsync(directory, CancellationToken.None);
            Assert.Empty((await HomeAssistantPluginSettings.LoadAsync(directory, CancellationToken.None)).Favorites);
            Assert.False(File.Exists(Path.Combine(directory, "homeassistant.json.tmp")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Remote_install_rejects_missing_or_ambiguous_destinations(int count)
    {
        var directories = Enumerable.Range(0, count).Select(index => $@"C:\Games{index}");
        var exception = Assert.Throws<LANCommander.Community.Launcher.MQTT.Core.RemoteOperationException>(() =>
            LANCommanderGameLaunchHost.RequireInstallDestination(directories));
        Assert.Contains("install_destination_not_configured", exception.Message);
        Assert.Throws<LANCommander.Community.Launcher.MQTT.Core.RemoteOperationException>(() =>
            LANCommanderGameLaunchHost.RequireInstallDestination(["relative"]));
        Assert.Equal(@"C:\Games", LANCommanderGameLaunchHost.RequireInstallDestination([@"C:\Games"]));
    }

    [Fact]
    public void Remote_install_plan_rejects_installed_dependencies_and_missing_target()
    {
        var gameId = Guid.NewGuid();
        var baseGameId = Guid.NewGuid();
        var plan = new LANCommander.SDK.Models.InstallPlan();
        plan.Items.Add(new LANCommander.SDK.Models.InstallPlanItem
            { EntityId = baseGameId, Type = LANCommander.SDK.Enums.InstallPlanItemType.Game });
        Assert.Throws<LANCommander.Community.Launcher.MQTT.Core.RemoteOperationException>(() =>
            LANCommanderGameLaunchHost.RequireFreshInstallPlan(gameId, plan, new HashSet<Guid>()));
        plan.Items.Add(new LANCommander.SDK.Models.InstallPlanItem
            { EntityId = gameId, Type = LANCommander.SDK.Enums.InstallPlanItemType.Addon });
        var exception = Assert.Throws<LANCommander.Community.Launcher.MQTT.Core.RemoteOperationException>(() =>
            LANCommanderGameLaunchHost.RequireFreshInstallPlan(gameId, plan, new HashSet<Guid> { baseGameId }));
        Assert.Contains("install_plan_would_modify_installed_game", exception.Message);
        LANCommanderGameLaunchHost.RequireFreshInstallPlan(gameId, plan, new HashSet<Guid>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_invalid_configuration_keeps_a_safe_editable_runtime(bool invalid)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"lc-ha-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            if (invalid)
                await File.WriteAllTextAsync(Path.Combine(directory, "homeassistant.json"), """{"Favorites": null}""");
            await using var provider = new ServiceCollection().BuildServiceProvider();
            await using var runtime = new HomeAssistantRuntime(provider, NullLogger<HomeAssistantRuntime>.Instance);
            await runtime.InitializeAsync(directory, CancellationToken.None);
            Assert.Empty(runtime.Settings.Favorites);
            Assert.NotNull(runtime.Host);
            Assert.Contains(invalid ? "Configuration could not start" : "Configure the broker", runtime.Status);
            if (invalid)
                Assert.Equal("""{"Favorites": null}""", await File.ReadAllTextAsync(Path.Combine(directory, "homeassistant.json")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Existing_dependency_manifest_is_not_allowed_even_with_a_stale_uninstalled_record()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"lc-ha-manifest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var id = Guid.NewGuid();
            var manifest = LANCommander.SDK.Helpers.ManifestHelper.GetPath(directory, id);
            Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
            File.WriteAllText(manifest, "test");
            var plan = new LANCommander.SDK.Models.InstallPlan();
            plan.Items.Add(new LANCommander.SDK.Models.InstallPlanItem
            {
                EntityId = id, InstallDirectory = directory,
                Type = LANCommander.SDK.Enums.InstallPlanItemType.Game
            });
            Assert.Throws<LANCommander.Community.Launcher.MQTT.Core.RemoteOperationException>(() =>
                LANCommanderGameLaunchHost.RequireFreshInstallPlan(id, plan, new HashSet<Guid>()));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("test-user", "")]
    [InlineData("", "test-password")]
    public void Empty_broker_credentials_are_rejected_without_exposing_values(string username, string password)
    {
        var settings = new HomeAssistantPluginSettings("broker", 1883, "lc_test_1234", "test/launcher",
            [], username, password);
        var exception = Assert.Throws<InvalidOperationException>(() => settings.CreateMqttCredentials());
        Assert.Equal("BrokerUsername and BrokerPassword are required in homeassistant.json.", exception.Message);
    }
}
