using Avalonia.Threading;
using LANCommander.Launcher.Data;
using LANCommander.Launcher.Data.Models;
using LANCommander.Launcher.Services;
using LANCommander.SDK.Abstractions;
using LANCommander.SDK.Enums;
using LANCommander.SDK.Helpers;
using LANCommander.SDK.Services;
using LANCommander.Community.Launcher.MQTT.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LANCommander.Community.Launcher.MQTT;

public sealed class LANCommanderGameLaunchHost(
    IServiceProvider root,
    Func<HomeAssistantPluginSettings> settings) : IGameCatalogHost
{
    private readonly IServiceScopeFactory _scopeFactory = root.GetRequiredService<IServiceScopeFactory>();

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(root.GetRequiredService<AuthenticationService>().GetUserId() != Guid.Empty);
    }

    public async Task<IReadOnlyList<CatalogGame>> GetLibraryAsync(CancellationToken cancellationToken)
    {
        var userId = root.GetRequiredService<AuthenticationService>().GetUserId();
        if (userId == Guid.Empty)
            return [];
        await using var scope = _scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        var games = await database.Set<Game>().AsNoTracking()
            .Where(game => game.ShowInLibrary && game.Libraries.Any(library => library.UserId == userId))
            .Select(game => new { game.Id, game.Title, game.Installed, game.InstallDirectory })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var client = root.GetRequiredService<GameClient>();
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (root.GetRequiredService<AuthenticationService>().GetUserId() != userId)
                return (IReadOnlyList<CatalogGame>)[];
            var queue = root.GetRequiredService<InstallService>().Queue;
            return (IReadOnlyList<CatalogGame>)games.Select(game =>
            {
                var installed = game.Installed && !string.IsNullOrEmpty(game.InstallDirectory)
                    && ManifestHelper.Exists(game.InstallDirectory, game.Id);
                var item = queue.LastOrDefault(item => item.Id == game.Id);
                var progress = item is { TotalBytes: > 0 }
                    ? Math.Clamp(100d * item.BytesDownloaded / item.TotalBytes, 0, 100) : (double?)null;
                return new CatalogGame(game.Id, game.Title, installed, client.IsRunning(game.Id),
                    item?.Status.ToString().ToLowerInvariant() ?? (installed ? "installed" : "not_installed"), progress);
            }).OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase).ThenBy(game => game.Id).ToArray();
        });
    }

    public async Task<IReadOnlyList<CatalogGame>> GetCatalogAsync(CancellationToken cancellationToken)
    {
        var library = await GetLibraryAsync(cancellationToken).ConfigureAwait(false);
        var current = settings();
        var selected = current.Favorites.Select(game => game.Id).ToHashSet();
        return library.Where(game => current.ExposureMode == "all" || selected.Contains(game.Id)).ToArray();
    }

    public async Task<GameAvailability> GetGameAvailabilityAsync(Guid gameId, CancellationToken cancellationToken)
    {
        var game = (await GetCatalogAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault(game => game.Id == gameId)
            ?? throw new InvalidOperationException("The selected game is no longer exposed in the signed-in user's library.");
        return new GameAvailability(game.IsInstalled, game.IsRunning);
    }

    public async Task<IGameLaunchSession> StartGameAsync(Guid gameId, CancellationToken cancellationToken)
    {
        var scope = _scopeFactory.CreateAsyncScope();
        try
        {
            var availability = await GetGameAvailabilityAsync(gameId, cancellationToken).ConfigureAwait(false);
            if (!availability.IsInstalled || availability.IsRunning)
                throw new InvalidOperationException("The game must be installed and not running.");
            var service = scope.ServiceProvider.GetRequiredService<GameService>();
            var game = await service.GetAsync(gameId).ConfigureAwait(false);
            var client = root.GetRequiredService<GameClient>();
            var actions = (await client.GetActionsAsync(game.InstallDirectory, gameId).ConfigureAwait(false))
                .Where(action => action.IsPrimaryAction).ToArray();
            if (actions.Length != 1)
                throw new InvalidOperationException("The installed game must have exactly one primary launch action.");
            Task? completion = null;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                EnsureNoQueueOrGameOperation();
                completion = service.Run(game, actions[0]);
            });
            return new LaunchSession(client, gameId, completion!, scope);
        }
        catch
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IGameLaunchSession> StartInstallAsync(Guid gameId, CancellationToken cancellationToken)
    {
        var availability = await GetGameAvailabilityAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (availability.IsInstalled || availability.IsRunning)
            throw new InvalidOperationException("The game is already installed or running.");
        if (!root.GetRequiredService<ConnectionClient>().IsConnected())
            throw new RemoteOperationException("server_not_connected");
        var directories = root.GetRequiredService<ISettingsProvider>().CurrentValue.Games.InstallDirectories ?? [];
        var destination = RequireInstallDestination(directories);
        Task? completion = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            EnsureNoQueueOrGameOperation();
            completion = InstallAndObserveAsync(gameId, destination);
        });
        return new InstallSession(completion!);
    }

    public static string RequireInstallDestination(IEnumerable<string> directories)
    {
        var configured = directories.Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (configured.Length != 1 || !Path.IsPathFullyQualified(configured[0]))
            throw new RemoteOperationException("install_destination_not_configured: configure exactly one absolute game install directory in Launcher settings");
        return configured[0];
    }

    private void EnsureNoQueueOrGameOperation()
    {
        var queue = root.GetRequiredService<InstallService>().Queue;
        if (queue.Any(item => item.Status is not (InstallStatus.Complete or InstallStatus.Canceled or InstallStatus.Failed)))
            throw new RemoteOperationException("launcher_install_queue_busy");
        // The singleton tracker is shared with local launches; never start over a local running game.
        var client = root.GetRequiredService<GameClient>();
        if (root.GetRequiredService<LibraryService>().Items.Any(item => client.IsRunning(item.Key)))
            throw new RemoteOperationException("launcher_game_running");
    }

    private async Task InstallAndObserveAsync(Guid gameId, string destination)
    {
        var install = root.GetRequiredService<InstallService>();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        var userId = root.GetRequiredService<AuthenticationService>().GetUserId();
        var game = await database.Set<Game>().AsNoTracking().SingleOrDefaultAsync(game =>
            game.Id == gameId && game.ShowInLibrary && game.Libraries.Any(library => library.UserId == userId));
        if (game is null)
            throw new RemoteOperationException("game_not_in_local_library");
        if (game.Installed || !string.IsNullOrEmpty(game.InstallDirectory) && ManifestHelper.Exists(game.InstallDirectory, gameId))
            throw new RemoteOperationException("game_already_installed_or_existing_manifest");
        var plan = await root.GetRequiredService<GameClient>().GenerateInstallPlanAsync(gameId, destination);
        var plannedIds = plan.Items.Select(item => item.EntityId).ToArray();
        var existing = await database.Set<Game>().AsNoTracking()
            .Where(item => plannedIds.Contains(item.Id))
            .Select(item => new { item.Id, item.Installed, item.InstallDirectory }).ToListAsync();
        var installedIds = existing.Where(item => item.Installed || !string.IsNullOrEmpty(item.InstallDirectory)
                && ManifestHelper.Exists(item.InstallDirectory, item.Id)).Select(item => item.Id).ToHashSet();
        RequireFreshInstallPlan(gameId, plan, installedIds);
        var current = await GetGameAvailabilityAsync(gameId, CancellationToken.None);
        if (current.IsInstalled || current.IsRunning)
            throw new RemoteOperationException("game_became_installed_or_running");
        EnsureNoQueueOrGameOperation();
        var previous = install.Queue.ToHashSet();
        await install.Add(game, destination);
        var item = install.Queue.LastOrDefault(item => item.Id == gameId);
        if (item is null)
            throw new RemoteOperationException("install_was_not_queued");
        while (true)
        {
            var group = install.Queue.Where(candidate => !previous.Contains(candidate)).ToArray();
            if (group.Any(candidate => candidate.Status is InstallStatus.Failed or InstallStatus.Canceled))
                throw new RemoteOperationException("install_or_dependency_failed_or_canceled");
            if (item.Status == InstallStatus.Complete
                && group.All(candidate => candidate.Status == InstallStatus.Complete))
                break;
            if (!group.Any(candidate => candidate.State))
                throw new RemoteOperationException("install_queue_blocked: inspect dependencies in Launcher");
            await Task.Delay(500);
        }
        // Add can swallow upstream exceptions; verify the persisted installed record and manifest.
        var catalog = await GetLibraryAsync(CancellationToken.None);
        if (!catalog.Any(game => game.Id == gameId && game.IsInstalled))
            throw new RemoteOperationException("install_not_confirmed");
    }

    public static void RequireFreshInstallPlan(Guid requestedGameId, LANCommander.SDK.Models.InstallPlan plan,
        IReadOnlySet<Guid> installedGameIds)
    {
        var games = plan.Items.Where(item => item.Type is InstallPlanItemType.Game or InstallPlanItemType.Addon).ToArray();
        if (!games.Any(item => item.EntityId == requestedGameId))
            throw new RemoteOperationException("install_plan_missing_requested_game");
        // Add regenerates its plan. Reject installed dependencies too: alpha.4's
        // installed-game branch can modify addons/tools rather than just enqueue.
        if (games.Any(item => installedGameIds.Contains(item.EntityId)
                || !string.IsNullOrEmpty(item.InstallDirectory) && ManifestHelper.Exists(item.InstallDirectory, item.EntityId)))
            throw new RemoteOperationException("install_plan_would_modify_installed_game: install this game locally");
    }

    private sealed class InstallSession(Task completion) : IGameLaunchSession
    {
        public Task<bool> Started => Task.FromResult(true);
        public Task Completion => completion;
    }

    private sealed class LaunchSession(GameClient client, Guid id, Task runTask, AsyncServiceScope scope) : IGameLaunchSession
    {
        private readonly Task _completion = CompleteAsync(runTask, scope);
        public Task<bool> Started => ObserveAsync();
        public Task Completion => _completion;
        private async Task<bool> ObserveAsync()
        {
            while (!runTask.IsCompleted)
            {
                var running = await Dispatcher.UIThread.InvokeAsync(() => client.IsRunning(id));
                if (running) return true;
                await Task.Delay(200).ConfigureAwait(false);
            }
            return false;
        }
        private static async Task CompleteAsync(Task task, AsyncServiceScope ownedScope)
        {
            try { await task.ConfigureAwait(false); }
            finally { await ownedScope.DisposeAsync().ConfigureAwait(false); }
        }
    }
}
