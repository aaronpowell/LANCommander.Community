using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;

namespace LANCommander.Community.Server.Quake3.Core;

public sealed record QuakeServerOptions(
    ImmutableArray<string> ContentRoots, string StartupConfigPath, string ExecPath, RconSettings Rcon)
{
    internal void Validate()
    {
        QuakeMapCatalogueService.ValidateRoots(ContentRoots);
        if (string.IsNullOrWhiteSpace(StartupConfigPath) || StartupConfigPath.Any(char.IsControl))
            throw new ArgumentException("Invalid startup configuration path.");
        if (!RconValidation.IsExecPath(ExecPath)) throw new ArgumentException("Invalid game-relative exec path.");
        ArgumentNullException.ThrowIfNull(Rcon);
    }
}

public enum QuakeApplyOutcome
{
    Applied,
    StartupWriteFailed,
    StartupWriteCancelled,
    StartupSavedButLiveApplyFailed,
    StartupSavedButLiveApplyCancelled,
    StartupSavedButStateSaveFailed,
    StartupSavedAndLiveAppliedButStateSaveFailed
}

/// <summary>Failed/uncertain startup writes never execute live commands. Active means the last
/// acknowledged apply; startup can differ after failure. Do not present these outcomes as success.</summary>
public sealed record QuakeApplyResult(QuakeApplyOutcome Outcome, string Message, string? Response = null)
{
    public bool Succeeded => Outcome == QuakeApplyOutcome.Applied;
    public bool StartupSaved => Outcome is not QuakeApplyOutcome.StartupWriteFailed and not QuakeApplyOutcome.StartupWriteCancelled;
}

/// <summary>Register once per plugin lifetime. Create controllers with the scoped host file adapter;
/// the factory owns server gates and the catalogue service, not the scoped adapter.</summary>
public sealed class QuakeControlFactory
{
    private readonly IQuakeStateStore store;
    private readonly QuakeMapCatalogueService catalogue;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();
    public QuakeControlFactory(IQuakeStateStore store, string durableRoot)
    {
        this.store = store;
        catalogue = new(store, durableRoot);
    }
    public QuakeControlService Create(Guid serverId, QuakeServerOptions options, IQuakeServerFiles files, IRcon? rcon = null)
    {
        options.Validate();
        return new(serverId, options, files, store, catalogue, rcon ?? new UdpRcon(options.Rcon),
            gates.GetOrAdd(serverId, _ => new(1, 1)));
    }
}

public sealed class QuakeControlService
{
    private readonly Guid serverId;
    private readonly QuakeServerOptions options;
    private readonly IQuakeServerFiles files;
    private readonly IQuakeStateStore store;
    private readonly QuakeMapCatalogueService catalogue;
    private readonly IRcon rcon;
    private readonly SemaphoreSlim gate;

    internal QuakeControlService(Guid serverId, QuakeServerOptions options, IQuakeServerFiles files,
        IQuakeStateStore store, QuakeMapCatalogueService catalogue, IRcon rcon, SemaphoreSlim gate)
    {
        this.serverId = serverId;
        this.options = options;
        this.files = files;
        this.store = store;
        this.catalogue = catalogue;
        this.rcon = rcon;
        this.gate = gate;
    }

    public Task<QuakeServerState> GetStateAsync(CancellationToken cancellationToken = default) =>
        SerializedAsync(() => store.ReadAsync(serverId, cancellationToken), cancellationToken);
    public Task<QuakeMapCatalogue> GetCatalogueAsync(bool refresh = false, CancellationToken cancellationToken = default) =>
        SerializedAsync(() => CatalogueAsync(refresh, cancellationToken), cancellationToken);
    public Task<string> StatusAsync(CancellationToken cancellationToken = default) =>
        SerializedAsync(() => rcon.CommandAsync("status", cancellationToken), cancellationToken);

    public Task<QuakeServerState> SavePresetAsync(string name, QuakeConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        name = QuakeNames.Preset(name);
        ArgumentNullException.ThrowIfNull(configuration);
        return SerializedAsync(async () =>
        {
            var maps = await CatalogueAsync(false, cancellationToken);
            maps.RequireFresh();
            configuration.ValidateInstalled(maps.Maps);
            return await store.UpdateAsync(serverId, state =>
            {
                if (!state.Presets.ContainsKey(name) && state.Presets.Count >= 32)
                    throw new ArgumentException("At most 32 presets can be saved.");
                return state with { Presets = state.Presets.SetItem(name, configuration) };
            }, cancellationToken);
        }, cancellationToken);
    }

    public Task<QuakeServerState> DeletePresetAsync(string name, CancellationToken cancellationToken = default)
    {
        name = QuakeNames.Preset(name);
        return SerializedAsync(() => store.UpdateAsync(serverId,
            state => state with { Presets = state.Presets.Remove(name) }, cancellationToken), cancellationToken);
    }

    public Task<QuakeApplyResult> ApplyAsync(QuakeConfiguration configuration, CancellationToken cancellationToken = default) =>
        SerializedAsync<QuakeApplyResult>(async () =>
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var maps = await CatalogueAsync(false, cancellationToken);
            maps.RequireFresh();
            configuration.ValidateInstalled(maps.Maps);
            // Read state before writing startup so known corruption prevents external mutation.
            await store.ReadAsync(serverId, cancellationToken);
            // Journal intent before a non-atomic external write; a crash/partial write must
            // remain visible after request completion or plugin restart.
            await store.UpdateAsync(serverId, state => state with
            {
                StartupAttempt = new(configuration, QuakeStartupStatus.WriteUncertain)
            }, cancellationToken);
            try
            {
                await files.WriteAsync(serverId, options.StartupConfigPath,
                    Encoding.UTF8.GetBytes(QuakeConfigRenderer.Render(configuration)), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new(QuakeApplyOutcome.StartupWriteCancelled,
                    "Startup write was cancelled and may be partial. Live apply was not sent; the last acknowledged active state was preserved. Reapply or repair startup before restarting the server.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new(QuakeApplyOutcome.StartupWriteFailed,
                    "Startup write failed and may be partial. Live apply was not sent; the last acknowledged active state was preserved. Reapply or repair startup before restarting the server.");
            }
            try
            {
                await store.UpdateAsync(serverId, state => state with
                {
                    StartupAttempt = new(configuration, QuakeStartupStatus.SavedLiveUncertain)
                }, CancellationToken.None);
            }
            catch (QuakePersistenceException)
            {
                return new(QuakeApplyOutcome.StartupSavedButStateSaveFailed,
                    "Startup saved, but durable write confirmation failed. Live apply was not sent; inspect startup before restarting the server.");
            }
            string response;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                response = await rcon.CommandAsync("exec " + options.ExecPath, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new(QuakeApplyOutcome.StartupSavedButLiveApplyCancelled,
                    "Startup configuration saved, but live apply was cancelled. Live state is uncertain; previous active state was preserved.");
            }
            catch (RconException)
            {
                return new(QuakeApplyOutcome.StartupSavedButLiveApplyFailed,
                    "Startup configuration saved, but live apply failed. Live state is uncertain; previous active state was preserved.");
            }
            try
            {
                // Once acknowledged, commit the result even if the request is subsequently cancelled.
                await store.UpdateAsync(serverId, state => state with
                {
                    Active = configuration,
                    StartupAttempt = new(configuration, QuakeStartupStatus.Applied)
                }, CancellationToken.None);
            }
            catch (QuakePersistenceException)
            {
                return new(QuakeApplyOutcome.StartupSavedAndLiveAppliedButStateSaveFailed,
                    "Startup saved and live apply acknowledged, but durable active state could not be saved. Refresh and reconcile before retrying.");
            }
            return new(QuakeApplyOutcome.Applied, "Startup configuration saved; live apply acknowledged.", response);
        }, cancellationToken);

    public Task<string> NextAsync(CancellationToken cancellationToken = default) => SerializedAsync(async () =>
    {
        if ((await store.ReadAsync(serverId, cancellationToken)).Active is null)
            throw new InvalidOperationException("Apply a rotation before advancing to the next map.");
        return await rcon.CommandAsync("vstr nextmap", cancellationToken);
    }, cancellationToken);
    public Task<string> RestartAsync(CancellationToken cancellationToken = default) =>
        SerializedAsync(() => rcon.CommandAsync("map_restart 0", cancellationToken), cancellationToken);
    public Task<string> ChangeMapAsync(string map, CancellationToken cancellationToken = default) => SerializedAsync(async () =>
    {
        if (!QuakeNames.IsMap(map)) throw new ArgumentException("Invalid map name.");
        var maps = await CatalogueAsync(false, cancellationToken);
        maps.RequireFresh();
        if (!maps.Maps.Contains(map, StringComparer.Ordinal)) throw new ArgumentException("Choose an installed map.");
        return await rcon.CommandAsync("map " + map, cancellationToken);
    }, cancellationToken);

    private Task<QuakeMapCatalogue> CatalogueAsync(bool refresh, CancellationToken token) =>
        catalogue.GetAsync(serverId, options.ContentRoots, files, refresh, token);
    private async Task<T> SerializedAsync<T>(Func<Task<T>> operation, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { token.ThrowIfCancellationRequested(); return await operation(); }
        finally { gate.Release(); }
    }
}
