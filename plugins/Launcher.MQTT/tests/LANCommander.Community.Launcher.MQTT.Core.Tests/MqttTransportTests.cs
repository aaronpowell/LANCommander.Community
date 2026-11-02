using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LANCommander.Community.Launcher.MQTT.Core;
using LANCommander.Community.Launcher.MQTT.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using MQTTnet.Server;
using Xunit;

namespace LANCommander.Community.Launcher.MQTT.Core.Tests;

public sealed class MqttTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Broker_exercises_readiness_retained_rejection_launch_dedup_birth_and_reconnect(bool useTls)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var journal = Path.Combine(Path.GetTempPath(), $"lc-ha-discovery-{Guid.NewGuid():N}.json");
        var staleTopic = $"homeassistant/button/lc_transport_test/launch_{Guid.NewGuid():N}/config";
        const string unrelatedTopic = "homeassistant/button/another_device/unrelated/config";
        await File.WriteAllTextAsync(journal, JsonSerializer.Serialize(new[] { staleTopic, unrelatedTopic }));
        using var certificate = CreateCertificate();
        var factory = new MqttFactory();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var serverOptions = new MqttServerOptionsBuilder();
        if (useTls)
            serverOptions.WithoutDefaultEndpoint()
                .WithEncryptedEndpoint()
                .WithEncryptedEndpointBoundIPAddress(IPAddress.Loopback)
                .WithEncryptedEndpointPort(port)
                .WithEncryptionCertificate(certificate);
        else
            serverOptions.WithDefaultEndpoint()
                .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
                .WithDefaultEndpointPort(port);

        using var server = factory.CreateMqttServer(serverOptions.Build());
        var messages = new ConcurrentQueue<(string Topic, string Payload, bool Retain)>();
        var connections = 0;
        server.ValidatingConnectionAsync += args =>
        {
            if (args.UserName != "test" || args.Password != "test-password")
                args.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
            if (args.ClientId == "lc_transport_test")
            {
                Assert.True(args.CleanSession);
                Assert.Equal(0u, args.SessionExpiryInterval);
                Interlocked.Increment(ref connections);
            }
            return Task.CompletedTask;
        };
        server.InterceptingPublishAsync += args =>
        {
            messages.Enqueue((args.ApplicationMessage.Topic,
                args.ApplicationMessage.ConvertPayloadToString() ?? "", args.ApplicationMessage.Retain));
            return Task.CompletedTask;
        };

        await server.StartAsync();
        try
        {
            var gameId = Guid.NewGuid();
            var host = new TestHost(gameId);
            var coordinator = new LaunchCoordinator(host, [gameId]);
            await using var transport = new MqttLaunchTransport(
                new MqttConnectionSettings("localhost", port, "lc_transport_test", "test/launcher", useTls),
                new MqttCredentials("test", "test-password"), coordinator,
                [new FavoriteGame(gameId, "Test game")], NullLogger.Instance, journal);
            await transport.StartAsync();
            await WaitForAsync(() => messages.Any(m => m.Topic == "test/launcher/availability" && m.Payload == "online"), timeout.Token);
            Assert.Contains(messages, m => m.Topic == "test/launcher/state" && m.Payload.Contains("\"ready\":false"));
            Assert.Contains(messages, m => m.Topic == staleTopic && m.Payload == "" && m.Retain);
            Assert.DoesNotContain(messages, m => m.Topic == unrelatedTopic);
            Assert.DoesNotContain(staleTopic, await File.ReadAllTextAsync(journal));
            host.Ready = true;
            await WaitForAsync(() => messages.Any(m => m.Topic == "test/launcher/state" && m.Payload.Contains("\"ready\":true")), timeout.Token);

            using var publisher = factory.CreateMqttClient();
            var publisherOptions = new MqttClientOptionsBuilder()
                .WithTcpServer("localhost", port)
                .WithProtocolVersion(MqttProtocolVersion.V500)
                .WithCredentials("test", "test-password");
            if (useTls)
                publisherOptions.WithTlsOptions(tls => tls.UseTls().WithCertificateValidationHandler(_ => true));
            await publisher.ConnectAsync(publisherOptions.Build(), timeout.Token);
            var retainedCommand = Command(gameId);
            await publisher.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic($"test/launcher/command/{gameId:D}")
                .WithPayload(JsonSerializer.Serialize(retainedCommand, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
                .WithRetainFlag()
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), timeout.Token);
            await Task.Delay(300, timeout.Token);
            Assert.Equal(0, host.LaunchCount);

            var command = Command(gameId);
            await SendAsync(publisher, gameId, command, timeout.Token);
            await WaitForAsync(() => host.LaunchCount == 1, timeout.Token);
            await SendAsync(publisher, gameId, command, timeout.Token);
            await WaitForAsync(() => messages.Any(m => m.Topic == "test/launcher/result"
                && m.Payload.Contains("\"disposition\":\"duplicate\"")), timeout.Token);
            Assert.Equal(1, host.LaunchCount);

            host.Complete();
            await WaitForAsync(() => coordinator.CurrentState.Status == "completed", timeout.Token);
            var retainedSelection = Command(gameId) with { Action = "select" };
            await SendActionAsync(publisher, "select", retainedSelection, timeout.Token, retain: true);
            await Task.Delay(300, timeout.Token);
            Assert.Null(coordinator.SelectedGameId);
            await SendActionAsync(publisher, "select", Command(gameId) with { Action = "select" }, timeout.Token);
            await WaitForAsync(() => messages.Any(m => m.Topic == "test/launcher/selection"
                && m.Payload == host.Game.Option), timeout.Token);
            Assert.Equal(1, host.LaunchCount);
            host.Installed = false;
            await SendActionAsync(publisher, "launch_selected", Command(Guid.Empty) with { Action = "launch_selected" }, timeout.Token);
            await WaitForAsync(() => messages.Any(m => m.Topic == "test/launcher/result"
                && m.Payload.Contains("game is not installed")), timeout.Token);
            var installCommand = Command(Guid.Empty) with { Action = "install_selected" };
            await SendActionAsync(publisher, "install_selected", installCommand, timeout.Token);
            await WaitForAsync(() => host.InstallCount == 1, timeout.Token);
            await SendActionAsync(publisher, "install_selected", installCommand, timeout.Token);
            Assert.Equal(1, host.InstallCount);
            Assert.Equal(1, host.LaunchCount);
            host.CompleteInstall();
            await WaitForAsync(() => messages.Any(m => m.Topic == "test/launcher/result"
                && m.Payload.Contains("\"disposition\":\"installed\"")), timeout.Token);
            Assert.Equal(1, host.LaunchCount);

            host.Exposed = false;
            await WaitForAsync(() => messages.Any(m => m.Topic.EndsWith($"/launch_{gameId:N}/config")
                && m.Payload.Length == 0 && m.Retain), timeout.Token);
            await WaitForAsync(() => messages.Any(m => m.Topic == "test/launcher/selection" && m.Payload == "None"), timeout.Token);
            await SendActionAsync(publisher, "install_selected", Command(Guid.Empty) with { Action = "install_selected" }, timeout.Token);
            await WaitForAsync(() => messages.Any(m => m.Topic == "test/launcher/result"
                && m.Payload.Contains("game is not allowlisted")), timeout.Token);
            Assert.Equal(1, host.InstallCount);
            Assert.DoesNotContain(messages, m => m.Topic == "test/launcher/result" && m.Retain);

            var discoveryBefore = messages.Count(m => m.Topic.StartsWith("homeassistant/button/"));
            await publisher.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic("homeassistant/status").WithPayload("online").WithRetainFlag()
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), timeout.Token);
            await WaitForAsync(() => messages.Count(m => m.Topic.StartsWith("homeassistant/button/")) > discoveryBefore, timeout.Token);

            var onlineBefore = messages.Count(m => m.Topic == "test/launcher/availability" && m.Payload == "online");
            await server.StopAsync();
            await server.StartAsync();
            await WaitForAsync(() => Volatile.Read(ref connections) >= 2
                && messages.Count(m => m.Topic == "test/launcher/availability" && m.Payload == "online") > onlineBefore,
                timeout.Token);
            Assert.Equal(1, host.LaunchCount);
        }
        finally
        {
            await server.StopAsync();
            File.Delete(journal);
            File.Delete(journal + ".tmp");
        }
    }

    private static LaunchCommand Command(Guid gameId)
    {
        var now = DateTimeOffset.UtcNow;
        return new LaunchCommand(Guid.NewGuid(), "launch", gameId, now, now.AddSeconds(30));
    }

    private static Task SendAsync(IMqttClient publisher, Guid gameId, LaunchCommand command, CancellationToken token) =>
        publisher.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic($"test/launcher/command/{gameId:D}")
            .WithPayload(JsonSerializer.Serialize(command, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), token);

    private static Task SendActionAsync(IMqttClient publisher, string target, LaunchCommand command, CancellationToken token, bool retain = false) =>
        publisher.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic($"test/launcher/command/{target}")
            .WithPayload(JsonSerializer.Serialize(command, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            .WithRetainFlag(retain)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), token);

    private static async Task WaitForAsync(Func<bool> predicate, CancellationToken token)
    {
        while (!predicate())
            await Task.Delay(50, token);
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=wrong-host.invalid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("wrong-host.invalid");
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }

    private sealed class TestHost(Guid gameId) : IGameCatalogHost
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _launchCount;
        private int _installCount;
        private readonly TaskCompletionSource _installation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Ready;
        public volatile bool Installed = true;
        public volatile bool Exposed = true;
        public CatalogGame Game => new(gameId, "Test game", Installed, false, Installed ? "installed" : "not_installed");
        public int LaunchCount => Volatile.Read(ref _launchCount);
        public int InstallCount => Volatile.Read(ref _installCount);
        public void CompleteInstall() { Installed = true; _installation.TrySetResult(); }
        public void Complete() => _completion.TrySetResult();
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => Task.FromResult(Ready);
        public Task<IReadOnlyList<CatalogGame>> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CatalogGame>>(Exposed ? [Game] : []);
        public Task<GameAvailability> GetGameAvailabilityAsync(Guid gameId, CancellationToken cancellationToken) =>
            Task.FromResult(new GameAvailability(Installed, false));
        public Task<IGameLaunchSession> StartInstallAsync(Guid id, CancellationToken token)
        {
            Interlocked.Increment(ref _installCount);
            return Task.FromResult<IGameLaunchSession>(new TestSession(_installation.Task));
        }
        public Task<IGameLaunchSession> StartGameAsync(Guid gameId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _launchCount);
            return Task.FromResult<IGameLaunchSession>(new TestSession(_completion.Task));
        }
    }

    private sealed class TestSession(Task completion) : IGameLaunchSession
    {
        public Task<bool> Started => Task.FromResult(true);
        public Task Completion => completion;
    }
}
