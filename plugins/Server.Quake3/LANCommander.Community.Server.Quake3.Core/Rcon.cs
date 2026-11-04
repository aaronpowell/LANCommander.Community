using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace LANCommander.Community.Server.Quake3.Core;

public interface IRcon
{
    Task<string> CommandAsync(string command, CancellationToken cancellationToken = default);
}

public sealed class RconException(string message) : Exception(message);

public sealed class RconSettings
{
    public string Host { get; }
    public int Port { get; }
    public string Password { get; }
    public RconSettings(string host, int port, string password)
    {
        RconValidation.ValidateEndpoint(host, port);
        if (password is null || !Regex.IsMatch(password, "\\A[A-Za-z0-9_-]{24,64}\\z"))
            throw new ArgumentException("RCON password must contain 24-64 URL-safe characters.");
        Host = host;
        Port = port;
        Password = password;
    }
    public override string ToString() => "RCON settings (credentials redacted)";
}

internal static class RconValidation
{
    internal static void ValidateEndpoint(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 ||
            Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new ArgumentException("Invalid RCON host.");
        if (port is < 1 or > 65535) throw new ArgumentException("Invalid RCON port.");
    }

    internal static void ValidateCommand(string command)
    {
        if (command is "status" or "vstr nextmap" or "map_restart 0") return;
        if (command.StartsWith("map ", StringComparison.Ordinal) && QuakeNames.IsMap(command[4..])) return;
        if (command.StartsWith("exec ", StringComparison.Ordinal) && IsExecPath(command[5..])) return;
        throw new ArgumentException("Unsupported RCON command.");
    }

    internal static bool IsExecPath(string? path) =>
        path is { Length: > 0 and <= 256 } &&
        path.Split('/').All(part => part.Length > 0 && part is not "." and not ".." &&
            Regex.IsMatch(part, "\\A[A-Za-z0-9_.-]+\\z"));
}

/// <summary>Connected UDP limits replies to the configured peer. All instances share a 600ms
/// endpoint gate; first response is bounded, multipart responses stop at 150ms idle or one second.</summary>
public sealed class UdpRcon : IRcon
{
    public const int MaximumResponseBytes = 65535;
    public static readonly TimeSpan CommandSpacing = TimeSpan.FromMilliseconds(600);
    public static readonly TimeSpan MultipartIdle = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan MultipartDuration = TimeSpan.FromSeconds(1);
    private static readonly byte[] Header = [255, 255, 255, 255, (byte)'p', (byte)'r', (byte)'i', (byte)'n', (byte)'t', 10];
    private static readonly ConcurrentDictionary<IPEndPoint, EndpointGate> Gates = new();
    private readonly string host;
    private readonly int port;
    private readonly string password;
    private readonly TimeSpan responseTimeout;

    public UdpRcon(RconSettings settings) : this(settings.Host, settings.Port, settings.Password) { }

    // Transport accepts shorter safe secrets for loopback fixtures; application settings enforce 24-64.
    public UdpRcon(string host, int port, string password, TimeSpan? responseTimeout = null)
    {
        RconValidation.ValidateEndpoint(host, port);
        if (password is null || !Regex.IsMatch(password, "\\A[A-Za-z0-9_-]{1,64}\\z"))
            throw new ArgumentException("Invalid RCON password.");
        this.host = host;
        this.port = port;
        this.password = password;
        this.responseTimeout = responseTimeout ?? TimeSpan.FromSeconds(3);
        if (this.responseTimeout <= TimeSpan.Zero || this.responseTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentException("Invalid RCON response timeout.");
    }

    public async Task<string> CommandAsync(string command, CancellationToken cancellationToken = default)
    {
        RconValidation.ValidateCommand(command);
        IPEndPoint endpoint;
        try
        {
            using var resolutionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            resolutionTimeout.CancelAfter(responseTimeout);
            var addresses = await Dns.GetHostAddressesAsync(host, resolutionTimeout.Token);
            var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?? addresses.FirstOrDefault() ?? throw new RconException("RCON endpoint could not be resolved.");
            endpoint = new(address, port);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new RconException("No RCON response. Check the game server and RCON settings.");
        }
        var gate = Gates.GetOrAdd(endpoint, _ => new());
        await gate.Semaphore.WaitAsync(cancellationToken);
        try
        {
            if (gate.LastSent is { } last)
            {
                var remaining = CommandSpacing - Stopwatch.GetElapsedTime(last);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellationToken);
            }
            using var socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            await socket.ConnectAsync(endpoint, cancellationToken);
            var packet = Encoding.UTF8.GetBytes($"rcon {password} {command}\n");
            var wire = new byte[packet.Length + 4];
            wire.AsSpan(0, 4).Fill(255);
            packet.CopyTo(wire, 4);
            gate.LastSent = Stopwatch.GetTimestamp();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(responseTimeout);
            await socket.SendAsync(wire, SocketFlags.None, timeout.Token);
            var buffer = new byte[MaximumResponseBytes];
            var count = await socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token);
            var text = new StringBuilder();
            Append(buffer, count, text);
            var total = count;
            var first = Stopwatch.GetTimestamp();
            while (total < MaximumResponseBytes)
            {
                var remaining = MultipartDuration - Stopwatch.GetElapsedTime(first);
                if (remaining <= TimeSpan.Zero) break;
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(remaining < MultipartIdle ? remaining : MultipartIdle);
                try { count = await socket.ReceiveAsync(buffer, SocketFlags.None, idle.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { break; }
                if (count > MaximumResponseBytes - total) throw new RconException("RCON response exceeds the size limit.");
                total += count;
                Append(buffer, count, text);
            }
            var result = text.ToString().Trim('\0', '\n', ' ');
            if (new[] { "bad rconpassword", "no rconpassword", "couldn't exec", "can't find map", "unknown command" }
                .Any(error => result.Contains(error, StringComparison.OrdinalIgnoreCase)))
                throw new RconException("The game server rejected the RCON command. Check RCON settings and installed maps.");
            return result.Replace(password, "[redacted]", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new RconException("No RCON response. Check the game server and RCON settings.");
        }
        finally { gate.Semaphore.Release(); }
    }

    private static void Append(byte[] packet, int length, StringBuilder text)
    {
        if (length < Header.Length || !packet.AsSpan(0, Header.Length).SequenceEqual(Header))
            throw new RconException("Unexpected RCON response from the game server.");
        text.Append(Encoding.UTF8.GetString(packet, Header.Length, length - Header.Length));
    }

    private sealed class EndpointGate
    {
        internal SemaphoreSlim Semaphore { get; } = new(1, 1);
        internal long? LastSent { get; set; }
    }
}
