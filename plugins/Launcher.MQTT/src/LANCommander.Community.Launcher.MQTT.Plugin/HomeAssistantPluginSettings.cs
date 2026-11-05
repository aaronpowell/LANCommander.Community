using System.Text.Json;
using System.Text.Json.Serialization;
using LANCommander.Community.Launcher.MQTT.Transport;
using LANCommander.SDK.Plugins;

namespace LANCommander.Community.Launcher.MQTT;

public sealed record HomeAssistantPluginSettings(
    string BrokerHost,
    int BrokerPort,
    string ClientId,
    string TopicPrefix,
    IReadOnlyList<FavoriteGame> Favorites,
    string BrokerUsername = "",
    string BrokerPassword = "",
    bool UseTls = false,
    string ExposureMode = "selected")
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public MqttConnectionSettings CreateMqttSettings()
    {
        if (ExposureMode is not ("selected" or "all"))
            throw new InvalidOperationException("ExposureMode must be selected or all.");
        if (Favorites is null)
            throw new InvalidOperationException("Favorites must be an array (it may be empty).");
        if (Favorites.Any(game => game.Id == Guid.Empty || string.IsNullOrWhiteSpace(game.Title)))
            throw new InvalidOperationException("Each favorite requires a game UUID and a display title.");
        if (Favorites.Select(game => game.Id).Distinct().Count() != Favorites.Count)
            throw new InvalidOperationException("Favorite game UUIDs must be unique.");
        var settings = new MqttConnectionSettings(
            BrokerHost,
            BrokerPort,
            ClientId,
            TopicPrefix,
            UseTls);
        settings.Validate();
        return settings;
    }

    public MqttCredentials CreateMqttCredentials()
    {
        if (string.IsNullOrWhiteSpace(BrokerUsername) || string.IsNullOrEmpty(BrokerPassword))
            throw new InvalidOperationException("BrokerUsername and BrokerPassword are required in homeassistant.json.");

        return new MqttCredentials(BrokerUsername, BrokerPassword);
    }

    public static string PrepareDataDirectory(string pluginDirectory)
    {
        var dataDirectory = Path.Combine(pluginDirectory, PluginManifest.DataFolderName);
        Directory.CreateDirectory(dataDirectory);
        foreach (var name in new[] { "homeassistant.json", "discovery-topics.json" })
        {
            var legacy = Path.Combine(pluginDirectory, name);
            var destination = Path.Combine(dataDirectory, name);
            if (!File.Exists(legacy))
                continue;
            if (File.Exists(destination))
                throw new IOException($"Both legacy and Data copies of {name} exist. Back them up, reconcile them into Data, and remove the legacy copy before starting.");
            File.Move(legacy, destination);
        }
        return dataDirectory;
    }

    public static async Task<HomeAssistantPluginSettings> LoadAsync(
        string pluginDirectory,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(pluginDirectory, "homeassistant.json");
        await using var stream = File.OpenRead(path);
        var settings = await JsonSerializer.DeserializeAsync<HomeAssistantPluginSettings>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);

        return settings ?? throw new InvalidOperationException($"Could not load plugin settings from '{path}'.");
    }

    public async Task SaveAsync(string pluginDirectory, CancellationToken cancellationToken)
    {
        CreateMqttSettings();
        CreateMqttCredentials();
        var path = Path.Combine(pluginDirectory, "homeassistant.json");
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(this,
            new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    public static HomeAssistantPluginSettings Default { get; } = new(
        "", 1883, $"lc_{Guid.NewGuid():N}", "lancommander/launcher", []);
}
