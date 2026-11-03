using System.Net;
using System.Net.Sockets;
using System.Text;
using LANCommander.Server.Plugins;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace LANCommander.Community.Server.Quake3.Host.Tests;

public sealed class LiveContractTests
{
    [Fact]
    public async Task ApplyUsesActualHostFileContractAndUdpThenRestoresDurableState()
    {
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), $"quake-integration-{Guid.NewGuid():N}");
        const string secret = "abcdefghijklmnopqrstuvwx";
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var provider = new Mock<IServerProvider>(MockBehavior.Strict);
        provider.Setup(p => p.GetAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerInfo { Id = id, Name = "Fixture server" });
        provider.Setup(p => p.ListFilesAsync(id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string path, CancellationToken _) =>
                path == "Server/baseq3/maps"
                    ? new ServerFileInfo[] { new("q3dm17.bsp", path + "/q3dm17.bsp", false, 0, DateTime.UnixEpoch) }
                    : []);
        byte[]? saved = null;
        provider.Setup(p => p.WriteFileAsync(id, "Overlay/baseq3/admin/server.cfg", It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, string _, Stream stream, CancellationToken token) =>
            {
                using var target = new MemoryStream();
                await stream.CopyToAsync(target, token);
                saved = target.ToArray();
            });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Plugins:Quake3:Servers:0:ServerId"] = id.ToString(),
            ["Plugins:Quake3:Servers:0:StartupConfigPath"] = "Overlay/baseq3/admin/server.cfg",
            ["Plugins:Quake3:Servers:0:ExecPath"] = "admin/server.cfg",
            ["Plugins:Quake3:Servers:0:RconHost"] = "127.0.0.1",
            ["Plugins:Quake3:Servers:0:RconPort"] = ((IPEndPoint)peer.Client.LocalEndPoint!).Port.ToString(),
            ["Plugins:Quake3:Servers:0:RconPassword"] = secret
        }).Build();
        var runtime = new Quake3Runtime();
        runtime.Initialize(root);
        var settings = new Quake3Settings(config);
        var auth = new Quake3Authorization(new TestAuthentication("Administrator", true));
        var service = new Quake3Administration(provider.Object, settings, runtime, auth);
        var exchange = Task.Run(async () =>
        {
            var request = await peer.ReceiveAsync(timeout.Token);
            Assert.NotNull(saved);
            Assert.Equal(new byte[] { 255, 255, 255, 255 }, request.Buffer[..4]);
            Assert.Equal($"rcon {secret} exec admin/server.cfg\n", Encoding.UTF8.GetString(request.Buffer.AsSpan(4)));
            byte[] response = [255, 255, 255, 255, .. Encoding.UTF8.GetBytes("print\nexecing\n")];
            await peer.SendAsync(response, request.RemoteEndPoint, timeout.Token);
            request = await peer.ReceiveAsync(timeout.Token);
            Assert.EndsWith(" status\n", Encoding.UTF8.GetString(request.Buffer.AsSpan(4)));
            response = [255, 255, 255, 255, .. Encoding.UTF8.GetBytes("print\nmap: q3dm17\n")];
            await peer.SendAsync(response, request.RemoteEndPoint, timeout.Token);
        }, timeout.Token);
        try
        {
            Assert.False((await service.ApplyAsync(id, MatchConfigurationView.Default, timeout.Token)).Failed);
            Assert.Contains("vstr dadlan_r0", Encoding.UTF8.GetString(saved!));
            var restartedRuntime = new Quake3Runtime();
            restartedRuntime.Initialize(root);
            var snapshot = await new Quake3Administration(provider.Object, settings, restartedRuntime, auth)
                .GetSnapshotAsync(id, false, timeout.Token);
            Assert.True(snapshot.Online);
            Assert.True(snapshot.CatalogKnown);
            Assert.False(snapshot.CatalogIsStale);
            Assert.Null(snapshot.StartupWarning);
            Assert.Equal(["q3dm17"], snapshot.Active!.Rotation);
            await exchange;
            provider.Verify(p => p.WriteFileAsync(id, "Overlay/baseq3/admin/server.cfg", It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            timeout.Cancel();
            await exchange.WaitAsync(TimeSpan.FromSeconds(2));
            if (Directory.Exists(root))
            {
                File.Delete(Path.Combine(root, $"{id:N}.json"));
                Directory.Delete(root);
            }
        }
    }
}
