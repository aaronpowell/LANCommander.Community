using System.Text.Json;
using System.Threading.Channels;
using LANCommander.Community.Launcher.MQTT.Core;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace LANCommander.Community.Launcher.MQTT.Transport;

public sealed class MqttLaunchTransport : IAsyncDisposable
{
    private const string HomeAssistantStatusTopic = "homeassistant/status";
    private readonly MqttConnectionSettings _settings;
    private readonly MqttCredentials _credentials;
    private readonly LaunchCoordinator _coordinator;
    private readonly IReadOnlyCollection<FavoriteGame> _favorites;
    private readonly ILogger _logger;
    private readonly IMqttClient _client;
    private readonly Channel<LaunchCommand> _commands = Channel.CreateBounded<LaunchCommand>(
        new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly CancellationTokenSource _stop = new();
    private Task? _connectionTask;
    private Task? _commandTask;
    private Task? _stateTask;
    private int _replayRequested;
    private readonly string? _discoveryJournalPath;
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private HashSet<string> _publishedTopics = [];
    private string? _discoveryFingerprint;
    private volatile bool _discoveryDisabled;
    private bool _journalLoaded;
    private int _disposed;

    public MqttLaunchTransport(
        MqttConnectionSettings settings,
        MqttCredentials credentials,
        LaunchCoordinator coordinator,
        IReadOnlyCollection<FavoriteGame> favorites,
        ILogger logger,
        string? discoveryJournalPath = null)
    {
        settings.Validate();
        _settings = settings;
        _credentials = credentials;
        _coordinator = coordinator;
        _favorites = favorites;
        _publishedTopics = favorites.Select(game =>
            $"homeassistant/button/{settings.ClientId}/launch_{game.Id:N}/config").ToHashSet(StringComparer.Ordinal);
        _logger = logger;
        _discoveryJournalPath = discoveryJournalPath;
        _client = new MqttFactory().CreateMqttClient();
    }

    public Task StartAsync()
    {
        if (_connectionTask is not null)
            throw new InvalidOperationException("The MQTT transport has already been started.");

        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        _commandTask = ProcessCommandsAsync(_stop.Token);
        _stateTask = PublishStateChangesAsync(_stop.Token);
        _connectionTask = RunConnectionLoopAsync(_stop.Token);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        if (_connectionTask is null)
            return;

        try
        {
            await Task.WhenAll(_connectionTask, _commandTask!, _stateTask!).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        finally
        {
            if (_client.IsConnected)
            {
                try
                {
                    await PublishAvailabilityAsync("offline", CancellationToken.None).ConfigureAwait(false);
                    await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build())
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Could not publish MQTT shutdown availability.");
                }
            }

            _commands.Writer.TryComplete();
            _client.Dispose();
            _stop.Dispose();
        }
    }

    private async Task RunConnectionLoopAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        var options = BuildClientOptions();
        var configured = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!_client.IsConnected)
                {
                    await _client.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
                    configured = false;
                    _discoveryFingerprint = null;
                }

                if (!configured)
                {
                    delay = TimeSpan.FromSeconds(1);
                    var subscription = await _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                        .WithTopicFilter(filter => filter
                            .WithTopic($"{_settings.TopicPrefix}/command/+")
                            .WithRetainAsPublished(true)
                            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                        .WithTopicFilter(filter => filter
                            .WithTopic(HomeAssistantStatusTopic)
                            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                        .Build(), cancellationToken).ConfigureAwait(false);
                    if (subscription.Items.Any(item => (int)item.ResultCode >= 128))
                        throw new InvalidOperationException("The broker denied an MQTT subscription; check topic ACLs.");

                    await PublishDiscoveryAsync(cancellationToken).ConfigureAwait(false);
                    await PublishStateAsync(cancellationToken).ConfigureAwait(false);
                    await PublishAvailabilityAsync("online", cancellationToken).ConfigureAwait(false);
                    configured = true;
                    _logger.LogInformation("Connected to the Home Assistant MQTT broker.");
                }

                if (Interlocked.Exchange(ref _replayRequested, 0) != 0)
                {
                    _discoveryFingerprint = null;
                    await PublishDiscoveryAsync(cancellationToken).ConfigureAwait(false);
                }

                await PublishDiscoveryAsync(cancellationToken).ConfigureAwait(false);
                await PublishStateAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Home Assistant MQTT connection failed; retrying in {Delay}.", delay);
                if (_client.IsConnected)
                {
                    try
                    {
                        await PublishAvailabilityAsync("offline", cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception availabilityException) when (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning(availabilityException, "Could not report unavailable MQTT state.");
                    }
                }
                configured = false;
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
    }

    private MqttClientOptions BuildClientOptions()
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_settings.Host, _settings.Port)
            .WithClientId(_settings.ClientId)
            .WithCredentials(_credentials.Username, _credentials.Password)
            .WithTimeout(TimeSpan.FromSeconds(10))
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithCleanStart(true)
            .WithSessionExpiryInterval(0)
            .WithWillTopic($"{_settings.TopicPrefix}/availability")
            .WithWillPayload("offline")
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithWillRetain(true);

        if (_settings.UseTls)
            builder.WithTlsOptions(tls => tls
                .UseTls()
                .WithCertificateValidationHandler(_ => true));

        return builder.Build();
    }

    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs eventArgs)
    {
        var message = eventArgs.ApplicationMessage;
        if (message.Topic == HomeAssistantStatusTopic)
        {
            if (string.Equals(message.ConvertPayloadToString(), "online", StringComparison.OrdinalIgnoreCase))
                Interlocked.Exchange(ref _replayRequested, 1);
            return Task.CompletedTask;
        }

        if (message.Retain)
        {
            _logger.LogWarning("Ignored retained MQTT message on {Topic}.", message.Topic);
            return Task.CompletedTask;
        }

        var target = message.Topic[(message.Topic.LastIndexOf('/') + 1)..];
        if (!LaunchCommandParser.TryParse(message.PayloadSegment, out var command)
            || command is null
            || !(target == command.GameId.ToString("D") && command.Action is "launch" or "install"
                || target == command.Action && command.Action is "select" or "launch_selected" or "install_selected"))
        {
            _logger.LogWarning("Ignored invalid launch request on {Topic}.", message.Topic);
            return Task.CompletedTask;
        }

        if (!_commands.Writer.TryWrite(command))
        {
            _logger.LogWarning("Ignored launch request because the bounded request queue is full.");
        }

        return Task.CompletedTask;
    }

    private async Task ProcessCommandsAsync(CancellationToken cancellationToken)
    {
        await foreach (var command in _commands.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var result = await _coordinator.ProcessAsync(command, cancellationToken).ConfigureAwait(false);
                await TryPublishResultAsync(command, result, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Launch request {RequestId} failed.", command.RequestId);
                var result = new LaunchResult(
                    command.RequestId,
                    command.GameId,
                    LaunchDisposition.Rejected,
                    $"launch failed: {exception.GetType().Name}");
                await TryPublishResultAsync(command, result, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task PublishStateChangesAsync(CancellationToken cancellationToken)
    {
        await foreach (var state in _coordinator.ReadStateChangesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!_client.IsConnected)
                continue;

            try
            {
                await PublishStateAsync(cancellationToken).ConfigureAwait(false);
                if (state.RequestId.HasValue)
                    await PublishResultAsync(state, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Could not publish Home Assistant launcher state.");
            }
        }
    }

    private Task PublishAvailabilityAsync(string state, CancellationToken cancellationToken) =>
        PublishAsync($"{_settings.TopicPrefix}/availability", state, true, cancellationToken);

    private async Task TryPublishResultAsync(
        LaunchCommand command, LaunchResult result, CancellationToken cancellationToken)
    {
        try
        {
            await PublishResultAsync(command, result, cancellationToken).ConfigureAwait(false);
            var latest = _coordinator.CurrentState;
            if (latest.RequestId == command.RequestId && latest.Status is "completed" or "installed" or "failed")
                await PublishResultAsync(latest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not publish result for request {RequestId}.", command.RequestId);
        }
    }

    private async Task PublishDiscoveryAsync(CancellationToken cancellationToken)
    {
        await _discoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_discoveryDisabled) return;
            if (!_journalLoaded && _discoveryJournalPath is not null && File.Exists(_discoveryJournalPath))
            {
                var previous = JsonSerializer.Deserialize<string[]>(
                    await File.ReadAllTextAsync(_discoveryJournalPath, cancellationToken).ConfigureAwait(false))
                    ?? throw new InvalidOperationException("The discovery topic journal is invalid.");
                // Only this plugin's own HA discovery topics may be tombstoned.
                _publishedTopics.UnionWith(previous.Where(topic => topic.StartsWith("homeassistant/", StringComparison.Ordinal)
                    && topic.Split('/').Length == 5 && topic.Split('/')[2] == _settings.ClientId
                    && topic.EndsWith("/config", StringComparison.Ordinal)));
            }
            _journalLoaded = true;
            var catalog = await _coordinator.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
            var available = catalog.Select(game => game.Id).ToHashSet();
            var publications = HomeAssistantDiscovery.Create(_settings.ClientId, _settings.TopicPrefix,
                _favorites.Where(game => available.Contains(game.Id)).ToArray(), catalog);
            var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                publications.SelectMany(item => item.Payload).ToArray()));
            if (fingerprint == _discoveryFingerprint)
                return;
            var topics = publications.Select(item => item.Topic).ToHashSet(StringComparer.Ordinal);
            // Write-ahead union preserves cleanup after a crash midway through publishing.
            var stale = _publishedTopics.Except(topics).ToArray();
            _publishedTopics.UnionWith(topics);
            await SaveDiscoveryJournalAsync(_publishedTopics, cancellationToken).ConfigureAwait(false);
            foreach (var topic in stale)
                await PublishAsync(topic, [], true, cancellationToken).ConfigureAwait(false);
            foreach (var publication in publications)
                await PublishAsync(publication.Topic, publication.Payload, true, cancellationToken).ConfigureAwait(false);
            _publishedTopics = topics;
            await SaveDiscoveryJournalAsync(topics, cancellationToken).ConfigureAwait(false);
            _discoveryFingerprint = fingerprint;
        }
        finally { _discoveryGate.Release(); }
    }

    public void ResumeDiscovery()
    {
        _discoveryDisabled = false;
        Interlocked.Exchange(ref _replayRequested, 1);
    }

    public async Task ClearDiscoveryAsync(CancellationToken token)
    {
        if (!_client.IsConnected)
            throw new InvalidOperationException("Reconnect the old broker before changing broker/client identity so old discovery can be removed.");
        await _discoveryGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _discoveryDisabled = true;
            foreach (var topic in _publishedTopics)
                await PublishAsync(topic, [], true, token).ConfigureAwait(false);
            _publishedTopics.Clear();
            await SaveDiscoveryJournalAsync([], token).ConfigureAwait(false);
        }
        finally { _discoveryGate.Release(); }
    }

    private async Task SaveDiscoveryJournalAsync(IEnumerable<string> topics, CancellationToken token)
    {
        if (_discoveryJournalPath is null) return;
        await File.WriteAllTextAsync(_discoveryJournalPath + ".tmp", JsonSerializer.Serialize(topics), token).ConfigureAwait(false);
        File.Move(_discoveryJournalPath + ".tmp", _discoveryJournalPath, overwrite: true);
    }

    private async Task PublishStateAsync(CancellationToken cancellationToken)
    {
        var catalog = await _coordinator.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        var state = _coordinator.CurrentState;
        var selected = catalog.FirstOrDefault(game => game.Id == _coordinator.SelectedGameId);
        var gameTitle = state.Status == "running" && state.GameId is Guid gameId
            ? catalog.FirstOrDefault(game => game.Id == gameId)?.Title
            : null;
        var ready = await _coordinator.IsReadyAsync(cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            ready,
            status = state.Status,
            requestId = state.RequestId,
            gameId = state.GameId,
            gameTitle,
            selectedGameId = selected?.Id,
            installStatus = selected?.InstallStatus ?? "no_selection",
            installProgress = selected?.InstallProgress,
            updatedAt = state.UpdatedAt,
            errorType = state.ErrorType
        });

        await PublishAsync($"{_settings.TopicPrefix}/state", payload, true, cancellationToken).ConfigureAwait(false);
        await PublishAsync($"{_settings.TopicPrefix}/selection", selected?.Option ?? "None", true, cancellationToken).ConfigureAwait(false);
    }

    private Task PublishResultAsync(
        LaunchCommand command,
        LaunchResult result,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            requestId = command.RequestId,
            gameId = result.GameId,
            disposition = result.Disposition.ToString().ToLowerInvariant(),
            message = result.Message,
            reportedAt = DateTimeOffset.UtcNow
        });

        return PublishAsync($"{_settings.TopicPrefix}/result", payload, false, cancellationToken);
    }

    private Task PublishResultAsync(LaunchState state, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            requestId = state.RequestId,
            gameId = state.GameId,
            disposition = state.Status,
            message = state.Status,
            errorType = state.ErrorType,
            reportedAt = state.UpdatedAt
        });

        return PublishAsync($"{_settings.TopicPrefix}/result", payload, false, cancellationToken);
    }

    private Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken) =>
        PublishAsync(topic, System.Text.Encoding.UTF8.GetBytes(payload), retain, cancellationToken);

    private Task PublishAsync(string topic, byte[] payload, bool retain, CancellationToken cancellationToken)
    {
        if (!_client.IsConnected)
            throw new InvalidOperationException("The MQTT client is disconnected; publication was not confirmed.");

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();

        return PublishCheckedAsync(message, cancellationToken);
    }

    private async Task PublishCheckedAsync(MqttApplicationMessage message, CancellationToken cancellationToken)
    {
        var result = await _client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
        if ((int)result.ReasonCode >= 128)
            throw new InvalidOperationException($"The broker rejected publication to '{message.Topic}': {result.ReasonCode}.");
    }
}
