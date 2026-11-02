namespace LANCommander.Community.Server.RecompCatalog.Core;

public enum CatalogBrowserStatus
{
    Loading,
    Ready,
    Unconfigured,
    Empty,
    Failed,
}

public sealed record CatalogBrowserEntry(
    string Id,
    CatalogEntry CatalogEntry,
    string SourceId,
    string SourceName,
    Uri SourceLocation,
    string OriginalPlatform,
    IReadOnlyList<string> TargetPlatforms,
    Uri? RepositoryUrl)
{
    public string DisplayName => CatalogEntry.DisplayName;
    public CatalogIdentity Identity { get; init; } = null!;
    public CatalogEntryStateView ImportState { get; init; } = CatalogEntryStateView.NeverImported();
}

public sealed record CatalogBrowserResult(
    CatalogBrowserStatus Status,
    Uri? FeedLocation,
    IReadOnlyList<CatalogBrowserEntry> Entries,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> OriginalPlatforms,
    IReadOnlyList<string> TargetPlatforms,
    string? ErrorMessage = null)
{
    public static CatalogBrowserResult Unconfigured() =>
        new(CatalogBrowserStatus.Unconfigured, null, [], [], [], []);

    public static CatalogBrowserResult Failed(Uri feedLocation, string errorMessage) =>
        new(CatalogBrowserStatus.Failed, feedLocation, [], [], [], [], errorMessage);
}

public sealed record CatalogBrowserFilter(
    string? Query = null,
    string? Tag = null,
    string? OriginalPlatform = null,
    string? TargetPlatform = null,
    CatalogEntryState? ImportState = null);

public enum ImportPreviewStatus
{
    Loading,
    Ready,
    Unsupported,
    Failed,
}

public sealed record ImportPreview(
    ImportPreviewStatus Status,
    CatalogBrowserEntry Entry,
    ReleaseInfo? Release = null,
    string? Message = null);

public sealed class CatalogBrowserService(
    ICatalogClient catalogs,
    IReleaseClient releases)
{
    private static readonly IReadOnlyDictionary<string, string> TargetPlatformTags =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["windows"] = "Windows",
            ["linux"] = "Linux",
            ["macos"] = "macOS",
            ["osx"] = "macOS",
            ["android"] = "Android",
        };

    public async Task<CatalogBrowserResult> LoadAsync(
        string? feedUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(feedUrl))
            return CatalogBrowserResult.Unconfigured();

        if (!Uri.TryCreate(feedUrl, UriKind.Absolute, out var feedLocation)
            || feedLocation.Scheme != Uri.UriSchemeHttps)
        {
            return CatalogBrowserResult.Failed(
                feedLocation ?? new Uri("https://invalid.invalid/"),
                "The catalog feed URL must be an absolute HTTPS URL.");
        }

        try
        {
            var sources = await catalogs.LoadAsync(feedLocation, cancellationToken);
            var entries = Map(feedLocation, sources);
            var status = entries.Count == 0
                ? CatalogBrowserStatus.Empty
                : CatalogBrowserStatus.Ready;

            return new CatalogBrowserResult(
                status,
                feedLocation,
                entries,
                DistinctSorted(entries.SelectMany(entry => entry.CatalogEntry.Tags)),
                DistinctSorted(entries.Select(entry => entry.OriginalPlatform)),
                DistinctSorted(entries.SelectMany(entry => entry.TargetPlatforms)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CatalogBrowserResult.Failed(feedLocation, ex.Message);
        }
    }

    public async Task<ImportPreview> PreviewImportAsync(
        CatalogBrowserEntry entry,
        CancellationToken cancellationToken = default)
    {
        if (!entry.CatalogEntry.IsGitHub)
        {
            return new ImportPreview(
                ImportPreviewStatus.Unsupported,
                entry,
                Message: "Release preview currently supports GitHub repositories only.");
        }

        try
        {
            var release = await releases.GetLatestAsync(entry.CatalogEntry, cancellationToken);
            return new ImportPreview(ImportPreviewStatus.Ready, entry, release);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ImportPreview(ImportPreviewStatus.Failed, entry, Message: ex.Message);
        }
    }

    public static IReadOnlyList<CatalogBrowserEntry> ApplyFilter(
        IEnumerable<CatalogBrowserEntry> entries,
        CatalogBrowserFilter filter)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(filter);

        var filtered = entries;

        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            var query = filter.Query.Trim();
            filtered = filtered.Where(entry =>
                entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.CatalogEntry.Repository.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.SourceName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.CatalogEntry.Tags.Any(tag =>
                    tag.Contains(query, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(filter.Tag))
        {
            filtered = filtered.Where(entry => entry.CatalogEntry.Tags.Any(tag =>
                tag.Equals(filter.Tag, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(filter.OriginalPlatform))
        {
            filtered = filtered.Where(entry => entry.OriginalPlatform.Equals(
                filter.OriginalPlatform,
                StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filter.TargetPlatform))
        {
            filtered = filtered.Where(entry => entry.TargetPlatforms.Any(platform =>
                platform.Equals(filter.TargetPlatform, StringComparison.OrdinalIgnoreCase)));
        }

        if (filter.ImportState.HasValue)
            filtered = filtered.Where(entry => entry.ImportState.Status == filter.ImportState.Value);

        return filtered
            .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.CatalogEntry.Repository, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<CatalogBrowserEntry> Map(
        Uri feedLocation,
        IEnumerable<CatalogSource> sources) =>
        sources
            .SelectMany(source => source.Entries.Select(entry => Map(feedLocation, source, entry)))
            .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.CatalogEntry.Repository, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static CatalogBrowserEntry Map(
        Uri feedLocation,
        CatalogSource source,
        CatalogEntry entry)
    {
        var targetPlatforms = DistinctSorted(entry.Tags
            .Select(tag => TargetPlatformTags.TryGetValue(tag, out var platform) ? platform : null)
            .OfType<string>());

        var identity = CatalogIdentity.Create(feedLocation, source.Id, source.Location, entry);

        return new CatalogBrowserEntry(
            identity.Value,
            entry,
            source.Id,
            source.Name,
            source.Location,
            source.Name,
            targetPlatforms,
            RepositoryLocation(entry))
        {
            Identity = identity,
        };
    }

    private static Uri? RepositoryLocation(CatalogEntry entry)
    {
        var repository = entry.Repository.Trim('/');
        var source = entry.RepositorySource?.Trim();

        if (string.IsNullOrWhiteSpace(source)
            || source.Equals("github", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate($"https://github.com/{repository}", UriKind.Absolute, out var github)
                ? github
                : null;
        }

        if (source.Equals("gitlab", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate($"https://gitlab.com/{repository}", UriKind.Absolute, out var gitlab)
                ? gitlab
                : null;
        }

        return null;
    }

    private static IReadOnlyList<string> DistinctSorted(IEnumerable<string> values) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
