namespace LANCommander.RecompCatalog.Core;

public static class ReleaseAssetSelection
{
    private static readonly string[] RejectedMarkers =
        ["source code", "symbols", "debug", "linux", "macos", "osx", "android"];

    public static IReadOnlyList<ReleaseAsset> WindowsPackages(
        IEnumerable<ReleaseAsset> assets,
        string? nameFilter = null) =>
        assets
            .Where(asset => IsSupportedExtension(asset.Name))
            .Where(asset => !RejectedMarkers.Any(marker =>
                asset.Name.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            .Where(asset => string.IsNullOrWhiteSpace(nameFilter)
                || asset.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsSupportedExtension(string name)
    {
        var extension = Path.GetExtension(name);
        return extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase);
    }
}

public static class ExecutableSelection
{
    public static string RequireExplicit(
        IReadOnlyList<string> candidates,
        string? selectedExecutable)
    {
        if (string.IsNullOrWhiteSpace(selectedExecutable))
            throw new InvalidOperationException("Select an executable before building the LCX.");

        var normalizedSelection = selectedExecutable.Replace('\\', '/');
        return candidates.SingleOrDefault(candidate =>
                candidate.Equals(normalizedSelection, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"'{selectedExecutable}' is not an executable candidate from the normalized artifact.");
    }
}

public static class CatalogSearch
{
    public static IReadOnlyList<CatalogEntry> Filter(
        IEnumerable<CatalogEntry> entries,
        string? query)
    {
        var filtered = string.IsNullOrWhiteSpace(query)
            ? entries
            : entries.Where(entry =>
                entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Repository.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Tags.Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase)));

        return filtered.OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
