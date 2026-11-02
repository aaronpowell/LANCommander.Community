using System.Threading.Channels;

namespace LanCommander.HomeAssistant.Core;

public enum LaunchDisposition
{
    Accepted,
    Rejected,
    Duplicate
}

public sealed record LaunchResult(Guid RequestId, Guid GameId, LaunchDisposition Disposition, string Message);

public sealed record LaunchState(
    string Status,
    Guid? RequestId,
    Guid? GameId,
    DateTimeOffset UpdatedAt,
    string? ErrorType = null);

public sealed class LaunchCoordinator
{
    private const int RecentRequestCapacity = 1024;

    private readonly IGameLaunchHost _host;
    private readonly HashSet<Guid> _allowedGameIds;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, CachedRequest> _recentResults = [];
    private readonly Channel<LaunchState> _stateChanges = Channel.CreateBounded<LaunchState>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    private LaunchState _currentState;
    private Guid? _activeRequestId;
    private Guid? _activeGameId;
    private CatalogGame? _selection;
    private bool _paused;

    public LaunchCoordinator(
        IGameLaunchHost host,
        IEnumerable<Guid> allowedGameIds,
        TimeProvider? timeProvider = null)
    {
        _host = host;
        _allowedGameIds = allowedGameIds.ToHashSet();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _currentState = new LaunchState("idle", null, null, _timeProvider.GetUtcNow());
        _stateChanges.Writer.TryWrite(_currentState);
    }

    public LaunchState CurrentState => Volatile.Read(ref _currentState);
    public Guid? SelectedGameId => Volatile.Read(ref _selection)?.Id;
    public bool IsBusy => CurrentState.Status is "starting" or "running" or "installing";

    public async Task<IReadOnlyList<CatalogGame>> GetCatalogAsync(CancellationToken cancellationToken)
    {
        var catalog = _host is IGameCatalogHost host
            ? await host.GetCatalogAsync(cancellationToken).ConfigureAwait(false)
            : _allowedGameIds.Select(id => new CatalogGame(id, id.ToString(), true)).ToArray();
        var selection = Volatile.Read(ref _selection);
        if (selection is not null && !catalog.Any(game => game.Id == selection.Id))
            Interlocked.CompareExchange(ref _selection, null, selection);
        return catalog;
    }

    public async Task PauseForConfigurationAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsBusy)
                throw new InvalidOperationException("Wait for the remote operation to finish before applying settings.");
            _paused = true;
        }
        finally { _gate.Release(); }
    }

    public void ResumeAfterConfiguration() => _paused = false;

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) =>
        _host.IsReadyAsync(cancellationToken);

    public IAsyncEnumerable<LaunchState> ReadStateChangesAsync(CancellationToken cancellationToken = default) =>
        _stateChanges.Reader.ReadAllAsync(cancellationToken);

    public async Task<LaunchResult> ProcessAsync(LaunchCommand command, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var validationError = LaunchCommandValidator.Validate(command, now);
        if (validationError is not null)
            return Reject(command, validationError);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            validationError = LaunchCommandValidator.Validate(command, _timeProvider.GetUtcNow());
            if (validationError is not null)
                return Reject(command, validationError);

            if (_recentResults.TryGetValue(command.RequestId, out var previous))
            {
                if (previous.Command != command)
                    return new LaunchResult(
                        command.RequestId,
                        command.GameId,
                        LaunchDisposition.Rejected,
                        "requestId was reused with different command data");

                return new LaunchResult(
                    command.RequestId,
                    previous.Result.GameId,
                    LaunchDisposition.Duplicate,
                    previous.Result.Message);
            }

            foreach (var expiredId in _recentResults.Where(pair => pair.Value.Command.ExpiresAt <= now)
                         .Select(pair => pair.Key).ToArray())
                _recentResults.Remove(expiredId);
            if (_recentResults.Count >= RecentRequestCapacity)
                return Reject(command, "request deduplication window is full; retry later with a fresh request");

            var duplicate = Remember(command, new LaunchResult(
                command.RequestId,
                command.GameId,
                LaunchDisposition.Rejected,
                "request is being processed"));

            try
            {
                if (_paused)
                    return UpdateResult(duplicate, "settings are being applied");

                if (!await _host.IsReadyAsync(cancellationToken).ConfigureAwait(false))
                    return UpdateResult(duplicate, "launcher is not ready");

                var gameId = command.Action.EndsWith("_selected", StringComparison.Ordinal)
                    ? SelectedGameId ?? Guid.Empty : command.GameId;
                var catalog = await GetCatalogAsync(cancellationToken).ConfigureAwait(false);
                if (!catalog.Any(game => game.Id == gameId))
                    return UpdateResult(duplicate, "game is not allowlisted");
                duplicate = duplicate with { GameId = gameId };
                if (command.Action == "select")
                {
                    Volatile.Write(ref _selection, catalog.Single(game => game.Id == gameId));
                    return UpdateResult(duplicate with { Disposition = LaunchDisposition.Accepted }, "selection changed");
                }
                if (_activeGameId.HasValue)
                    return UpdateResult(duplicate, "launcher is busy");

                var install = command.Action is "install" or "install_selected";
                var availability = await _host.GetGameAvailabilityAsync(gameId, cancellationToken)
                    .ConfigureAwait(false);
                if (!install && !availability.IsInstalled)
                    return UpdateResult(duplicate, "game is not installed");
                if (install && availability.IsInstalled)
                    return UpdateResult(duplicate, "game is already installed");
                if (availability.IsRunning)
                    return UpdateResult(duplicate, "game is already running");
                if (install && _host is not IGameCatalogHost)
                    return UpdateResult(duplicate, "remote installation is unavailable");

                _activeRequestId = command.RequestId;
                _activeGameId = gameId;
                SetState(install ? "installing" : "starting", command.RequestId, gameId);

                var session = install
                    ? await ((IGameCatalogHost)_host).StartInstallAsync(gameId, cancellationToken).ConfigureAwait(false)
                    : await _host.StartGameAsync(gameId, cancellationToken).ConfigureAwait(false);
                var result = UpdateResult(duplicate with
                {
                    Disposition = LaunchDisposition.Accepted,
                    Message = "launch accepted"
                }, install ? "install accepted" : "launch accepted");
                _ = ObserveSessionAsync(command.RequestId, gameId, session, install);
                return result;
            }
            catch (Exception exception)
            {
                if (_activeRequestId == command.RequestId)
                {
                    _activeRequestId = null;
                    _activeGameId = null;
                }

                var message = $"launch failed: {exception.GetType().Name}";
                _recentResults[command.RequestId] = _recentResults[command.RequestId] with
                {
                    Result = duplicate with
                    {
                        Disposition = LaunchDisposition.Rejected,
                        Message = message
                    }
                };
                SetState("failed", command.RequestId, duplicate.GameId,
                    exception is RemoteOperationException ? exception.Message : exception.GetType().Name);
                if (exception is RemoteOperationException)
                    return UpdateResult(duplicate, exception.Message);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private LaunchResult Reject(LaunchCommand command, string reason)
    {
        return new LaunchResult(command.RequestId, command.GameId, LaunchDisposition.Rejected, reason);
    }

    private LaunchResult Remember(LaunchCommand command, LaunchResult result)
    {
        _recentResults.Add(command.RequestId, new CachedRequest(command, result));
        return result;
    }

    private LaunchResult UpdateResult(LaunchResult original, string message)
    {
        var updated = original with { Message = message };
        var cached = _recentResults[original.RequestId];
        _recentResults[original.RequestId] = cached with { Result = updated };
        if (updated.Disposition == LaunchDisposition.Rejected && !_activeGameId.HasValue)
            SetState("rejected", original.RequestId, original.GameId);

        return updated;
    }

    private async Task ObserveSessionAsync(Guid requestId, Guid gameId, IGameLaunchSession session, bool install)
    {
        var status = "failed";
        string? errorType = null;
        try
        {
            var wasObservedRunning = await session.Started.ConfigureAwait(false);
            if (wasObservedRunning)
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_activeRequestId != requestId)
                        return;

                    SetState(install ? "installing" : "running", requestId, gameId);
                }
                finally
                {
                    _gate.Release();
                }
            }

            await session.Completion.ConfigureAwait(false);
            status = install ? "installed" : "completed";
        }
        catch (Exception exception)
        {
            errorType = exception is RemoteOperationException ? exception.Message : exception.GetType().Name;
            try
            {
                await session.Completion.ConfigureAwait(false);
            }
            catch (Exception completionException)
            {
                errorType = completionException is RemoteOperationException ? completionException.Message : completionException.GetType().Name;
            }
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_activeRequestId != requestId)
                return;

            _activeRequestId = null;
            _activeGameId = null;
            SetState(status, requestId, gameId, errorType);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void SetState(string status, Guid? requestId, Guid? gameId, string? errorType = null)
    {
        var state = new LaunchState(status, requestId, gameId, _timeProvider.GetUtcNow(), errorType);
        Volatile.Write(ref _currentState, state);
        _stateChanges.Writer.TryWrite(state);
    }

    private sealed record CachedRequest(LaunchCommand Command, LaunchResult Result);
}
