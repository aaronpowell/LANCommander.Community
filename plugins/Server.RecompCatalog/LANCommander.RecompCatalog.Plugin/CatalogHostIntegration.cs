using LANCommander.RecompCatalog.Core;
using LANCommander.Server.Plugins;
using System.Text.Json;
using ManifestGame = LANCommander.SDK.Models.Manifest.Game;

namespace LANCommander.RecompCatalog.Plugin;

public interface ICatalogHostIntegration
{
    Task<IReadOnlyDictionary<string, CatalogEntryStateView>> LoadImportedStatesAsync(
        IReadOnlyList<CatalogBrowserEntry> entries,
        CancellationToken cancellationToken = default);

    IReadOnlyList<string> GetMetadataProviders();

    Task<IReadOnlyList<MetadataLookupResult>> SearchMetadataAsync(
        string providerName,
        string query,
        CancellationToken cancellationToken = default);

    Task<MetadataLookupResult?> GetMetadataAsync(
        string providerName,
        string providerGameId,
        CancellationToken cancellationToken = default);
}

/// <summary>A server game as seen through the plugin contracts, reduced to what import state needs.</summary>
internal sealed record HostGameSnapshot(
    Guid Id,
    string Title,
    IReadOnlyDictionary<string, string> CustomFields,
    string ImportedTag = "")
{
    internal string? Field(string name) => CustomFields.TryGetValue(name, out var value) ? value : null;
}

public sealed class CatalogHostIntegration(
    IGameProvider games,
    IGameVersionProvider versions,
    IMetadataLookup metadata) : ICatalogHostIntegration
{
    internal const string CatalogIdentityField = "RecompCatalog.Identity";
    internal const string ReleaseIdentityField = "RecompCatalog.Release";
    internal const string UpdatePolicyField = "RecompCatalog.UpdatePolicy";

    public async Task<IReadOnlyDictionary<string, CatalogEntryStateView>> LoadImportedStatesAsync(
        IReadOnlyList<CatalogBrowserEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var allGames = await games.GetAsync(cancellationToken);
        var fieldsByGame = (await games.GetCustomFieldsAsync(cancellationToken))
            .GroupBy(field => field.GameId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyDictionary<string, string>)group
                    .GroupBy(field => field.Name, StringComparer.Ordinal)
                    .ToDictionary(named => named.Key, named => named.First().Value, StringComparer.Ordinal));
        var legacyIds = entries.Select(LegacyGameId).ToHashSet();

        var snapshots = new List<HostGameSnapshot>(allGames.Count);
        foreach (var game in allGames)
        {
            var fields = fieldsByGame.GetValueOrDefault(game.Id)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var snapshot = new HostGameSnapshot(game.Id, game.Title, fields);

            // Versions are only needed for games that can match a catalog entry.
            if (legacyIds.Contains(game.Id) || !string.IsNullOrWhiteSpace(snapshot.Field(CatalogIdentityField)))
            {
                var gameVersions = await versions.GetAsync(game.Id, cancellationToken);
                snapshot = snapshot with { ImportedTag = LatestTag(gameVersions) };
            }

            snapshots.Add(snapshot);
        }

        return MapImportedStates(entries, snapshots, cancellationToken);
    }

    internal static string LatestTag(IEnumerable<GameVersionInfo> gameVersions) =>
        gameVersions
            .OrderByDescending(version => version.SortOrder)
            .ThenByDescending(version => version.CreatedOn)
            .Select(version => version.Version)
            .FirstOrDefault(version => !string.IsNullOrWhiteSpace(version))
        ?? "";

    private static Guid LegacyGameId(CatalogBrowserEntry entry) =>
        StableId.Create($"game:{entry.CatalogEntry.Repository.ToLowerInvariant()}:{entry.CatalogEntry.Name}");

    internal static IReadOnlyDictionary<string, CatalogEntryStateView> MapImportedStates(
        IReadOnlyList<CatalogBrowserEntry> entries,
        IEnumerable<HostGameSnapshot> games,
        CancellationToken cancellationToken = default)
    {
        var gameList = games.ToList();
        var byCatalogIdentity = gameList
            .Select(game => new { Game = game, Identity = game.Field(CatalogIdentityField) })
            .Where(item => !string.IsNullOrWhiteSpace(item.Identity))
            .GroupBy(item => item.Identity!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Game).ToList(), StringComparer.Ordinal);
        var byGameId = gameList.ToDictionary(game => game.Id);
        var states = new Dictionary<string, CatalogEntryStateView>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var legacyGameId = LegacyGameId(entry);
            if (byCatalogIdentity.TryGetValue(entry.Identity.Value, out var matches) && matches.Count != 1)
            {
                states[entry.Id] = CatalogEntryStateClassifier.Classify(
                    null, failure: "Multiple LANCommander games have this catalog identity. Resolve duplicates before importing.");
                continue;
            }

            var game = matches?.Single();
            if (game is null
                && !byGameId.TryGetValue(legacyGameId, out game))
            {
                states[entry.Id] = CatalogEntryStateView.NeverImported();
                continue;
            }

            var importedTag = game.ImportedTag;
            var releaseValue = game.Field(ReleaseIdentityField);
            _ = ReleaseIdentity.TryParse(releaseValue ?? "", out var release);
            var policyValue = game.Field(UpdatePolicyField);
            var policy = DeserializePolicy(policyValue)
                ?? (release is null
                    ? null
                    : new PackageUpdatePolicy(
                        release.RepositoryProvider,
                        release.Repository,
                        entry.CatalogEntry.ReleaseAssetFilter,
                        release.AssetName,
                        "zip-or-exe",
                        true));
            var provenance = new CatalogImportProvenance(
                game.Id,
                game.Title,
                entry.Identity,
                importedTag,
                release,
                policy);

            states[entry.Id] = CatalogEntryStateClassifier.Classify(provenance);
        }

        return states;
    }

    public IReadOnlyList<string> GetMetadataProviders() =>
        metadata.GetProviderNames()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public async Task<IReadOnlyList<MetadataLookupResult>> SearchMetadataAsync(
        string providerName,
        string query,
        CancellationToken cancellationToken = default)
    {
        var page = await metadata.SearchGamesAsync(providerName, query.Trim(), 10, 0, cancellationToken);

        return page.Results
            .Select(result => Map(providerName, result.Id, result.Data))
            .ToList();
    }

    public async Task<MetadataLookupResult?> GetMetadataAsync(
        string providerName,
        string providerGameId,
        CancellationToken cancellationToken = default)
    {
        var game = await metadata.GetGameAsync(providerName, providerGameId, cancellationToken);
        return game is null ? null : Map(providerName, providerGameId, game);
    }

    private static PackageUpdatePolicy? DeserializePolicy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            return JsonSerializer.Deserialize<PackageUpdatePolicy>(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MetadataLookupResult Map(
        string provider,
        string providerGameId,
        ManifestGame game) =>
        new(
            provider,
            providerGameId,
            game.Title,
            game.Description,
            game.ReleasedOn == default ? null : game.ReleasedOn,
            game.Singleplayer,
            game.Engine?.Name,
            Names(game.Developers),
            Names(game.Publishers),
            Names(game.Genres),
            Names(game.Tags),
            game.ExternalIds?
                .Where(id => !string.IsNullOrWhiteSpace(id.Provider)
                    && !string.IsNullOrWhiteSpace(id.ExternalId))
                .Select(id => $"{id.Provider}: {id.ExternalId}")
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList()
                ?? []);

    private static IReadOnlyList<string> Names<T>(IEnumerable<T>? values)
        where T : LANCommander.SDK.Models.Manifest.BaseModel =>
        values?
            .Select(value => value switch
            {
                LANCommander.SDK.Models.Manifest.Company company => company.Name,
                LANCommander.SDK.Models.Manifest.Genre genre => genre.Name,
                LANCommander.SDK.Models.Manifest.Tag tag => tag.Name,
                _ => null,
            })
            .OfType<string>()
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? [];
}
