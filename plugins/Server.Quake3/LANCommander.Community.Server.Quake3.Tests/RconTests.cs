using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LANCommander.Community.Server.Quake3.Core;
using Xunit;

namespace LANCommander.Community.Server.Quake3.Tests;

public sealed class RconTests
{
    private static byte[] Reply(string text) => [255, 255, 255, 255, .. Encoding.UTF8.GetBytes("print\n" + text)];

    [Fact]
    public async Task ConnectedLoopbackPacketAndMultipartMatchSpec()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var transport = new UdpRcon("127.0.0.1", ((IPEndPoint)server.Client.LocalEndPoint!).Port, "test-secret");
        var exchange = Task.Run(async () =>
        {
            var request = await server.ReceiveAsync(timeout.Token);
            Assert.Equal((byte[])[255, 255, 255, 255, .. Encoding.UTF8.GetBytes("rcon test-secret status\n")], request.Buffer);
            await server.SendAsync(Reply("map: q3dm17\n"), request.RemoteEndPoint, timeout.Token);
            await server.SendAsync(Reply("players: 2\n"), request.RemoteEndPoint, timeout.Token);
        }, timeout.Token);
        Assert.Equal("map: q3dm17\nplayers: 2", await transport.CommandAsync("status", timeout.Token));
        await exchange;
    }

    [Theory]
    [InlineData("Bad rconpassword. test-secret")]
    [InlineData("No rconpassword")]
    [InlineData("couldn't exec file test-secret")]
    [InlineData("can't find map")]
    [InlineData("unknown command")]
    public async Task RecognizedFailuresNeverEchoCredentials(string response)
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var transport = new UdpRcon("127.0.0.1", ((IPEndPoint)server.Client.LocalEndPoint!).Port, "test-secret");
        var exchange = Task.Run(async () =>
        {
            var request = await server.ReceiveAsync(timeout.Token);
            await server.SendAsync(Reply(response), request.RemoteEndPoint, timeout.Token);
        }, timeout.Token);
        var exception = await Assert.ThrowsAsync<RconException>(() => transport.CommandAsync("status", timeout.Token));
        Assert.DoesNotContain("test-secret", exception.ToString());
        await exchange;
    }

    [Fact]
    public async Task InvalidPacketsAreRejectedAndSuccessfulEchoIsRedacted()
    {
        foreach (var reply in new byte[][] { [1, 2, 3], Reply("echo test-secret") })
        {
            using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var transport = new UdpRcon("127.0.0.1", ((IPEndPoint)server.Client.LocalEndPoint!).Port, "test-secret");
            var exchange = Task.Run(async () =>
            {
                var request = await server.ReceiveAsync(timeout.Token);
                await server.SendAsync(reply, request.RemoteEndPoint, timeout.Token);
            }, timeout.Token);
            if (reply.Length == 3)
                await Assert.ThrowsAsync<RconException>(() => transport.CommandAsync("status", timeout.Token));
            else
                Assert.Equal("echo [redacted]", await transport.CommandAsync("status", timeout.Token));
            await exchange;
        }
    }

    [Fact]
    public async Task PacingSharedAcrossInstancesAndCancellationWhileWaiting()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var first = new UdpRcon("127.0.0.1", port, "test-secret");
        var second = new UdpRcon("localhost", port, "other-secret");
        var times = new List<long>();
        var exchange = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                var request = await server.ReceiveAsync(timeout.Token);
                times.Add(Stopwatch.GetTimestamp());
                await server.SendAsync(Reply("ok"), request.RemoteEndPoint, timeout.Token);
            }
        }, timeout.Token);
        await first.CommandAsync("status", timeout.Token);
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.CommandAsync("status", cancelled.Token));
        await second.CommandAsync("status", timeout.Token);
        await exchange;
        Assert.True(Stopwatch.GetElapsedTime(times[0], times[1]) >= TimeSpan.FromMilliseconds(550));
    }

    [Fact]
    public async Task ResponseTimeoutAndCancellationAreBounded()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var transport = new UdpRcon("127.0.0.1", port, "test-secret", TimeSpan.FromMilliseconds(100));
        var watch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<RconException>(() => transport.CommandAsync("status"));
        Assert.DoesNotContain("test-secret", error.ToString());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.CommandAsync("status", cancelled.Token));
    }

    [Fact]
    public async Task ConnectedSocketIgnoresSpoofedPeer()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var stranger = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var transport = new UdpRcon("127.0.0.1", ((IPEndPoint)server.Client.LocalEndPoint!).Port, "test-secret");
        var exchange = Task.Run(async () =>
        {
            var request = await server.ReceiveAsync(timeout.Token);
            await stranger.SendAsync(Reply("spoofed"), request.RemoteEndPoint, timeout.Token);
            await server.SendAsync(Reply("real"), request.RemoteEndPoint, timeout.Token);
        }, timeout.Token);
        Assert.Equal("real", await transport.CommandAsync("status", timeout.Token));
        await exchange;
    }

    [Fact]
    public async Task TotalMultipartBytesAreBounded()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var transport = new UdpRcon("127.0.0.1", ((IPEndPoint)server.Client.LocalEndPoint!).Port, "test-secret");
        var exchange = Task.Run(async () =>
        {
            var request = await server.ReceiveAsync(timeout.Token);
            await server.SendAsync(Reply(new string('a', 40_000)), request.RemoteEndPoint, timeout.Token);
            await server.SendAsync(Reply(new string('b', 40_000)), request.RemoteEndPoint, timeout.Token);
        }, timeout.Token);
        var error = await Assert.ThrowsAsync<RconException>(() => transport.CommandAsync("status", timeout.Token));
        Assert.Contains("size limit", error.Message);
        await exchange;
    }

    [Fact]
    public async Task MultipartReceptionStopsWithinOneSecondAfterFirstResponse()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var transport = new UdpRcon("127.0.0.1", ((IPEndPoint)server.Client.LocalEndPoint!).Port, "test-secret");
        var exchange = Task.Run(async () =>
        {
            var request = await server.ReceiveAsync(timeout.Token);
            for (var i = 0; i < 15; i++)
            {
                await server.SendAsync(Reply("x"), request.RemoteEndPoint, timeout.Token);
                await Task.Delay(100, timeout.Token);
            }
        }, timeout.Token);
        var watch = Stopwatch.StartNew();
        var text = await transport.CommandAsync("status", timeout.Token);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(1400));
        Assert.InRange(text.Length, 5, 12);
        await exchange;
    }

    [Fact]
    public async Task TransportRejectsCommandsOutsideTypedAllowlist()
    {
        var transport = new UdpRcon("127.0.0.1", 27600, "test-secret");
        foreach (var command in new[] { "quit", "status;quit", "map q3dm17\nquit", "exec ../secret", "exec config;quit" })
            await Assert.ThrowsAsync<ArgumentException>(() => transport.CommandAsync(command));
    }
}
