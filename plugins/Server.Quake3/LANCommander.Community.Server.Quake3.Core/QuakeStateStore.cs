using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LANCommander.Community.Server.Quake3.Core;

/// <summary>Caller supplies PluginContext.DataDirectory, never the game content directory.
/// A single JSON replacement commits active state, presets, and last-known catalogue together.</summary>
public sealed class QuakeStateStore : IQuakeStateStore
{
    public const int MaximumStateBytes = 1_048_576;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16
    };
    private readonly string root;

    public QuakeStateStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        root = Path.GetFullPath(dataDirectory);
    }

    public async Task<QuakeServerState> ReadAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        var path = StatePath(serverId);
        var gate = Gates.GetOrAdd(path, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { return await ReadCoreAsync(path, cancellationToken); }
        finally { gate.Release(); }
    }

    public async Task<QuakeServerState> UpdateAsync(Guid serverId,
        Func<QuakeServerState, QuakeServerState> update, CancellationToken cancellationToken = default)
    {
        var path = StatePath(serverId);
        var gate = Gates.GetOrAdd(path, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        string? staging = null;
        try
        {
            var state = update(await ReadCoreAsync(path, cancellationToken));
            Validate(state);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
            if (bytes.Length > MaximumStateBytes) throw new QuakePersistenceException("Saved Quake state is too large.");
            Directory.CreateDirectory(root);
            staging = path + "." + Guid.NewGuid().ToString("N") + ".pending";
            await using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staging, path, true);
            return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new QuakePersistenceException("Could not persist Quake server state.", ex);
        }
        finally
        {
            try
            {
                if (staging is not null)
                {
                    try { File.Delete(staging); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        throw new QuakePersistenceException("Could not clean up staged Quake state; persistence may already have committed.", ex);
                    }
                }
            }
            finally { gate.Release(); }
        }
    }

    private string StatePath(Guid id) => Path.Combine(root, id.ToString("N") + ".json");

    private static async Task<QuakeServerState> ReadCoreAsync(string path, CancellationToken token)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                8192, FileOptions.Asynchronous);
            if (stream.Length > MaximumStateBytes) throw new QuakePersistenceException("Saved Quake state is too large.");
            var bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes, token);
            var state = JsonSerializer.Deserialize<QuakeServerState>(bytes, JsonOptions)
                ?? throw new QuakePersistenceException("Saved Quake state is corrupt.");
            Validate(state);
            return state;
        }
        catch (FileNotFoundException) { return QuakeServerState.Empty; }
        catch (DirectoryNotFoundException) { return QuakeServerState.Empty; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            throw new QuakePersistenceException("Could not read saved Quake state; it may be corrupt.", ex);
        }
    }

    private static void Validate(QuakeServerState state)
    {
        if (state.StartupAttempt is { } attempt &&
            (attempt.Configuration is null || !Enum.IsDefined(attempt.Status)))
            throw new QuakePersistenceException("Saved startup attempt is corrupt.");
        if (state.Presets is null || state.Presets.Count > 32 ||
            state.Presets.Any(p => p.Value is null || QuakeNames.Preset(p.Key) != p.Key))
            throw new QuakePersistenceException("Saved presets are corrupt.");
        if (state.Catalogue is { } catalogue &&
            (catalogue.ContentRoots.IsDefaultOrEmpty || catalogue.ContentRoots.Any(string.IsNullOrWhiteSpace) ||
             catalogue.Maps.IsDefault || catalogue.Maps.Length > QuakeMapCatalogueService.MaximumMapCount ||
             catalogue.Maps.Any(m => !QuakeNames.IsMap(m)) || string.IsNullOrWhiteSpace(catalogue.Signature)))
            throw new QuakePersistenceException("Saved map catalogue is corrupt.");
    }
}
