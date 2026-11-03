using System.Collections.Immutable;
using LANCommander.Community.Server.Quake3.Core;
using Xunit;

namespace LANCommander.Community.Server.Quake3.Tests;

public sealed class StoreTests
{
    [Fact]
    public async Task AtomicStoreRestoresImmutableStateAndKeepsServersIsolated()
    {
        using var directory = new TestDirectory();
        var id = Guid.NewGuid();
        var store = new QuakeStateStore(directory.Path);
        await store.UpdateAsync(id, state => state with
        {
            Active = QuakeConfiguration.Default,
            Presets = state.Presets.Add("Friday", QuakeConfiguration.Default),
            Catalogue = new(["Server/baseq3"], "signature", ["q3dm17"], DateTimeOffset.UtcNow)
        });
        var restored = await new QuakeStateStore(directory.Path).ReadAsync(id);
        Assert.Equal<string>(["q3dm17"], restored.Active!.Rotation);
        Assert.Single(restored.Presets);
        Assert.NotNull(restored.Catalogue);
        Assert.Null((await store.ReadAsync(Guid.NewGuid())).Active);
        Assert.Empty(System.IO.Directory.EnumerateFiles(directory.Path, "*.pending"));
    }

    [Fact]
    public async Task CorruptionAndOversizedStateFailExplicitly()
    {
        using var directory = new TestDirectory();
        var id = Guid.NewGuid();
        var path = System.IO.Path.Combine(directory.Path, id.ToString("N") + ".json");
        var store = new QuakeStateStore(directory.Path);
        foreach (var json in new[] { "map q3dm17\n", "null", "{}", "{\"Active\":null,\"Presets\":null,\"Catalogue\":null}",
            "{\"Active\":null,\"Presets\":{},\"Catalogue\":null,\"Unknown\":true}" })
        {
            await File.WriteAllTextAsync(path, json);
            await Assert.ThrowsAsync<QuakePersistenceException>(() => store.ReadAsync(id));
        }
        await File.WriteAllBytesAsync(path, new byte[QuakeStateStore.MaximumStateBytes + 1]);
        await Assert.ThrowsAsync<QuakePersistenceException>(() => store.ReadAsync(id));
    }

    [Fact]
    public async Task ConcurrentStoreInstancesDoNotLoseUpdatesAndCancelledSavePreservesState()
    {
        using var directory = new TestDirectory();
        var id = Guid.NewGuid();
        var first = new QuakeStateStore(directory.Path);
        var second = new QuakeStateStore(directory.Path);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            (i % 2 == 0 ? first : second).UpdateAsync(id, state =>
                state with { Presets = state.Presets.Add("preset" + i, QuakeConfiguration.Default) })));
        Assert.Equal(20, (await first.ReadAsync(id)).Presets.Count);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            first.UpdateAsync(id, state => state with { Active = QuakeConfiguration.Default }, cancelled.Token));
        Assert.Null((await first.ReadAsync(id)).Active);
    }

    [Fact]
    public async Task RealFilesystemWriteFailureIsExplicitAndLeavesNoPendingFiles()
    {
        using var directory = new TestDirectory();
        var fileRoot = System.IO.Path.Combine(directory.Path, "not-a-directory");
        await File.WriteAllTextAsync(fileRoot, "occupied");
        var store = new QuakeStateStore(fileRoot);
        var error = await Assert.ThrowsAsync<QuakePersistenceException>(() =>
            store.UpdateAsync(Guid.NewGuid(), state => state with { Active = QuakeConfiguration.Default }));
        Assert.NotNull(error.InnerException);
        Assert.Equal("occupied", await File.ReadAllTextAsync(fileRoot));
        Assert.Empty(System.IO.Directory.EnumerateFiles(directory.Path, "*.pending", SearchOption.AllDirectories));
    }
}
