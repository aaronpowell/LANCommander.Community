using System.Collections.Immutable;
using LANCommander.Community.Server.Quake3.Core;
using LANCommander.Server.Plugins;

namespace LANCommander.Community.Server.Quake3;

public sealed class Quake3Runtime
{
    private QuakeControlFactory? _factory;
    public QuakeControlFactory Factory => _factory ??
        throw new InvalidOperationException("Quake plugin durable storage has not been initialized.");

    public void Initialize(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _factory ??= new QuakeControlFactory(new QuakeStateStore(dataDirectory), dataDirectory);
    }
}

public sealed class Quake3Administration(
    IServerProvider servers, Quake3Settings settings, Quake3Runtime runtime,
    Quake3Authorization authorization)
{
    public async Task<IReadOnlyList<AssociatedServer>> GetServersAsync(CancellationToken token = default)
    {
        await authorization.RequireAdministratorAsync(token);
        return (await servers.GetAsync(token)).Where(s => settings.Servers.ContainsKey(s.Id))
            .Select(s => new AssociatedServer(s.Id, s.Name)).ToArray();
    }

    private async Task<QuakeControlService> ControllerAsync(Guid id, CancellationToken token)
    {
        await authorization.RequireAdministratorAsync(token);
        if (!settings.Servers.TryGetValue(id, out var association))
            throw new InvalidOperationException("The selected LC server is not associated with Quake 3.");
        if (await servers.GetAsync(id, token) is null)
            throw new InvalidOperationException("The associated LC server no longer exists.");
        return runtime.Factory.Create(id, new QuakeServerOptions(
            association.MapRoots.ToImmutableArray(), association.StartupConfigPath,
            association.ExecPath, new RconSettings(association.RconHost, association.RconPort,
                association.RconPassword)), new HostServerFiles(servers));
    }

    public async Task<Quake3View> GetSnapshotAsync(Guid id, bool refresh, CancellationToken token = default)
    {
        var controller = await ControllerAsync(id, token);
        var catalogue = await controller.GetCatalogueAsync(refresh, token);
        var state = await controller.GetStateAsync(token);
        var online = true;
        string status;
        try { status = await controller.StatusAsync(token); }
        catch (RconException exception) { online = false; status = exception.Message; }
        return new(online, status, catalogue.HasKnownCatalogue,
            catalogue.Availability != QuakeCatalogueAvailability.Fresh, catalogue.Error,
            catalogue.Maps, state.Active is null ? null : View(state.Active),
            state.Presets.ToDictionary(p => p.Key, p => View(p.Value), StringComparer.Ordinal),
            state.StartupAttempt?.Status switch
            {
                QuakeStartupStatus.WriteUncertain => "Startup write is uncertain and may be partial. Reapply or repair startup before restarting the server.",
                QuakeStartupStatus.SavedLiveUncertain => "Startup was saved, but live apply is unconfirmed. The last acknowledged active state may differ.",
                _ => null
            },
            state.StartupAttempt is { } attempt ? View(attempt.Configuration) : null);
    }

    public async Task<AdministrationResult> ApplyAsync(Guid id, MatchConfigurationView config, CancellationToken token = default)
    {
        var controller = await ControllerAsync(id, token);
        var result = await controller.ApplyAsync(Configuration(config), token);
        return new(result.Message + (result.Response is null ? "" : "\n" + result.Response), !result.Succeeded);
    }

    public async Task<AdministrationResult> SavePresetAsync(Guid id, string name, MatchConfigurationView config, CancellationToken token = default)
    {
        var controller = await ControllerAsync(id, token);
        await controller.SavePresetAsync(name, Configuration(config), token);
        return new("Preset saved. Startup configuration and running game have not changed.");
    }

    public async Task<AdministrationResult> NextAsync(Guid id, CancellationToken token = default) =>
        new("Command acknowledged.\n" + await (await ControllerAsync(id, token)).NextAsync(token));
    public async Task<AdministrationResult> RestartAsync(Guid id, CancellationToken token = default) =>
        new("Match restart acknowledged.\n" + await (await ControllerAsync(id, token)).RestartAsync(token));
    public async Task<AdministrationResult> ChangeMapAsync(Guid id, string map, CancellationToken token = default) =>
        new("Map change acknowledged.\n" + await (await ControllerAsync(id, token)).ChangeMapAsync(map, token));

    private static QuakeConfiguration Configuration(MatchConfigurationView config) =>
        new((QuakeGameMode)config.Mode, config.TimeLimit, config.FragLimit, config.CaptureLimit,
            config.Rotation.ToImmutableArray());
    private static MatchConfigurationView View(QuakeConfiguration config) =>
        new((int)config.Mode, config.TimeLimit, config.FragLimit, config.CaptureLimit, config.Rotation);
}

internal sealed class HostServerFiles(IServerProvider servers) : IQuakeServerFiles
{
    public async Task<IReadOnlyList<QuakeFileEntry>> ListAsync(Guid id, string path, CancellationToken token)
    {
        try
        {
            var files = await servers.ListFilesAsync(id, path, token);
            if (files.Count > QuakeMapCatalogueService.MaximumListedFiles)
                throw new QuakeCatalogueException("Server file listing exceeds the catalogue limit.");
            return files.Where(f => !f.IsDirectory && (f.Path.EndsWith(".pk3", StringComparison.OrdinalIgnoreCase)
                || f.Path.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase)))
                .Select(f => new QuakeFileEntry(f.Path,
                    f.Size ?? throw new QuakeFilesUnavailableException("Server file sizes are unavailable."),
                    f.LastModified is { } modified
                        ? new DateTimeOffset(DateTime.SpecifyKind(modified, DateTimeKind.Utc))
                        : throw new QuakeFilesUnavailableException("Server file modification times are unavailable.")))
                .ToArray();
        }
        catch (DirectoryNotFoundException) { return []; }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException or KeyNotFoundException)
        { throw new QuakeFilesUnavailableException("Selected engine cannot provide server files: " + exception.Message); }
    }

    public async Task<Stream> OpenReadAsync(Guid id, string path, CancellationToken token)
    {
        try { return await servers.OpenFileAsync(id, path, token); }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException or KeyNotFoundException)
        { throw new QuakeFilesUnavailableException("Selected engine cannot open the map pack: " + exception.Message); }
    }

    public async Task WriteAsync(Guid id, string path, ReadOnlyMemory<byte> content, CancellationToken token)
    {
        using var stream = new MemoryStream(content.ToArray(), writable: false);
        try { await servers.WriteFileAsync(id, path, stream, token); }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException or KeyNotFoundException)
        { throw new QuakeFilesUnavailableException("Selected engine cannot write startup configuration: " + exception.Message); }
    }
}
