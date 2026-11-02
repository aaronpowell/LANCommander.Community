using LANCommander.Community.Server.RecompCatalog.Core;
using LANCommander.Community.Server.RecompCatalog.Plugin;
using Microsoft.Extensions.DependencyInjection;

namespace LANCommander.Community.Server.RecompCatalog.Tests;

public class CatalogImportSafetyTests
{
    [Fact]
    public async Task RepeatedImportWritesOnceAndPreservesLocallyEditedMetadata()
    {
        var preparation = Preparation();
        var host = new StubHost(preparation);
        using var gate = new CatalogImportGate();
        var writes = 0;
        var archives = 0;
        var metadata = "Original metadata";

        Task<CatalogImportResult> Import(CancellationToken token)
        {
            writes++;
            archives++;
            metadata = preparation.Metadata!.Title;
            host.State = CatalogEntryStateClassifier.Classify(preparation.Provenance);
            return Task.FromResult(new CatalogImportResult(preparation.Provenance.GameId, 1, preparation.Provenance));
        }

        var first = await gate.RunAsync(preparation, host, Import);
        metadata = "Locally edited title and description";
        var second = await gate.RunAsync(
            preparation with { Metadata = EditableMetadataDraft.Empty("Replacement metadata") },
            host, Import);

        Assert.False(first.AlreadyImported);
        Assert.True(second.AlreadyImported);
        Assert.Equal(first.GameId, second.GameId);
        Assert.Equal(0, second.ImportedCount);
        Assert.Equal(1, writes);
        Assert.Equal(1, archives);
        Assert.Equal("Locally edited title and description", metadata);
        Assert.Equal(2, host.Reads);
    }

    [Theory]
    [InlineData("v2", "game-win.zip", "https://downloads.test/game.zip")]
    [InlineData("v1", "other-win.zip", "https://downloads.test/game.zip")]
    [InlineData("v1", "game-win.zip", "https://downloads.test/replaced.zip")]
    public async Task DifferentReleaseOrAssetIsAnUpdateAndNeverInvokesImporter(
        string tag, string name, string url)
    {
        var original = Preparation();
        var host = new StubHost(original)
        {
            State = CatalogEntryStateClassifier.Classify(original.Provenance),
        };
        var asset = original.Asset with { Name = name, DownloadUrl = new Uri(url) };
        var candidate = original with
        {
            Release = original.Release with { Tag = tag },
            Asset = asset,
        };
        var assessment = CatalogImportSafety.Assess(
            host.State, ReleaseIdentity.Create(candidate.Entry.Identity, candidate.Release, candidate.Asset));

        Assert.Equal(CatalogImportDisposition.UpdateAvailable, assessment.Disposition);
        Assert.False(assessment.CanImport);
        Assert.Contains("overwrite locally edited metadata", assessment.Message);
        using var gate = new CatalogImportGate();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.RunAsync(candidate, host, UnexpectedImport));
        Assert.Equal(assessment.Message, error.Message);
    }

    [Fact]
    public async Task UnknownLegacyReleaseAndFailedOrMissingStateFailClosed()
    {
        var preparation = Preparation();
        using var gate = new CatalogImportGate();
        var host = new StubHost(preparation);
        foreach (var state in new[]
        {
            CatalogEntryStateClassifier.Classify(preparation.Provenance with { Release = null }),
            CatalogEntryStateClassifier.Classify(null, failure: "Database unavailable"),
            new CatalogEntryStateView(CatalogEntryState.ImportedCurrent),
        })
        {
            host.State = state;
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                gate.RunAsync(preparation, host, UnexpectedImport));
        }

        host.ReturnMissingState = true;
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.RunAsync(preparation, host, UnexpectedImport));
        Assert.Contains("no state", missing.Message);
    }

    [Fact]
    public async Task ConcurrentImportsRecheckAfterFirstImportCompletes()
    {
        var preparation = Preparation();
        var host = new StubHost(preparation);
        using var gate = new CatalogImportGate();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;

        async Task<CatalogImportResult> Import(CancellationToken token)
        {
            writes++;
            started.SetResult();
            await finish.Task.WaitAsync(token);
            host.State = CatalogEntryStateClassifier.Classify(preparation.Provenance);
            return new(preparation.Provenance.GameId, 1, preparation.Provenance);
        }

        var first = gate.RunAsync(preparation, host, Import);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = gate.RunAsync(preparation, host, UnexpectedImport);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, host.Reads);
        finish.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, writes);
        Assert.True(results[1].AlreadyImported);
        Assert.Equal(2, host.Reads);
    }

    [Fact]
    public async Task FailedImportReleasesGateAndAllowsRetry()
    {
        var preparation = Preparation();
        var host = new StubHost(preparation);
        using var gate = new CatalogImportGate();
        await Assert.ThrowsAsync<IOException>(() =>
            gate.RunAsync(preparation, host, _ => throw new IOException("Importer failed")));
        var result = await gate.RunAsync(preparation, host, _ =>
            Task.FromResult(new CatalogImportResult(preparation.Provenance.GameId, 1, preparation.Provenance)));

        Assert.False(result.AlreadyImported);
        Assert.Equal(2, host.Reads);
    }

    [Fact]
    public async Task HostReadFailureIsSurfacedAndDoesNotLeaveGateLocked()
    {
        var preparation = Preparation();
        var host = new StubHost(preparation) { ReadFailure = new IOException("Database offline") };
        using var gate = new CatalogImportGate();
        var error = await Assert.ThrowsAsync<IOException>(() =>
            gate.RunAsync(preparation, host, UnexpectedImport));
        Assert.Equal("Database offline", error.Message);

        host.ReadFailure = null;
        host.State = CatalogEntryStateClassifier.Classify(preparation.Provenance);
        var result = await gate.RunAsync(preparation, host, UnexpectedImport)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.AlreadyImported);
    }

    [Fact]
    public async Task CancelledRequestDoesNotReadOrImport()
    {
        var preparation = Preparation();
        var host = new StubHost(preparation);
        using var gate = new CatalogImportGate();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gate.RunAsync(preparation, host, UnexpectedImport, cancellation.Token));
        Assert.Equal(0, host.Reads);
    }

    [Fact]
    public async Task WorkflowSkipsPackageBuildingAndHostImporterForExistingRelease()
    {
        var preparation = Preparation();
        var host = new StubHost(preparation)
        {
            State = CatalogEntryStateClassifier.Classify(preparation.Provenance),
        };
        using var http = new RecompCatalogHttpClient();
        using var gate = new CatalogImportGate();
        var importer = new ThrowingPackageImporter();
        var workflow = new CatalogImportWorkflow(http, importer, host, gate);
        var nonexistent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var prepared = new PreparedImportArtifact(
            new NormalizedArtifact(Path.Combine(nonexistent, "missing.zip"), "hash", 1, 1, ["game.exe"]),
            nonexistent);

        var result = await workflow.ImportAsync(preparation, prepared, "game.exe");

        Assert.True(result.AlreadyImported);
        Assert.Equal(0, importer.Calls);
        Assert.False(Directory.Exists(nonexistent));
    }

    [Fact]
    public void HostMatchesCanonicalIdentityAfterCatalogRenameAndBlocksDuplicateMatches()
    {
        var preparation = Preparation();
        var game = new HostGameSnapshot(
            Guid.NewGuid(),
            "Locally edited title",
            new Dictionary<string, string>
            {
                ["RecompCatalog.Identity"] = preparation.Entry.Identity.Value,
                ["RecompCatalog.Release"] = preparation.Provenance.Release!.Value,
            });
        var renamed = preparation.Entry with
        {
            CatalogEntry = preparation.Entry.CatalogEntry with { Name = "Renamed catalog title" },
        };
        var states = CatalogHostIntegration.MapImportedStates([renamed], [game]);
        Assert.Equal(game.Id, states[renamed.Id].Imported!.GameId);
        Assert.Equal("Locally edited title", states[renamed.Id].Imported!.GameTitle);
        Assert.Equal(CatalogImportDisposition.AlreadyImported,
            CatalogImportSafety.Assess(states[renamed.Id], preparation.Provenance.Release!).Disposition);

        var duplicate = game with { Id = Guid.NewGuid(), Title = "Duplicate" };
        var ambiguous = CatalogHostIntegration.MapImportedStates([renamed], [game, duplicate]);
        Assert.Equal(CatalogEntryState.StateCheckFailed, ambiguous[renamed.Id].Status);
        Assert.False(CatalogImportSafety.Assess(ambiguous[renamed.Id], preparation.Provenance.Release!).CanImport);
    }

    [Fact]
    public void HostFindsLegacyGameWithoutExactReleaseAndNeverTreatsItAsNew()
    {
        var preparation = Preparation();
        var game = new HostGameSnapshot(
            preparation.Provenance.GameId,
            "Legacy edited title",
            new Dictionary<string, string>());
        var states = CatalogHostIntegration.MapImportedStates([preparation.Entry], [game]);
        var state = states[preparation.Entry.Id];

        Assert.Equal(game.Id, state.Imported!.GameId);
        Assert.Equal(CatalogEntryState.ImportedReleaseUnknown, state.Status);
        Assert.Contains("overwrite locally edited metadata",
            CatalogImportSafety.Assess(state, preparation.Provenance.Release!).Message);
    }

    [Fact]
    public void HostOnlyAllowsNewImportWhenNoMatchingGameExists()
    {
        var preparation = Preparation();
        var states = CatalogHostIntegration.MapImportedStates([preparation.Entry], []);
        Assert.Equal(CatalogEntryState.NeverImported, states[preparation.Entry.Id].Status);
        Assert.True(CatalogImportSafety.Assess(states[preparation.Entry.Id], preparation.Provenance.Release!).CanImport);
    }

    private static Task<CatalogImportResult> UnexpectedImport(CancellationToken token) =>
        throw new Xunit.Sdk.XunitException("The importer must not be invoked.");

    internal static ImportPreparation Preparation()
    {
        var catalogEntry = new CatalogEntry { Name = "Game", Repository = "org/game", FolderName = "game" };
        var identity = CatalogIdentity.Create(new Uri("https://catalog.test/index.json"),
            "test", new Uri("https://catalog.test/list.json"), catalogEntry);
        var entry = new CatalogBrowserEntry(identity.Value, catalogEntry, "test", "Test",
            identity.SourceLocation, "Test", [], new Uri("https://github.com/org/game"))
        {
            Identity = identity,
        };
        var asset = new ReleaseAsset("game-win.zip", new Uri("https://downloads.test/game.zip"), 1, "application/zip");
        var release = new ReleaseInfo("v1", "Release", null, [asset]);
        var provenance = new CatalogImportProvenance(StableId.Create("game:org/game:Game"),
            "Game", identity, "v1", ReleaseIdentity.Create(identity, release, asset), null);
        return new(entry, release, asset, provenance, EditableMetadataDraft.Empty("Game"));
    }

    private sealed class StubHost(ImportPreparation preparation) : ICatalogHostIntegration
    {
        internal CatalogEntryStateView State { get; set; } = CatalogEntryStateView.NeverImported();
        internal int Reads { get; private set; }
        internal bool ReturnMissingState { get; set; }
        internal Exception? ReadFailure { get; set; }

        public Task<IReadOnlyDictionary<string, CatalogEntryStateView>> LoadImportedStatesAsync(
            IReadOnlyList<CatalogBrowserEntry> entries, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            if (ReadFailure is not null)
                throw ReadFailure;
            Assert.Equal(preparation.Entry.Id, Assert.Single(entries).Id);
            IReadOnlyDictionary<string, CatalogEntryStateView> states = ReturnMissingState
                ? new Dictionary<string, CatalogEntryStateView>()
                : new Dictionary<string, CatalogEntryStateView> { [preparation.Entry.Id] = State };
            return Task.FromResult(states);
        }

        public IReadOnlyList<string> GetMetadataProviders() => [];
        public Task<IReadOnlyList<MetadataLookupResult>> SearchMetadataAsync(
            string providerName, string query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MetadataLookupResult>>([]);
        public Task<MetadataLookupResult?> GetMetadataAsync(
            string providerName, string providerGameId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MetadataLookupResult?>(null);
    }
}
