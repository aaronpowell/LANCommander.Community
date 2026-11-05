using LANCommander.Community.Launcher.MQTT.Core;
using LANCommander.Community.Launcher.MQTT.Transport;
using Microsoft.Extensions.Logging;

namespace LANCommander.Community.Launcher.MQTT;

// Resolved as a singleton so its provider is the GUI root, not InitializeAsync's temporary scope.
public sealed class HomeAssistantRuntime(IServiceProvider root, ILogger<HomeAssistantRuntime> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _apply = new(1, 1);
    private HomeAssistantPluginSettings _settings = HomeAssistantPluginSettings.Default;
    private string? _directory;
    private MqttLaunchTransport? _transport;
    private LaunchCoordinator? _coordinator;
    private LANCommanderGameLaunchHost? _host;
    public HomeAssistantPluginSettings Settings => Volatile.Read(ref _settings);
    public string Status { get; private set; } = "Open after the GUI Launcher has finished starting.";
    public LANCommanderGameLaunchHost Host => _host ?? throw new InvalidOperationException(Status);

    public async Task InitializeAsync(string directory, CancellationToken token)
    {
        _host = new LANCommanderGameLaunchHost(root, () => Settings);
        _coordinator = new LaunchCoordinator(_host, []);
        try
        {
            _directory = HomeAssistantPluginSettings.PrepareDataDirectory(directory);
            if (!File.Exists(Path.Combine(_directory, "homeassistant.json")))
            {
                Status = "Configure the broker and exposed games here, then Save and apply.";
                logger.LogWarning("Home Assistant MQTT is not configured. Open Launcher settings.");
                return;
            }
            var loaded = await HomeAssistantPluginSettings.LoadAsync(_directory, token).ConfigureAwait(false);
            loaded.CreateMqttSettings();
            _settings = loaded;
            await StartTransportAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Status = $"Configuration could not start ({exception.GetType().Name}). Correct it in Launcher settings.";
            logger.LogError(exception, "Could not start Home Assistant MQTT. Open Launcher settings.");
        }
    }

    public async Task ApplyAsync(HomeAssistantPluginSettings settings, CancellationToken token)
    {
        settings.CreateMqttSettings();
        settings.CreateMqttCredentials();
        await _apply.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_directory is null || _coordinator is null)
                throw new InvalidOperationException("Wait for GUI Launcher initialization.");
            await _coordinator.PauseForConfigurationAsync(token).ConfigureAwait(false);
            try
            {
                if (_transport is not null)
                {
                    // Same broker identities reconcile retained configs on reconnect; changed
                    // identities try to remove old discovery while that session is still online.
                    if (Settings.BrokerHost != settings.BrokerHost || Settings.BrokerPort != settings.BrokerPort
                        || Settings.ClientId != settings.ClientId || Settings.TopicPrefix != settings.TopicPrefix)
                        await _transport.ClearDiscoveryAsync(token).ConfigureAwait(false);
                }
                await settings.SaveAsync(_directory, token).ConfigureAwait(false);
                if (_transport is not null)
                {
                    await _transport.DisposeAsync().ConfigureAwait(false);
                    _transport = null;
                }
                Volatile.Write(ref _settings, settings);
                await StartTransportAsync().ConfigureAwait(false);
            }
            finally { _coordinator.ResumeAfterConfiguration(); }
        }
        catch (Exception exception)
        {
            _transport?.ResumeDiscovery();
            Status = $"Apply failed ({exception.GetType().Name}); inspect Launcher logs and retry.";
            logger.LogError(exception, "Could not apply Home Assistant settings.");
            throw;
        }
        finally { _apply.Release(); }
    }

    private async Task StartTransportAsync()
    {
        var settings = Settings;
        var connection = settings.CreateMqttSettings();
        logger.LogWarning(settings.UseTls
            ? "Home Assistant MQTT TLS does not verify certificates; broker impersonation is possible."
            : "Home Assistant MQTT uses plaintext; credentials and commands are exposed on the network.");
        _transport = new MqttLaunchTransport(connection, settings.CreateMqttCredentials(), _coordinator!,
            settings.Favorites.Take(8).ToArray(), logger, Path.Combine(_directory!, "discovery-topics.json"));
        await _transport.StartAsync().ConfigureAwait(false);
        Status = "Saved and applied. MQTT connects/retries in background; check HA readiness and Launcher logs.";
    }

    public async ValueTask DisposeAsync()
    {
        if (_transport is not null)
            await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
