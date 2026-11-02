using LanCommander.HomeAssistant.Core;
using Xunit;

namespace LanCommander.HomeAssistant.Core.Tests;

public sealed class LaunchCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 4, 33, 0, TimeSpan.Zero);

    [Fact]
    public async Task Launch_requires_allowlist_readiness_and_installation()
    {
        var allowedGameId = Guid.NewGuid();
        var host = new FakeHost { IsReady = false };
        var coordinator = new LaunchCoordinator(host, [allowedGameId], new FixedTimeProvider(Now));

        var notReady = await coordinator.ProcessAsync(Command(allowedGameId));
        Assert.Equal("launcher is not ready", notReady.Message);

        host.IsReady = true;
        host.Availability = new GameAvailability(false, false);
        var notInstalled = await coordinator.ProcessAsync(Command(allowedGameId));
        Assert.Equal("game is not installed", notInstalled.Message);

        host.Availability = new GameAvailability(true, false);
        var notAllowed = await coordinator.ProcessAsync(Command(Guid.NewGuid()));
        Assert.Equal("game is not allowlisted", notAllowed.Message);
        Assert.Equal(0, host.StartCount);
    }

    [Fact]
    public async Task Rejected_request_does_not_replace_active_launch_state()
    {
        var gameId = Guid.NewGuid();
        var host = new FakeHost();
        var coordinator = new LaunchCoordinator(host, [gameId], new FixedTimeProvider(Now));

        await coordinator.ProcessAsync(Command(gameId));
        var expired = Command(gameId) with
        {
            IssuedAt = Now - TimeSpan.FromSeconds(30),
            ExpiresAt = Now
        };
        var rejected = await coordinator.ProcessAsync(expired);

        Assert.Equal(LaunchDisposition.Rejected, rejected.Disposition);
        Assert.Equal("starting", coordinator.CurrentState.Status);
    }

    [Fact]
    public async Task Duplicate_request_is_not_started_twice()
    {
        var gameId = Guid.NewGuid();
        var host = new FakeHost();
        var coordinator = new LaunchCoordinator(host, [gameId], new FixedTimeProvider(Now));
        var command = Command(gameId);

        var first = await coordinator.ProcessAsync(command);
        var duplicate = await coordinator.ProcessAsync(command);

        Assert.Equal(LaunchDisposition.Accepted, first.Disposition);
        Assert.Equal(LaunchDisposition.Duplicate, duplicate.Disposition);
        Assert.Equal(1, host.StartCount);
        host.Session.MarkStarted();
        await WaitForStatusAsync(coordinator, "running");
        Assert.Equal("running", coordinator.CurrentState.Status);
    }

    [Fact]
    public async Task Reused_request_id_with_different_data_is_rejected()
    {
        var firstGameId = Guid.NewGuid();
        var secondGameId = Guid.NewGuid();
        var host = new FakeHost();
        var coordinator = new LaunchCoordinator(host, [firstGameId, secondGameId], new FixedTimeProvider(Now));
        var original = Command(firstGameId);

        await coordinator.ProcessAsync(original);
        var changed = await coordinator.ProcessAsync(original with { GameId = secondGameId });

        Assert.Equal(LaunchDisposition.Rejected, changed.Disposition);
        Assert.Equal("requestId was reused with different command data", changed.Message);
        Assert.Equal(1, host.StartCount);
    }

    [Fact]
    public async Task Concurrent_requests_start_only_one_game_at_a_time()
    {
        var firstGameId = Guid.NewGuid();
        var secondGameId = Guid.NewGuid();
        var host = new FakeHost();
        var coordinator = new LaunchCoordinator(host, [firstGameId, secondGameId], new FixedTimeProvider(Now));

        var results = await Task.WhenAll(
            coordinator.ProcessAsync(Command(firstGameId)),
            coordinator.ProcessAsync(Command(secondGameId)));

        Assert.Equal(1, results.Count(result => result.Disposition == LaunchDisposition.Accepted));
        Assert.Equal(1, results.Count(result => result.Message == "launcher is busy"));
        Assert.Equal(1, host.StartCount);
    }

    [Fact]
    public async Task Running_game_is_rejected_and_completion_releases_busy_gate()
    {
        var gameId = Guid.NewGuid();
        var host = new FakeHost { Availability = new GameAvailability(true, true) };
        var coordinator = new LaunchCoordinator(host, [gameId], new FixedTimeProvider(Now));

        var alreadyRunning = await coordinator.ProcessAsync(Command(gameId));
        Assert.Equal("game is already running", alreadyRunning.Message);

        host.Availability = new GameAvailability(true, false);
        var started = await coordinator.ProcessAsync(Command(gameId));
        Assert.Equal(LaunchDisposition.Accepted, started.Disposition);
        host.Session.MarkStarted();
        await WaitForStatusAsync(coordinator, "running");
        host.Session.Complete();
        await WaitForStatusAsync(coordinator, "completed");

        Assert.Equal("completed", coordinator.CurrentState.Status);
    }

    private static LaunchCommand Command(Guid gameId) => new(
        Guid.NewGuid(),
        "launch",
        gameId,
        Now,
        Now + TimeSpan.FromSeconds(30));

    private static async Task WaitForStatusAsync(LaunchCoordinator coordinator, string status)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await foreach (var state in coordinator.ReadStateChangesAsync(timeout.Token))
        {
            if (state.Status == status)
                return;
        }

        throw new TimeoutException($"Launch state did not become {status}");
    }

    private sealed class FakeHost : IGameLaunchHost
    {
        public bool IsReady { get; set; } = true;
        public GameAvailability Availability { get; set; } = new(true, false);
        public FakeSession Session { get; } = new();
        public int StartCount { get; private set; }

        public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => Task.FromResult(IsReady);

        public Task<GameAvailability> GetGameAvailabilityAsync(Guid gameId, CancellationToken cancellationToken) =>
            Task.FromResult(Availability);

        public Task<IGameLaunchSession> StartGameAsync(Guid gameId, CancellationToken cancellationToken)
        {
            StartCount++;
            return Task.FromResult<IGameLaunchSession>(Session);
        }
    }

    private sealed class FakeSession : IGameLaunchSession
    {
        private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> Started => _started.Task;
        public Task Completion => _completion.Task;
        public void MarkStarted() => _started.TrySetResult(true);
        public void Complete() => _completion.TrySetResult();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
