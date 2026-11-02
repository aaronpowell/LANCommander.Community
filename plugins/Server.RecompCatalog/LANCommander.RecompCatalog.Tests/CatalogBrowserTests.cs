using LANCommander.RecompCatalog.Core;

namespace LANCommander.RecompCatalog.Tests;

public class CatalogBrowserTests
{
    [Fact]
    public async Task MapsFeedEntriesAndFacets()
    {
        var service = CreateService(
            [
                Source("Nintendo",
                    Entry("Zelda", "org/zelda", ["recomp", "n64", "windows"])),
                Source("PlayStation",
                    Entry("Crash", "org/crash", ["recreation", "ps1", "linux"])),
            ]);

        var result = await service.LoadAsync("https://catalog.test/index.json");

        Assert.Equal(CatalogBrowserStatus.Ready, result.Status);
        Assert.Equal(["Nintendo", "PlayStation"], result.OriginalPlatforms);
        Assert.Equal(["Linux", "Windows"], result.TargetPlatforms);
        Assert.Equal(["linux", "n64", "ps1", "recomp", "recreation", "windows"], result.Tags);

        var zelda = Assert.Single(result.Entries, entry => entry.DisplayName == "Zelda");
        Assert.Equal("Nintendo", zelda.OriginalPlatform);
        Assert.Equal(["Windows"], zelda.TargetPlatforms);
        Assert.Equal(new Uri("https://github.com/org/zelda"), zelda.RepositoryUrl);
        Assert.Equal(
            "v1|https://catalog.test/index.json|nintendo|https://catalog.test/nintendo.json|github|org/zelda|zelda",
            zelda.Identity.Value);
    }

    [Fact]
    public async Task FiltersDeterministicallyAcrossSearchAndFacets()
    {
        var service = CreateService(
            [
                Source("Nintendo",
                    Entry("Zelda", "org/zelda", ["recomp", "n64", "windows"]),
                    Entry("Banjo", "org/banjo", ["recomp", "n64", "windows"])),
                Source("PlayStation",
                    Entry("Zelda Tool", "org/tool", ["tool", "ps1", "windows"])),
            ]);
        var loaded = await service.LoadAsync("https://catalog.test/index.json");

        var results = CatalogBrowserService.ApplyFilter(
            loaded.Entries,
            new CatalogBrowserFilter(
                Query: "zelda",
                Tag: "recomp",
                OriginalPlatform: "Nintendo",
                TargetPlatform: "Windows"));

        Assert.Equal("Zelda", Assert.Single(results).DisplayName);
    }

    [Fact]
    public async Task ReportsUnconfiguredEmptyAndFailedFeeds()
    {
        var unconfigured = await CreateService([]).LoadAsync(null);
        var empty = await CreateService([]).LoadAsync("https://catalog.test/index.json");
        var failed = await new CatalogBrowserService(
            new ThrowingCatalogClient(),
            new StubReleaseClient())
            .LoadAsync("https://catalog.test/index.json");

        Assert.Equal(CatalogBrowserStatus.Unconfigured, unconfigured.Status);
        Assert.Equal(CatalogBrowserStatus.Empty, empty.Status);
        Assert.Equal(CatalogBrowserStatus.Failed, failed.Status);
        Assert.Equal("Feed unavailable.", failed.ErrorMessage);
    }

    [Fact]
    public async Task ResolvesReleaseMetadataWithoutDownloadingAssets()
    {
        var entry = Entry("Zelda", "org/zelda", ["recomp"]);
        var browserEntry = new CatalogBrowserEntry(
            "source:org/zelda:Zelda",
            entry,
            "source",
            "Nintendo",
            new Uri("https://catalog.test/nintendo.json"),
            "Nintendo",
            [],
            new Uri("https://github.com/org/zelda"));
        var release = new ReleaseInfo(
            "v1.2.3",
            "Version 1.2.3",
            DateTimeOffset.Parse("2026-10-01T00:00:00Z"),
            [new ReleaseAsset("zelda-win.zip", new Uri("https://downloads.test/zelda.zip"), 42, "application/zip")]);
        var service = new CatalogBrowserService(
            new StubCatalogClient([]),
            new StubReleaseClient(release));

        var preview = await service.PreviewImportAsync(browserEntry);

        Assert.Equal(ImportPreviewStatus.Ready, preview.Status);
        var previewRelease = Assert.IsType<ReleaseInfo>(preview.Release);
        Assert.Same(release, previewRelease);
        Assert.Single(previewRelease.Assets);
    }

    [Fact]
    public void CanonicalIdentityNormalizesFeedProviderAndRepository()
    {
        var entry = Entry("Zelda", "/Org/Zelda/", ["recomp"]) with
        {
            RepositorySource = " GitHub ",
            FolderName = " Zelda ",
        };

        var first = CatalogIdentity.Create(
            new Uri("https://CATALOG.test/index.json?cache=1"),
            " Nintendo ",
            new Uri("https://CATALOG.test/lists/nintendo.json#fragment"),
            entry);
        var second = CatalogIdentity.Create(
            new Uri("https://catalog.test/index.json"),
            "nintendo",
            new Uri("https://catalog.test/lists/nintendo.json"),
            entry);

        Assert.Equal(first.Value, second.Value);
        Assert.Equal("github", first.RepositoryProvider);
        Assert.Equal("org/zelda", first.Repository);
    }

    [Fact]
    public void ClassifiesImportedReleaseConservatively()
    {
        var identity = CatalogIdentity.Create(
            new Uri("https://catalog.test/index.json"),
            "nintendo",
            new Uri("https://catalog.test/nintendo.json"),
            Entry("Zelda", "org/zelda", ["recomp"]));
        var asset = new ReleaseAsset(
            "zelda.zip",
            new Uri("https://downloads.test/zelda.zip"),
            42,
            "application/zip");
        var release = ReleaseIdentity.Create(
            identity,
            new ReleaseInfo("v1", "v1", null, [asset]),
            asset);
        var imported = new CatalogImportProvenance(
            Guid.NewGuid(),
            "Zelda",
            identity,
            "v1",
            release,
            null);

        Assert.Equal(
            CatalogEntryState.ImportedCurrent,
            CatalogEntryStateClassifier.Classify(imported, release).Status);
        Assert.True(ReleaseIdentity.TryParse(release.Value, out var parsed));
        Assert.Equal(release, parsed);

        var changed = ReleaseIdentity.Create(
            identity,
            new ReleaseInfo("nightly", "nightly", null, [asset]),
            asset);
        Assert.Equal(
            CatalogEntryState.UpdateAvailable,
            CatalogEntryStateClassifier.Classify(imported, changed).Status);
        Assert.Equal(
            CatalogEntryState.ImportedReleaseUnknown,
            CatalogEntryStateClassifier.Classify(imported).Status);
        Assert.Equal(
            CatalogEntryState.StateCheckFailed,
            CatalogEntryStateClassifier.Classify(imported, failure: "offline").Status);
    }

    [Fact]
    public async Task FiltersByImportedState()
    {
        var loaded = await CreateService(
            [Source("Nintendo", Entry("Zelda", "org/zelda", ["recomp"]))])
            .LoadAsync("https://catalog.test/index.json");
        var imported = loaded.Entries[0] with
        {
            ImportState = new CatalogEntryStateView(CatalogEntryState.UpdateAvailable),
        };

        var results = CatalogBrowserService.ApplyFilter(
            [imported],
            new CatalogBrowserFilter(ImportState: CatalogEntryState.UpdateAvailable));

        Assert.Same(imported, Assert.Single(results));
    }

    [Fact]
    public void MetadataMergeRetainsCatalogAndReleaseProvenance()
    {
        var entry = Entry("Zelda", "org/zelda", ["recomp"]);
        var identity = CatalogIdentity.Create(
            new Uri("https://catalog.test/index.json"),
            "nintendo",
            new Uri("https://catalog.test/nintendo.json"),
            entry);
        var browserEntry = new CatalogBrowserEntry(
            identity.Value,
            entry,
            "nintendo",
            "Nintendo",
            identity.SourceLocation,
            "Nintendo",
            [],
            new Uri("https://github.com/org/zelda"))
        {
            Identity = identity,
        };
        var asset = new ReleaseAsset(
            "zelda.zip",
            new Uri("https://downloads.test/zelda.zip"),
            42,
            "application/zip");
        var release = new ReleaseInfo("v1", "v1", null, [asset]);
        var provenance = new CatalogImportProvenance(
            Guid.NewGuid(),
            "Zelda",
            identity,
            "v1",
            ReleaseIdentity.Create(identity, release, asset),
            null);
        var preparation = new ImportPreparation(browserEntry, release, asset, provenance);
        var metadata = new MetadataLookupResult(
            "IGDB",
            "123",
            "The Legend of Zelda",
            "Description",
            new DateTime(1986, 2, 21),
            true,
            null,
            ["Nintendo"],
            ["Nintendo"],
            ["Adventure"],
            ["Fantasy"],
            ["Steam: 1"]);

        var merged = ImportPreparationMerger.ApplyMetadata(preparation, metadata);

        Assert.Same(provenance, merged.Provenance);
        Assert.Same(asset, merged.Asset);
        Assert.Equal("The Legend of Zelda", merged.Metadata!.Title);
        Assert.Equal("IGDB", merged.Metadata.Provider);
        Assert.Equal("Original", preparation.Metadata?.Title ?? "Original");
    }

    [Fact]
    public void MetadataDraftSeedsFromLookupResultAndRoundTripsToImmutableResult()
    {
        var lookup = new MetadataLookupResult(
            "PCGamingWiki",
            "zelda-64",
            "The Legend of Zelda",
            "A classic adventure.",
            new DateTime(1986, 2, 21),
            true,
            "Engine",
            ["Nintendo EAD"],
            ["Nintendo"],
            ["Action-Adventure"],
            ["Adventure", "RPG"],
            ["PCGamingWiki: 123", "IGDB: 456"]);

        var draft = EditableMetadataDraft.FromMetadataLookupResult(lookup);
        Assert.Equal("The Legend of Zelda", draft.Title);
        Assert.Equal("Nintendo EAD", draft.Developers);
        Assert.Equal("Adventure, RPG", draft.Tags);

        draft = draft with
        {
            Title = "The Legend of Zelda: Ocarina of Time",
            Description = "Updated description.",
            Developers = "Nintendo EAD, Grezzo",
            Singleplayer = false,
        };

        var roundTrip = draft.ToMetadataLookupResult();
        Assert.Equal("The Legend of Zelda: Ocarina of Time", roundTrip.Title);
        Assert.Equal("Updated description.", roundTrip.Description);
        Assert.False(roundTrip.Singleplayer);
        Assert.Equal(["Nintendo EAD", "Grezzo"], roundTrip.Developers);
        Assert.Equal(["Nintendo"], roundTrip.Publishers);
        Assert.Equal(["Action-Adventure"], roundTrip.Genres);
        Assert.Equal(["Adventure", "RPG"], roundTrip.Tags);
        Assert.Equal(["PCGamingWiki: 123", "IGDB: 456"], roundTrip.ExternalIds);
    }

    [Fact]
    public void MetadataDraftSupportsManualEntryWithoutLookup()
    {
        var draft = EditableMetadataDraft.Empty("Custom title", "manual", "custom-id", "Manually entered description");
        draft = draft with
        {
            ReleasedOn = new DateTime(2024, 5, 13),
            Engine = "OpenTTD",
            Developers = "Example Studio",
            Genres = "Strategy",
            Tags = "recomp, open-source",
            ExternalIds = "steampowered: 42",
        };

        var result = draft.ToMetadataLookupResult();
        Assert.Equal("Custom title", result.Title);
        Assert.Equal("Manually entered description", result.Description);
        Assert.Equal("OpenTTD", result.Engine);
        Assert.Equal(["Example Studio"], result.Developers);
        Assert.Equal(["recomp", "open-source"], result.Tags);
    }

    [Fact]
    public void ProvenanceRemainsImmutableWhenApplyingEdits()
    {
        var entry = Entry("Zelda", "org/zelda", ["recomp"]);
        var identity = CatalogIdentity.Create(
            new Uri("https://catalog.test/index.json"),
            "nintendo",
            new Uri("https://catalog.test/nintendo.json"),
            entry);
        var browserEntry = new CatalogBrowserEntry(
            identity.Value,
            entry,
            "nintendo",
            "Nintendo",
            identity.SourceLocation,
            "Nintendo",
            [],
            new Uri("https://github.com/org/zelda"))
        {
            Identity = identity,
        };
        var asset = new ReleaseAsset(
            "zelda.zip",
            new Uri("https://downloads.test/zelda.zip"),
            42,
            "application/zip");
        var release = new ReleaseInfo("v1", "v1", null, [asset]);
        var provenance = new CatalogImportProvenance(
            Guid.NewGuid(),
            "Zelda",
            identity,
            "v1",
            ReleaseIdentity.Create(identity, release, asset),
            null);
        var preparation = new ImportPreparation(
            browserEntry,
            release,
            asset,
            provenance,
            EditableMetadataDraft.Empty("Manual draft"));
        var lookup = new MetadataLookupResult(
            "IGDB",
            "123",
            "The Legend of Zelda",
            "Description",
            null,
            true,
            null,
            ["Nintendo"],
            ["Nintendo"],
            ["Adventure"],
            ["Fantasy"],
            []);

        var lookupDraft = EditableMetadataDraft.FromMetadataLookupResult(lookup);
        var updated = ImportPreparationMerger.UpdateMetadataDraft(
            preparation,
            lookupDraft with { Title = "Edited title" });

        Assert.Same(provenance, updated.Provenance);
        Assert.Same(identity, updated.Provenance.Catalog);
        Assert.Same(provenance.Release, updated.Provenance.Release);
        Assert.Same(asset, updated.Asset);
        Assert.Equal("Manual draft", preparation.Metadata!.Title);
        Assert.Equal("Edited title", updated.Metadata!.Title);
    }

    private static CatalogBrowserService CreateService(IReadOnlyList<CatalogSource> sources) =>
        new(new StubCatalogClient(sources), new StubReleaseClient());

    private static CatalogSource Source(string name, params CatalogEntry[] entries) =>
        new(
            name.ToLowerInvariant(),
            new Uri($"https://catalog.test/{name.ToLowerInvariant()}.json"),
            name,
            $"{name} entries",
            "1.0.0",
            entries);

    private static CatalogEntry Entry(
        string name,
        string repository,
        IReadOnlyList<string> tags) =>
        new()
        {
            Name = name,
            Repository = repository,
            FolderName = name.Replace(" ", "", StringComparison.Ordinal),
            Tags = tags,
        };

    private sealed class StubCatalogClient(IReadOnlyList<CatalogSource> sources) : ICatalogClient
    {
        public Task<IReadOnlyList<CatalogSource>> LoadAsync(
            Uri indexLocation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(sources);
    }

    private sealed class ThrowingCatalogClient : ICatalogClient
    {
        public Task<IReadOnlyList<CatalogSource>> LoadAsync(
            Uri indexLocation,
            CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("Feed unavailable.");
    }

    private sealed class StubReleaseClient(ReleaseInfo? release = null) : IReleaseClient
    {
        public Task<ReleaseInfo> GetLatestAsync(
            CatalogEntry entry,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(release ?? new ReleaseInfo("v1", "v1", null, []));
    }
}
