using System.Collections.Immutable;
using LANCommander.Community.Server.Quake3.Core;
using Xunit;

namespace LANCommander.Community.Server.Quake3.Tests;

public sealed class ControlTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialStartupWritePersistsUncertaintyAndNeverExecutes(bool cancelled)
    {
        using var fixture = new ControlFixture();
        fixture.Files.PartialWrite = true;
        fixture.Files.CancelPartialWrite = cancelled;
        var result = await fixture.Control.ApplyAsync(QuakeConfiguration.Default);
        Assert.False(result.Succeeded);
        Assert.False(result.StartupSaved);
        Assert.Equal(cancelled ? QuakeApplyOutcome.StartupWriteCancelled : QuakeApplyOutcome.StartupWriteFailed, result.Outcome);
        Assert.Contains("partial", result.Message);
        Assert.Empty(fixture.Rcon.Commands);
        var restored = await new QuakeStateStore(fixture.Directory.Path).ReadAsync(fixture.ServerId);
        Assert.Null(restored.Active);
        Assert.Equal(QuakeStartupStatus.WriteUncertain, restored.StartupAttempt!.Status);
        Assert.NotEmpty(fixture.Files.Data[fixture.Options.StartupConfigPath]);
        fixture.Files.PartialWrite = false;
        Assert.True((await fixture.Control.ApplyAsync(QuakeConfiguration.Default)).Succeeded);
        Assert.Equal(QuakeStartupStatus.Applied, (await fixture.Control.GetStateAsync()).StartupAttempt!.Status);
    }

    [Fact]
    public async Task ConfirmationFailureAfterWriteDoesNotSendLiveApply()
    {
        using var fixture = new ControlFixture();
        fixture.Store.FailStartupConfirmation = true;
        var result = await fixture.Control.ApplyAsync(QuakeConfiguration.Default);
        Assert.Equal(QuakeApplyOutcome.StartupSavedButStateSaveFailed, result.Outcome);
        Assert.True(result.StartupSaved);
        Assert.False(result.Succeeded);
        Assert.Single(fixture.Files.Writes);
        Assert.Empty(fixture.Rcon.Commands);
        Assert.Equal(QuakeStartupStatus.WriteUncertain, (await fixture.Control.GetStateAsync()).StartupAttempt!.Status);
    }

    [Fact]
    public async Task IntentPersistenceFailurePreventsExternalStartupWrite()
    {
        using var fixture = new ControlFixture();
        await fixture.Control.GetCatalogueAsync();
        fixture.Store.FailAll = true;
        await Assert.ThrowsAsync<QuakePersistenceException>(() => fixture.Control.ApplyAsync(QuakeConfiguration.Default));
        Assert.Empty(fixture.Files.Writes);
        Assert.Empty(fixture.Rcon.Commands);
    }
    [Fact]
    public async Task PresetsAreTrimmedDurableAndDoNotExecuteGameOrWriteStartup()
    {
        using var fixture = new ControlFixture();
        await fixture.Control.SavePresetAsync(" Friday ", QuakeConfiguration.Default);
        Assert.Empty(fixture.Files.Writes);
        Assert.Empty(fixture.Rcon.Commands);
        var state = await new QuakeStateStore(fixture.Directory.Path).ReadAsync(fixture.ServerId);
        Assert.Null(state.Active);
        Assert.Equal<string>(QuakeConfiguration.Default.Rotation, state.Presets["Friday"].Rotation);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Control.SavePresetAsync(" ", QuakeConfiguration.Default));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Control.SavePresetAsync(new string('a', 49), QuakeConfiguration.Default));
        for (var i = 0; i < 31; i++) await fixture.Control.SavePresetAsync("preset" + i, QuakeConfiguration.Default);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Control.SavePresetAsync("too many", QuakeConfiguration.Default));
        await fixture.Control.SavePresetAsync("Friday", QuakeConfiguration.Default);
        Assert.Equal(32, (await fixture.Control.GetStateAsync()).Presets.Count);
        await fixture.Control.DeletePresetAsync("Friday");
        Assert.Equal(31, (await fixture.Control.GetStateAsync()).Presets.Count);
    }

    [Fact]
    public async Task ApplyWritesSeparateStartupPathThenExecThenPersistsActive()
    {
        using var fixture = new ControlFixture();
        fixture.Rcon.Execute = async (command, token) =>
        {
            Assert.Single(fixture.Files.Writes);
            Assert.Null((await fixture.Store.ReadAsync(fixture.ServerId, token)).Active);
            Assert.Equal("exec dadlan-admin/server.cfg", command);
            return "execing";
        };
        var result = await fixture.Control.ApplyAsync(QuakeConfiguration.Default);
        Assert.True(result.Succeeded);
        Assert.True(result.StartupSaved);
        Assert.Equal(fixture.Options.StartupConfigPath, fixture.Files.Writes[0].Path);
        Assert.Equal<string>(QuakeConfiguration.Default.Rotation, (await fixture.Control.GetStateAsync()).Active!.Rotation);
    }

    [Fact]
    public async Task FailedLiveApplyPreservesPreviousActiveAndExplicitStartupSaved()
    {
        using var fixture = new ControlFixture();
        await fixture.Control.ApplyAsync(QuakeConfiguration.Default);
        var ctf = new QuakeConfiguration(QuakeGameMode.CaptureTheFlag, 20, 0, 10, ["q3ctf1"]);
        fixture.Rcon.Execute = (_, _) => throw new RconException("secret echoed");
        var result = await fixture.Control.ApplyAsync(ctf);
        Assert.Equal(QuakeApplyOutcome.StartupSavedButLiveApplyFailed, result.Outcome);
        Assert.Contains("Startup configuration saved, but live apply failed", result.Message);
        Assert.DoesNotContain("secret", result.Message);
        Assert.Equal(QuakeGameMode.FreeForAll, (await fixture.Control.GetStateAsync()).Active!.Mode);
        var restored = await new QuakeStateStore(fixture.Directory.Path).ReadAsync(fixture.ServerId);
        Assert.Equal(QuakeGameMode.CaptureTheFlag, restored.StartupAttempt!.Configuration.Mode);
        Assert.Equal(QuakeStartupStatus.SavedLiveUncertain, restored.StartupAttempt.Status);
        Assert.Contains("q3ctf1", System.Text.Encoding.UTF8.GetString(fixture.Files.Writes[^1].Content));
    }

    [Fact]
    public async Task CancellationAfterStartupSaveIsExplicitAndPreservesActive()
    {
        using var fixture = new ControlFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Files.AfterWrite = cancellation.Cancel;
        var result = await fixture.Control.ApplyAsync(QuakeConfiguration.Default, cancellation.Token);
        Assert.Equal(QuakeApplyOutcome.StartupSavedButLiveApplyCancelled, result.Outcome);
        Assert.True(result.StartupSaved);
        Assert.Empty(fixture.Rcon.Commands);
        Assert.Null((await fixture.Control.GetStateAsync()).Active);
    }

    [Fact]
    public async Task StartupDiskFailureDoesNotExecuteOrChangeActive()
    {
        using var fixture = new ControlFixture();
        fixture.Files.FailWrite = true;
        var result = await fixture.Control.ApplyAsync(QuakeConfiguration.Default);
        Assert.Equal(QuakeApplyOutcome.StartupWriteFailed, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Empty(fixture.Rcon.Commands);
        Assert.Null((await fixture.Control.GetStateAsync()).Active);
    }

    [Fact]
    public async Task ActiveDiskFailureAfterLiveAckReportsPartialOutcome()
    {
        using var fixture = new ControlFixture();
        fixture.Store.FailActive = true;
        var result = await fixture.Control.ApplyAsync(QuakeConfiguration.Default);
        Assert.Equal(QuakeApplyOutcome.StartupSavedAndLiveAppliedButStateSaveFailed, result.Outcome);
        Assert.Single(fixture.Rcon.Commands);
        Assert.Null((await fixture.Control.GetStateAsync()).Active);
    }

    [Fact]
    public async Task PresetPersistenceFailureIsNotSuccess()
    {
        using var fixture = new ControlFixture();
        fixture.Store.FailAll = true;
        await Assert.ThrowsAsync<QuakePersistenceException>(() => fixture.Control.SavePresetAsync("Friday", QuakeConfiguration.Default));
        Assert.Empty((await fixture.Control.GetStateAsync()).Presets);
    }

    [Fact]
    public async Task PresetsRejectMissingMapsAndUnavailableOrStaleCatalogue()
    {
        using var fixture = new ControlFixture();
        var missing = new QuakeConfiguration(QuakeGameMode.FreeForAll, 15, 30, 8, ["missing"]);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Control.SavePresetAsync("Missing", missing));
        fixture.Files.Unavailable = true;
        await Assert.ThrowsAsync<QuakeCatalogueException>(() => fixture.Control.SavePresetAsync("Stale", QuakeConfiguration.Default));
        var other = fixture.Factory.Create(Guid.NewGuid(), fixture.Options, fixture.Files, fixture.Rcon);
        await Assert.ThrowsAsync<QuakeCatalogueException>(() => other.SavePresetAsync("Unavailable", QuakeConfiguration.Default));
        Assert.Empty((await fixture.Control.GetStateAsync()).Presets);
        Assert.Empty(fixture.Files.Writes);
        Assert.Empty(fixture.Rcon.Commands);
    }

    [Fact]
    public async Task ApplyAndMapRejectStaleAndUnavailableCatalogues()
    {
        using var fixture = new ControlFixture();
        await fixture.Control.GetCatalogueAsync();
        fixture.Files.Unavailable = true;
        await Assert.ThrowsAsync<QuakeCatalogueException>(() => fixture.Control.ApplyAsync(QuakeConfiguration.Default));
        await Assert.ThrowsAsync<QuakeCatalogueException>(() => fixture.Control.ChangeMapAsync("q3dm17"));
        Assert.Empty(fixture.Files.Writes);
        Assert.Empty(fixture.Rcon.Commands);
    }

    [Fact]
    public async Task ActionsAreFixedAndNeverWriteStartupOrPresets()
    {
        using var fixture = new ControlFixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Control.NextAsync());
        await fixture.Control.ApplyAsync(QuakeConfiguration.Default);
        var writes = fixture.Files.Writes.Count;
        var before = await fixture.Control.GetStateAsync();
        await fixture.Control.StatusAsync();
        await fixture.Control.NextAsync();
        await fixture.Control.RestartAsync();
        await fixture.Control.ChangeMapAsync("q3ctf1");
        Assert.Equal(["exec dadlan-admin/server.cfg", "status", "vstr nextmap", "map_restart 0", "map q3ctf1"], fixture.Rcon.Commands);
        Assert.Equal(writes, fixture.Files.Writes.Count);
        Assert.Equal<string>(before.Active!.Rotation, (await fixture.Control.GetStateAsync()).Active!.Rotation);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Control.ChangeMapAsync("q3dm17;quit"));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Control.ChangeMapAsync("missing"));
    }

    [Fact]
    public async Task FactorySerializesAcrossScopedControllersAndCancelsWaitingCall()
    {
        using var fixture = new ControlFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Rcon.Execute = async (_, token) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return "status";
        };
        var first = fixture.Control.StatusAsync();
        await entered.Task;
        var other = fixture.Factory.Create(fixture.ServerId, fixture.Options, fixture.Files, fixture.Rcon);
        using var cancellation = new CancellationTokenSource();
        var waiting = other.RestartAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Single(fixture.Rcon.Commands);
        release.SetResult();
        await first;
        fixture.Rcon.Execute = null;
        await other.RestartAsync();
        Assert.Equal(2, fixture.Rcon.Commands.Count);
    }
}
