namespace LANCommander.RecompCatalog.Core;

public sealed record CatalogIdentity(
    string Value,
    Uri FeedLocation,
    string SourceId,
    Uri SourceLocation,
    string RepositoryProvider,
    string Repository,
    string EntryKey)
{
    public static CatalogIdentity Create(
        Uri feedLocation,
        string sourceId,
        Uri sourceLocation,
        CatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(feedLocation);
        ArgumentNullException.ThrowIfNull(sourceLocation);
        ArgumentNullException.ThrowIfNull(entry);

        var provider = NormalizeSegment(entry.RepositorySource, "github");
        var repository = NormalizeRepository(entry.Repository);
        var entryKey = NormalizeSegment(
            string.IsNullOrWhiteSpace(entry.FolderName) ? entry.Name : entry.FolderName,
            "entry");
        var normalizedFeed = NormalizeUri(feedLocation);
        var normalizedSource = NormalizeUri(sourceLocation);
        var normalizedSourceId = NormalizeSegment(sourceId, "source");
        var value = string.Join(
            "|",
            "v1",
            normalizedFeed,
            normalizedSourceId,
            normalizedSource,
            provider,
            repository,
            entryKey);

        return new CatalogIdentity(
            value,
            feedLocation,
            normalizedSourceId,
            sourceLocation,
            provider,
            repository,
            entryKey);
    }

    private static string NormalizeRepository(string value) =>
        string.Join(
            "/",
            value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToLowerInvariant();

    private static string NormalizeSegment(string? value, string fallback)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string NormalizeUri(Uri value)
    {
        var builder = new UriBuilder(value)
        {
            Fragment = "",
            Host = value.Host.ToLowerInvariant(),
            Query = "",
        };
        builder.Path = builder.Path.TrimEnd('/');
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }
}

public sealed record ReleaseIdentity(
    string Value,
    string RepositoryProvider,
    string Repository,
    string Tag,
    string AssetName,
    Uri AssetLocation)
{
    public static ReleaseIdentity Create(
        CatalogIdentity catalog,
        ReleaseInfo release,
        ReleaseAsset asset)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(asset);

        var tag = release.Tag.Trim();
        var assetName = asset.Name.Trim();
        var value = string.Join(
            "|",
            "v1",
            catalog.RepositoryProvider,
            catalog.Repository,
            tag,
            assetName,
            asset.DownloadUrl.AbsoluteUri);

        return new ReleaseIdentity(
            value,
            catalog.RepositoryProvider,
            catalog.Repository,
            tag,
            assetName,
            asset.DownloadUrl);
    }

    public static bool TryParse(string value, out ReleaseIdentity? identity)
    {
        var parts = value.Split('|', 6);
        if (parts.Length != 6
            || parts[0] != "v1"
            || !Uri.TryCreate(parts[5], UriKind.Absolute, out var assetLocation))
        {
            identity = null;
            return false;
        }

        identity = new ReleaseIdentity(
            value,
            parts[1],
            parts[2],
            parts[3],
            parts[4],
            assetLocation);
        return true;
    }
}

public sealed record PackageUpdatePolicy(
    string Provider,
    string Repository,
    string? ReleaseAssetFilter,
    string SelectedAssetName,
    string ArchiveFormat,
    bool NormalizeSingleRootDirectory,
    string? ExecutablePath = null);

public sealed record CatalogImportProvenance(
    Guid GameId,
    string GameTitle,
    CatalogIdentity Catalog,
    string ImportedTag,
    ReleaseIdentity? Release,
    PackageUpdatePolicy? UpdatePolicy);

public enum CatalogEntryState
{
    NeverImported,
    ImportedCurrent,
    UpdateAvailable,
    ImportedReleaseUnknown,
    StateCheckFailed,
}

public sealed record CatalogEntryStateView(
    CatalogEntryState Status,
    CatalogImportProvenance? Imported = null,
    ReleaseIdentity? Latest = null,
    string? Message = null)
{
    public static CatalogEntryStateView NeverImported() =>
        new(CatalogEntryState.NeverImported);
}

public static class CatalogEntryStateClassifier
{
    public static CatalogEntryStateView Classify(
        CatalogImportProvenance? imported,
        ReleaseIdentity? latest = null,
        string? failure = null)
    {
        if (!string.IsNullOrWhiteSpace(failure))
        {
            return new CatalogEntryStateView(
                CatalogEntryState.StateCheckFailed,
                imported,
                Message: failure);
        }

        if (imported is null)
            return CatalogEntryStateView.NeverImported();

        if (imported.Release is null || latest is null)
        {
            return new CatalogEntryStateView(
                CatalogEntryState.ImportedReleaseUnknown,
                imported,
                latest,
                "The exact imported release or selected asset is unavailable.");
        }

        return string.Equals(imported.Release.Value, latest.Value, StringComparison.Ordinal)
            ? new CatalogEntryStateView(CatalogEntryState.ImportedCurrent, imported, latest)
            : new CatalogEntryStateView(CatalogEntryState.UpdateAvailable, imported, latest);
    }
}

public sealed record MetadataLookupResult(
    string Provider,
    string ProviderGameId,
    string Title,
    string? Description,
    DateTime? ReleasedOn,
    bool Singleplayer,
    string? Engine,
    IReadOnlyList<string> Developers,
    IReadOnlyList<string> Publishers,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> ExternalIds);

public sealed record EditableMetadataDraft(
    string Provider,
    string ProviderGameId,
    string Title,
    string? Description,
    DateTime? ReleasedOn,
    bool Singleplayer,
    string? Engine,
    string Developers,
    string Publishers,
    string Genres,
    string Tags,
    string ExternalIds)
{
    public static EditableMetadataDraft Empty(
        string title,
        string? provider = null,
        string? providerGameId = null,
        string? description = null) =>
        new(
            provider ?? string.Empty,
            providerGameId ?? string.Empty,
            title,
            description,
            null,
            false,
            null,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);

    public static EditableMetadataDraft FromMetadataLookupResult(MetadataLookupResult metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        return new EditableMetadataDraft(
            metadata.Provider,
            metadata.ProviderGameId,
            metadata.Title,
            metadata.Description,
            metadata.ReleasedOn,
            metadata.Singleplayer,
            metadata.Engine,
            JoinValues(metadata.Developers),
            JoinValues(metadata.Publishers),
            JoinValues(metadata.Genres),
            JoinValues(metadata.Tags),
            JoinValues(metadata.ExternalIds));
    }

    /// <summary>
    /// Seeds a draft from what the catalog feed already knows, so an operator can type
    /// metadata by hand without ever running a provider lookup.
    /// </summary>
    public static EditableMetadataDraft SeedFromCatalogEntry(CatalogBrowserEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return SeedFromCatalog(entry.CatalogEntry, fallbackTitle: entry.DisplayName);
    }

    public static EditableMetadataDraft SeedFromCatalog(
        CatalogEntry entry,
        MetadataLookupResult? lookup = null,
        string? fallbackTitle = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var draft = lookup is null
            ? Empty(string.IsNullOrWhiteSpace(fallbackTitle) ? entry.Name : fallbackTitle)
            : FromMetadataLookupResult(lookup);

        return draft with
        {
            Title = string.IsNullOrWhiteSpace(draft.Title)
                ? (string.IsNullOrWhiteSpace(fallbackTitle) ? entry.Name : fallbackTitle)
                : draft.Title,
            Tags = string.IsNullOrWhiteSpace(draft.Tags) ? JoinValues(entry.Tags) : draft.Tags,
        };
    }

    public MetadataLookupResult ToMetadataLookupResult() =>
        new(
            Provider,
            ProviderGameId,
            string.IsNullOrWhiteSpace(Title) ? "Untitled" : Title.Trim(),
            Description,
            ReleasedOn,
            Singleplayer,
            Engine,
            SplitValues(Developers),
            SplitValues(Publishers),
            SplitValues(Genres),
            SplitValues(Tags),
            SplitValues(ExternalIds));

    private static string JoinValues(IEnumerable<string> values) =>
        string.Join(", ", values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase));

    private static IReadOnlyList<string> SplitValues(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value
                .Split([';', ',', '\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
}

public sealed record ImportPreparation(
    CatalogBrowserEntry Entry,
    ReleaseInfo Release,
    ReleaseAsset Asset,
    CatalogImportProvenance Provenance,
    EditableMetadataDraft? Metadata = null);

public static class ImportPreparationMerger
{
    public static ImportPreparation ApplyMetadata(
        ImportPreparation preparation,
        MetadataLookupResult metadata)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(metadata);
        return preparation with { Metadata = EditableMetadataDraft.FromMetadataLookupResult(metadata) };
    }

    public static ImportPreparation UpdateMetadataDraft(
        ImportPreparation preparation,
        EditableMetadataDraft draft)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(draft);
        return preparation with { Metadata = draft };
    }
}
