using LANCommander.Community.Launcher.MQTT.Core;
using Xunit;

namespace LANCommander.Community.Launcher.MQTT.Core.Tests;

public sealed class GameSelectionTests
{
    [Fact]
    public async Task Selection_has_no_side_effect_and_install_never_implicitly_launches()
    {
        var host = new CatalogHost();
        var coordinator = new LaunchCoordinator(host, []);
        Assert.Equal(LaunchDisposition.Accepted, (await coordinator.ProcessAsync(Command("select", host.Game.Id))).Disposition);
        Assert.Equal(0, host.InstallCount + host.LaunchCount);
        Assert.Equal("game is not installed", (await coordinator.ProcessAsync(Command("launch_selected"))).Message);
        var install = Command("install_selected");
        var accepted = await coordinator.ProcessAsync(install);
        Assert.Equal(LaunchDisposition.Accepted, accepted.Disposition);
        Assert.Equal(host.Game.Id, accepted.GameId);
        Assert.Equal(1, host.InstallCount);
        Assert.Equal(0, host.LaunchCount);
        var duplicate = await coordinator.ProcessAsync(install);
        Assert.Equal(LaunchDisposition.Duplicate, duplicate.Disposition);
        Assert.Equal(host.Game.Id, duplicate.GameId);
        Assert.Equal("launcher is busy", (await coordinator.ProcessAsync(Command("install_selected"))).Message);
        Assert.Equal(0, host.LaunchCount);
        host.Game = host.Game with { IsInstalled = true, InstallStatus = "installed" };
        host.Completion.SetResult();
        await WaitForAsync(() => coordinator.CurrentState.Status == "installed");
        Assert.Equal(0, host.LaunchCount);
        Assert.Equal("game is already installed", (await coordinator.ProcessAsync(Command("install_selected"))).Message);
        Assert.Equal(LaunchDisposition.Accepted, (await coordinator.ProcessAsync(Command("launch_selected"))).Disposition);
        Assert.Equal(1, host.LaunchCount);
    }

    [Fact]
    public async Task Selection_and_explicit_targets_revalidate_live_exposure()
    {
        var host = new CatalogHost();
        var coordinator = new LaunchCoordinator(host, []);
        await coordinator.ProcessAsync(Command("select", host.Game.Id));
        host.Exposed = false;
        Assert.Equal("game is not allowlisted", (await coordinator.ProcessAsync(Command("install_selected"))).Message);
        Assert.Null(coordinator.SelectedGameId);
        Assert.Equal("game is not allowlisted", (await coordinator.ProcessAsync(Command("install", host.Game.Id))).Message);
        Assert.Equal(0, host.InstallCount);
        host.Exposed = true;
        Assert.Equal("game is not allowlisted", (await coordinator.ProcessAsync(Command("install_selected"))).Message);
    }

    [Fact]
    public async Task Failed_install_has_explicit_outcome_releases_busy_and_does_not_launch()
    {
        var host = new CatalogHost();
        var coordinator = new LaunchCoordinator(host, []);
        await coordinator.ProcessAsync(Command("install", host.Game.Id));
        host.Completion.SetException(new RemoteOperationException("install_queue_blocked"));
        await WaitForAsync(() => coordinator.CurrentState.Status == "failed");
        Assert.False(coordinator.IsBusy);
        Assert.Equal("install_queue_blocked", coordinator.CurrentState.ErrorType);
        Assert.Equal(0, host.LaunchCount);
    }

    [Fact]
    public async Task Applying_configuration_requires_idle_and_preserves_deduplication()
    {
        var host = new CatalogHost();
        var coordinator = new LaunchCoordinator(host, []);
        var selected = Command("select", host.Game.Id);
        await coordinator.ProcessAsync(selected);
        await coordinator.PauseForConfigurationAsync(CancellationToken.None);
        Assert.Equal("settings are being applied", (await coordinator.ProcessAsync(Command("install_selected"))).Message);
        coordinator.ResumeAfterConfiguration();
        Assert.Equal(LaunchDisposition.Duplicate, (await coordinator.ProcessAsync(selected)).Disposition);
        await coordinator.ProcessAsync(Command("install_selected"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.PauseForConfigurationAsync(CancellationToken.None));
        host.Completion.SetResult();
    }

    [Fact]
    public async Task Full_dedup_window_rejects_new_ids_instead_of_evicting_unexpired_requests()
    {
        var host = new CatalogHost();
        var coordinator = new LaunchCoordinator(host, []);
        var first = Command("select", host.Game.Id);
        await coordinator.ProcessAsync(first);
        for (var index = 1; index < 1024; index++)
            await coordinator.ProcessAsync(Command("select", host.Game.Id));
        Assert.Equal(LaunchDisposition.Rejected, (await coordinator.ProcessAsync(Command("install", host.Game.Id))).Disposition);
        Assert.Equal(LaunchDisposition.Duplicate, (await coordinator.ProcessAsync(first)).Disposition);
        Assert.Equal(0, host.InstallCount);
    }

    [Fact]
    public void Duplicate_titles_and_long_titles_remain_unique_bounded_uuid_options()
    {
        var first = new CatalogGame(Guid.NewGuid(), new string('x', 500), false);
        var second = first with { Id = Guid.NewGuid() };
        Assert.NotEqual(first.Option, second.Option);
        Assert.True(first.Option.Length <= 255);
        Assert.EndsWith($"[{first.Id:D}]", first.Option);
    }

    private static LaunchCommand Command(string action, Guid gameId = default)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), action, gameId, now, now.AddSeconds(30));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class CatalogHost : IGameCatalogHost
    {
        public CatalogGame Game = new(Guid.NewGuid(), "Example", false);
        public bool Exposed = true;
        public int InstallCount;
        public int LaunchCount;
        public TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> IsReadyAsync(CancellationToken token) => Task.FromResult(true);
        public Task<IReadOnlyList<CatalogGame>> GetCatalogAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<CatalogGame>>(Exposed ? [Game] : []);
        public Task<GameAvailability> GetGameAvailabilityAsync(Guid id, CancellationToken token) =>
            Task.FromResult(new GameAvailability(Game.IsInstalled, Game.IsRunning));
        public Task<IGameLaunchSession> StartInstallAsync(Guid id, CancellationToken token)
        {
            InstallCount++;
            return Task.FromResult<IGameLaunchSession>(new Session(Completion.Task));
        }
        public Task<IGameLaunchSession> StartGameAsync(Guid id, CancellationToken token)
        {
            LaunchCount++;
            return Task.FromResult<IGameLaunchSession>(new Session(Task.CompletedTask));
        }
    }

    private sealed class Session(Task completion) : IGameLaunchSession
    {
        public Task<bool> Started => Task.FromResult(true);
        public Task Completion => completion;
    }
}
