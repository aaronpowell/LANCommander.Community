using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace LANCommander.Community.Server.Quake3;

public sealed record Quake3Association(
    Guid ServerId, string ContentRoot, string StartupConfigPath,
    string ExecPath, string RconHost, int RconPort, string RconPassword)
{
    public IReadOnlyList<string> MapRoots =>
        [Join(ContentRoot, "Server/baseq3"), Join(ContentRoot, "Overlay/baseq3")];

    private static string Join(string root, string child) => root.Length == 0 ? child : $"{root}/{child}";
}

public sealed class Quake3Settings
{
    public IReadOnlyDictionary<Guid, Quake3Association> Servers { get; }

    public Quake3Settings(IConfiguration configuration)
    {
        var servers = new Dictionary<Guid, Quake3Association>();
        foreach (var entry in configuration.GetSection("Plugins:Quake3:Servers").GetChildren())
        {
            if (!Guid.TryParse(entry["ServerId"], out var id) || id == Guid.Empty)
                throw new InvalidDataException("Quake 3 association requires a valid ServerId.");
            var root = entry["ContentRoot"] ?? "";
            var startup = Required(entry, "StartupConfigPath");
            var exec = Required(entry, "ExecPath");
            CheckPath(root, allowEmpty: true);
            CheckPath(startup, allowEmpty: false);
            CheckPath(exec, allowEmpty: false);
            var host = Required(entry, "RconHost");
            if (host.Length > 253 || Uri.CheckHostName(host) == UriHostNameType.Unknown)
                throw new InvalidDataException("Quake 3 RconHost must be a hostname or IP address.");
            if (!int.TryParse(entry["RconPort"], out var port) || port is < 1 or > 65535)
                throw new InvalidDataException("Quake 3 RconPort must be between 1 and 65535.");
            var password = Required(entry, "RconPassword");
            if (!Regex.IsMatch(password, @"\A[A-Za-z0-9_-]{24,64}\z"))
                throw new InvalidDataException("Quake 3 RCON requires 24-64 URL-safe password characters.");
            if (!servers.TryAdd(id, new(id, root, startup, exec, host, port, password)))
                throw new InvalidDataException("Quake 3 contains duplicate server associations.");
            if (servers.Count > 128)
                throw new InvalidDataException("Quake 3 supports at most 128 associated servers.");
        }
        Servers = servers;
    }

    private static string Required(IConfiguration section, string key) =>
        !string.IsNullOrWhiteSpace(section[key]) ? section[key]! :
            throw new InvalidDataException($"Quake 3 association requires {key}.");

    internal static void CheckPath(string path, bool allowEmpty)
    {
        if (allowEmpty && path.Length == 0) return;
        if (path.Length is 0 or > 512 || path.Split('/').Any(segment =>
            segment.Length == 0 || segment is "." or ".." ||
            !Regex.IsMatch(segment, @"\A[A-Za-z0-9_.-]+\z")))
            throw new InvalidDataException("Quake 3 paths must be safe engine-root-relative paths.");
    }
}
