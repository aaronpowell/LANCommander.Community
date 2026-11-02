namespace LANCommander.Community.Server.RecompCatalog.Core;

public interface ICatalogClient
{
    Task<IReadOnlyList<CatalogSource>> LoadAsync(
        Uri indexLocation,
        CancellationToken cancellationToken = default);
}

public interface IReleaseClient
{
    Task<ReleaseInfo> GetLatestAsync(
        CatalogEntry entry,
        CancellationToken cancellationToken = default);
}

public interface IArtifactNormalizer
{
    Task<NormalizedArtifact> DownloadAndNormalizeAsync(
        CatalogEntry entry,
        ReleaseAsset asset,
        string workingDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IPackageBuilder
{
    Task<PackageResult> BuildAsync(
        PackageRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}
