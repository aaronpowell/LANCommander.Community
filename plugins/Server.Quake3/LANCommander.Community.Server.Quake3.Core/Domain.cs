using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LANCommander.Community.Server.Quake3.Core;

public enum QuakeGameMode { FreeForAll = 0, Duel = 1, TeamDeathmatch = 3, CaptureTheFlag = 4 }

public sealed record QuakeConfiguration
{
    public QuakeGameMode Mode { get; }
    public int TimeLimit { get; }
    public int FragLimit { get; }
    public int CaptureLimit { get; }
    public ImmutableArray<string> Rotation { get; }

    public QuakeConfiguration(QuakeGameMode mode, int timeLimit, int fragLimit, int captureLimit,
        ImmutableArray<string> rotation)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentException("Unsupported game mode.");
        if (timeLimit is < 0 or > 120) throw new ArgumentException("Time limit must be between 0 and 120.");
        if (fragLimit is < 0 or > 1000) throw new ArgumentException("Frag limit must be between 0 and 1000.");
        if (captureLimit is < 0 or > 100) throw new ArgumentException("Capture limit must be between 0 and 100.");
        if (rotation.IsDefaultOrEmpty || rotation.Length > 32)
            throw new ArgumentException("Choose between 1 and 32 maps.");
        if (rotation.Any(map => !QuakeNames.IsMap(map))) throw new ArgumentException("Invalid map name.");
        Mode = mode;
        TimeLimit = timeLimit;
        FragLimit = fragLimit;
        CaptureLimit = captureLimit;
        Rotation = rotation;
    }

    public static QuakeConfiguration Default { get; } =
        new(QuakeGameMode.FreeForAll, 15, 30, 8, ["q3dm17"]);

    public void ValidateInstalled(IEnumerable<string> installed)
    {
        var maps = installed.ToHashSet(StringComparer.Ordinal);
        if (Rotation.Any(map => !maps.Contains(map))) throw new ArgumentException("A rotation map is not installed.");
    }
}

public static partial class QuakeNames
{
    [GeneratedRegex("\\A[A-Za-z0-9_-]{1,64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex MapPattern();
    public static bool IsMap(string? value) => value is not null && MapPattern().IsMatch(value);
    public static string Preset(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        name = name.Trim();
        if (name.Length is < 1 or > 48) throw new ArgumentException("Preset name must be between 1 and 48 characters.");
        return name;
    }
}

public static class QuakeConfigRenderer
{
    public const string StatePrefix = "// dadlan-admin-state: ";
    public static string Render(QuakeConfiguration? configuration)
    {
        var state = configuration is null ? "null" : JsonSerializer.Serialize(new
        {
            mode = ((int)configuration.Mode).ToString(System.Globalization.CultureInfo.InvariantCulture),
            timelimit = configuration.TimeLimit,
            fraglimit = configuration.FragLimit,
            capturelimit = configuration.CaptureLimit,
            rotation = configuration.Rotation
        });
        var lines = new List<string> { StatePrefix + state };
        if (configuration is not null)
        {
            lines.Add($"set g_gametype {(int)configuration.Mode}");
            lines.Add($"set timelimit {configuration.TimeLimit}");
            lines.Add($"set fraglimit {configuration.FragLimit}");
            lines.Add($"set capturelimit {configuration.CaptureLimit}");
            for (var i = 0; i < configuration.Rotation.Length; i++)
                lines.Add($"set dadlan_r{i} \"map {configuration.Rotation[i]}; set nextmap vstr dadlan_r{(i + 1) % configuration.Rotation.Length}\"");
            lines.Add("vstr dadlan_r0");
        }
        return string.Join('\n', lines) + "\n";
    }
}

public sealed record QuakeCatalogueMetadata(
    ImmutableArray<string> ContentRoots, string Signature, ImmutableArray<string> Maps, DateTimeOffset ScannedAt);

public enum QuakeStartupStatus { WriteUncertain, SavedLiveUncertain, Applied }
public sealed record QuakeStartupAttempt(QuakeConfiguration Configuration, QuakeStartupStatus Status);

public sealed record QuakeServerState(
    QuakeConfiguration? Active,
    ImmutableDictionary<string, QuakeConfiguration> Presets,
    QuakeCatalogueMetadata? Catalogue,
    QuakeStartupAttempt? StartupAttempt = null)
{
    public static QuakeServerState Empty { get; } = new(null,
        ImmutableDictionary<string, QuakeConfiguration>.Empty, null);
}

public enum QuakeCatalogueAvailability { Fresh, Stale, Unavailable }
public sealed record QuakeMapCatalogue(
    QuakeCatalogueAvailability Availability, ImmutableArray<string> Maps,
    DateTimeOffset? ScannedAt, string? Error)
{
    public bool HasKnownCatalogue => ScannedAt is not null;
    public void RequireFresh()
    {
        if (Availability != QuakeCatalogueAvailability.Fresh)
            throw new QuakeCatalogueException("A current map catalogue is required. Refresh when server files are available.");
    }
}

public sealed class QuakeCatalogueException(string message) : Exception(message);
public sealed class QuakePersistenceException(string message, Exception? innerException = null) : Exception(message, innerException);
public sealed class QuakeFilesUnavailableException(string message = "Server files are unavailable.") : IOException(message);

/// <summary>Internal integration seam, not a LANCommander host API. Lists immediate files only.
/// Missing directories return an empty list; unavailable engines or failed reads must throw.
/// Write can partially commit before throwing; callers must treat failed or cancelled writes
/// as uncertain and must not assume the destination is unchanged.</summary>
public interface IQuakeServerFiles
{
    Task<IReadOnlyList<QuakeFileEntry>> ListAsync(Guid serverId, string directory, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(Guid serverId, string path, CancellationToken cancellationToken);
    Task WriteAsync(Guid serverId, string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
}

public sealed record QuakeFileEntry(string Path, long Length, DateTimeOffset LastModified);

public interface IQuakeStateStore
{
    Task<QuakeServerState> ReadAsync(Guid serverId, CancellationToken cancellationToken = default);
    Task<QuakeServerState> UpdateAsync(Guid serverId, Func<QuakeServerState, QuakeServerState> update,
        CancellationToken cancellationToken = default);
}
