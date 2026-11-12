using System.Reflection;
using LANCommander.Community.Server.RecompCatalog.Core;
using LANCommander.Community.Server.RecompCatalog.Infrastructure;
using LANCommander.Community.Server.RecompCatalog.Providers;
using LANCommander.Community.Server.RecompCatalog.Plugin;
using LANCommander.SDK.Enums;
using LANCommander.SDK.Plugins;
using LANCommander.Server.Plugins;
using ManifestGame = LANCommander.SDK.Models.Manifest.Game;
using ManifestGenre = LANCommander.SDK.Models.Manifest.Genre;

namespace LANCommander.Community.Server.RecompCatalog.Tests;

/// <summary>Exercises the plugin's host adapters against the published LANCommander.Server.Plugins contracts.</summary>
public class HostContractTests
{
    [Fact]
    public void DeclaresMinimumHostVersionForPublishedContracts()
    {
        var plugin = typeof(RecompCatalogPlugin).Assembly.GetCustomAttribute<LANCommanderPluginAttribute>();

        Assert.NotNull(plugin);
        Assert.Equal("2.2.0-beta.4", plugin.MinHostVersion);
        Assert.Equal(PluginHost.Server, plugin.Hosts);
    }

    [Fact]
    public void ReportsTheFullAssemblyInformationalVersion()
    {
        var assembly = typeof(RecompCatalogPlugin).Assembly;
        var expected = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        Assert.Equal(expected, new RecompCatalogPlugin().Version);
    }

    [Fact]
    public async Task LoadsImportStateFromCustomFieldsAndOnlyReadsVersionsForCandidateGames()
    {
        var preparation = CatalogImportSafetyTests.Preparation();
        var imported = new GameInfo { Id = Guid.NewGuid(), Title = "Locally edited" };
        var unrelated = new GameInfo { Id = Guid.NewGuid(), Title = "Unrelated" };
        var games = new FakeGameProvider([imported, unrelated])
        {
            CustomFields =
            [
                Field(imported.Id, CatalogHostIntegration.CatalogIdentityField, preparation.Entry.Identity.Value),
                Field(imported.Id, CatalogHostIntegration.ReleaseIdentityField, preparation.Provenance.Release!.Value),
                Field(unrelated.Id, "Other", "value"),
            ],
        };
        var versions = new FakeGameVersionProvider();
        versions.Versions[imported.Id] =
        [
            new GameVersionInfo { GameId = imported.Id, Version = "v0", SortOrder = 0 },
            new GameVersionInfo { GameId = imported.Id, Version = "v1", SortOrder = 1 },
        ];
        var host = new CatalogHostIntegration(games, versions, new FakeMetadataLookup());

        var states = await host.LoadImportedStatesAsync([preparation.Entry]);

        var state = states[preparation.Entry.Id];
        Assert.Equal(imported.Id, state.Imported!.GameId);
        Assert.Equal("Locally edited", state.Imported.GameTitle);
        Assert.Equal("v1", state.Imported.ImportedTag);
        Assert.Equal([imported.Id], versions.VersionReads);
        Assert.Equal(CatalogImportDisposition.AlreadyImported,
            CatalogImportSafety.Assess(state, preparation.Provenance.Release!).Disposition);
    }

    [Fact]
    public async Task LoadsLegacyGameByStableIdWithoutCatalogFields()
    {
        var preparation = CatalogImportSafetyTests.Preparation();
        var legacy = new GameInfo { Id = preparation.Provenance.GameId, Title = "Legacy" };
        var versions = new FakeGameVersionProvider();
        var host = new CatalogHostIntegration(new FakeGameProvider([legacy]), versions, new FakeMetadataLookup());

        var states = await host.LoadImportedStatesAsync([preparation.Entry]);

        Assert.Equal(CatalogEntryState.ImportedReleaseUnknown, states[preparation.Entry.Id].Status);
        Assert.Equal([legacy.Id], versions.VersionReads);
    }

    [Fact]
    public async Task MapsMetadataLookupResults()
    {
        var metadata = new FakeMetadataLookup
        {
            Game = new ManifestGame
            {
                Title = "Zelda",
                Description = "Adventure",
                Genres = [new ManifestGenre { Name = "Action" }],
            },
        };
        var host = new CatalogHostIntegration(new FakeGameProvider([]), new FakeGameVersionProvider(), metadata);

        Assert.Equal(["IGDB", "PCGamingWiki"], host.GetMetadataProviders());
        var result = Assert.Single(await host.SearchMetadataAsync("IGDB", "  zelda  "));
        Assert.Equal("zelda", metadata.LastQuery);
        Assert.Equal(("IGDB", "42", "Zelda", "Adventure"),
            (result.Provider, result.ProviderGameId, result.Title, result.Description));
        Assert.Equal(["Action"], result.Genres);
        Assert.Equal("Zelda", (await host.GetMetadataAsync("IGDB", "42"))!.Title);
    }

    [Fact]
    public async Task RomLinkReplacesPluginRomPreservesManualRedistributablesAndStoresPlacement()
    {
        var oldRom = PluginRom(new string('a', 64));
        var newRom = PluginRom(new string('b', 64));
        var directX = new RedistributableInfo { Id = Guid.NewGuid(), Name = "DirectX" };
        var redistributables = new FakeRedistributableProvider([oldRom, newRom, directX]);
        var versions = new FakeGameVersionProvider();
        var gameId = Guid.NewGuid();
        var host = new CatalogRomHost(redistributables, versions, new ThrowingPackageImporter());

        Assert.Null(await host.GetGameRomAsync(gameId));
        var version = await versions.GetOrCreateLatestAsync(gameId);
        versions.Links[version.Id] = [new(directX, null), new(oldRom, null)];

        await host.LinkAsync(gameId, newRom.Id, new RomPlacement("baserom.z64", "roms"));

        Assert.Equal([directX.Id, newRom.Id], versions.Links[version.Id].Select(link => link.Redistributable.Id));
        var link = await host.GetGameRomAsync(gameId);
        Assert.Equal(newRom.Id, link!.Rom.Id);
        Assert.Equal(new RomPlacement("baserom.z64", "roms"), link.Placement);
        Assert.Equal(new[] { oldRom.Id, newRom.Id }.Order(), (await host.ListRomsAsync()).Select(rom => rom.Id).Order());

        await host.UnlinkAsync(gameId);
        Assert.Equal([directX.Id], versions.Links[version.Id].Select(row => row.Redistributable.Id));
    }

    [Fact]
    public async Task RomImportReusesExistingRomAndRefusesNameClashes()
    {
        var sha = new string('c', 64);
        var rom = new NormalizedRom(Path.Combine(Path.GetTempPath(), "missing.z64"), "game.n64", 8,
            new string('d', 40), sha, RomByteOrder.N64BigEndian);
        var existing = PluginRom(sha) with { Id = RomRedistributableBuilder.CreateId(sha) };
        var importer = new ThrowingPackageImporter();

        var reused = await new CatalogRomHost(
            new FakeRedistributableProvider([existing]), new FakeGameVersionProvider(), importer)
            .ImportRomAsync(rom, "Game");
        Assert.Equal(existing.Id, reused.Id);

        var clash = new RedistributableInfo
        {
            Id = Guid.NewGuid(),
            Name = RomRedistributableBuilder.CreateName("Game", sha),
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CatalogRomHost(
            new FakeRedistributableProvider([clash]), new FakeGameVersionProvider(), importer)
            .ImportRomAsync(rom, "Game"));
        Assert.Equal(0, importer.Calls);
    }

    private static GameCustomFieldInfo Field(Guid gameId, string name, string value) =>
        new() { Id = Guid.NewGuid(), GameId = gameId, Name = name, Value = value };

    private static RedistributableInfo PluginRom(string sha256) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"ROM {sha256[..6]}",
        Notes = $"{RomRedistributableBuilder.NotesMarker}\nSHA-256: {sha256}",
    };
}

internal sealed class ThrowingPackageImporter : IPackageImporter
{
    internal int Calls { get; private set; }

    public Task<PackageImportResult> ImportFileAsync(
        string packagePath, Guid? storageLocationId = null, ManifestType? manifestType = null,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        throw new Xunit.Sdk.XunitException("The package importer must not be invoked.");
    }

    public Task<PackageImportResult> ImportStreamAsync(
        Stream package, Guid? storageLocationId = null, ManifestType? manifestType = null,
        IProgress<long>? copyProgress = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        throw new Xunit.Sdk.XunitException("The package importer must not be invoked.");
    }
}

internal sealed class FakeMetadataLookup : IMetadataLookup
{
    internal ManifestGame? Game { get; init; }
    internal string? LastQuery { get; private set; }

    public IReadOnlyList<string> GetProviderNames() => ["PCGamingWiki", "IGDB"];

    public Task<MetadataSearchPage> SearchGamesAsync(
        string providerName, string input, int limit = 10, int offset = 0, CancellationToken cancellationToken = default)
    {
        LastQuery = input;
        IReadOnlyList<MetadataGame> results = Game is null ? [] : [new MetadataGame("42", Game)];
        return Task.FromResult(new MetadataSearchPage(results, false, limit, offset));
    }

    public Task<ManifestGame?> GetGameAsync(
        string providerName, string gameId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Game);
}

internal sealed class FakeGameProvider(IReadOnlyList<GameInfo> games) : IGameProvider
{
    internal IReadOnlyList<GameCustomFieldInfo> CustomFields { get; init; } = [];

    public Task<IReadOnlyList<GameInfo>> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(games);
    public Task<GameInfo?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(games.FirstOrDefault(game => game.Id == id));
    public Task<IReadOnlyList<GameCustomFieldInfo>> GetCustomFieldsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CustomFields);
    public Task<IReadOnlyList<GameCustomFieldInfo>> GetCustomFieldsAsync(Guid gameId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GameCustomFieldInfo>>(CustomFields.Where(field => field.GameId == gameId).ToList());

    public Task<IReadOnlyList<GameInfo>> GetAddonsAsync(Guid baseGameId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<GameInfo?> FindByExternalIdAsync(string provider, string externalId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<GameInfo> AddAsync(GameInfo game, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<GameInfo> UpdateAsync(GameInfo game, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<Guid>> GetLinksAsync(Guid gameId, GameLinkKind kind, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SetLinksAsync(Guid gameId, GameLinkKind kind, IEnumerable<Guid> ids, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<string?> GetRedistributableOptionsAsync(Guid gameId, Guid redistributableId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SetRedistributableOptionsAsync(Guid gameId, Guid redistributableId, string? optionsJson, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<GameExternalIdInfo>> GetExternalIdsAsync(Guid gameId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<GameExternalIdInfo> SetExternalIdAsync(Guid gameId, string provider, string externalId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task RemoveExternalIdAsync(Guid gameId, string provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<GameCustomFieldInfo> SetCustomFieldAsync(Guid gameId, string name, string value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task RemoveCustomFieldAsync(Guid gameId, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class FakeGameVersionProvider : IGameVersionProvider
{
    internal Dictionary<Guid, List<GameVersionInfo>> Versions { get; } = [];
    internal Dictionary<Guid, List<GameVersionRedistributableInfo>> Links { get; } = [];
    internal List<Guid> VersionReads { get; } = [];

    public Task<IReadOnlyList<GameVersionInfo>> GetAsync(Guid gameId, CancellationToken cancellationToken = default)
    {
        VersionReads.Add(gameId);
        return Task.FromResult<IReadOnlyList<GameVersionInfo>>(Versions.GetValueOrDefault(gameId) ?? []);
    }

    public Task<GameVersionInfo?> GetLatestAsync(Guid gameId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Versions.GetValueOrDefault(gameId)?.OrderByDescending(version => version.SortOrder).FirstOrDefault());

    public async Task<GameVersionInfo> GetOrCreateLatestAsync(Guid gameId, CancellationToken cancellationToken = default)
    {
        if (await GetLatestAsync(gameId, cancellationToken) is { } latest)
            return latest;

        var created = new GameVersionInfo { Id = Guid.NewGuid(), GameId = gameId, Version = "1.0" };
        Versions[gameId] = [created];
        return created;
    }

    public Task<IReadOnlyList<GameVersionRedistributableInfo>> GetRedistributablesAsync(Guid versionId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GameVersionRedistributableInfo>>(Links.GetValueOrDefault(versionId)?.ToList() ?? []);

    public Task SetRedistributablesAsync(Guid versionId, IEnumerable<Guid> redistributableIds, CancellationToken cancellationToken = default)
    {
        var current = Links.GetValueOrDefault(versionId) ?? [];
        Links[versionId] = redistributableIds
            .Select(id => current.FirstOrDefault(link => link.Redistributable.Id == id)
                ?? new GameVersionRedistributableInfo(FakeRedistributableProvider.Known[id], null))
            .ToList();
        return Task.CompletedTask;
    }

    public Task SetRedistributableOptionsAsync(Guid versionId, Guid redistributableId, string? optionsJson, CancellationToken cancellationToken = default)
    {
        var links = Links[versionId];
        var index = links.FindIndex(link => link.Redistributable.Id == redistributableId);
        links[index] = links[index] with { Options = optionsJson };
        return Task.CompletedTask;
    }

    public Task<GameVersionInfo?> GetVersionAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<GameVersionInfo> CreateAsync(Guid gameId, string version, string? changelog = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<GameVersionInfo> UpdateAsync(GameVersionInfo version, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class FakeRedistributableProvider : IRedistributableProvider
{
    // Lets the version fake resolve ids the same way the host joins version links to redistributables.
    internal static readonly Dictionary<Guid, RedistributableInfo> Known = [];
    private readonly IReadOnlyList<RedistributableInfo> _items;

    internal FakeRedistributableProvider(IReadOnlyList<RedistributableInfo> items)
    {
        _items = items;
        lock (Known)
        {
            foreach (var item in items)
                Known[item.Id] = item;
        }
    }

    public Task<IReadOnlyList<RedistributableInfo>> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(_items);
    public Task<RedistributableInfo?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_items.FirstOrDefault(item => item.Id == id));
    public Task<RedistributableInfo?> FindAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(_items.FirstOrDefault(item => string.Equals(item.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)));

    public Task<RedistributableInfo> AddAsync(RedistributableInfo redistributable, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RedistributableInfo> UpdateAsync(RedistributableInfo redistributable, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
