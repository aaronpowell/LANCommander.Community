namespace LANCommander.Community.Server.RecompCatalog.Core;

public sealed record ReleaseAsset(
    string Name,
    Uri DownloadUrl,
    long Size,
    string ContentType);

public sealed record ReleaseInfo(
    string Tag,
    string Name,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<ReleaseAsset> Assets);

public sealed record NormalizedArtifact(
    string ZipPath,
    string Sha256,
    long CompressedSize,
    long UncompressedSize,
    IReadOnlyList<string> Executables);

public sealed record PackageRequest(
    CatalogEntry Entry,
    ReleaseInfo Release,
    ReleaseAsset Asset,
    NormalizedArtifact Artifact,
    string Executable,
    string OutputPath,
    CatalogImportProvenance? Provenance = null,
    EditableMetadataDraft? Metadata = null);

public sealed record PackageResult(
    Guid GameId,
    Guid ArchiveId,
    string OutputPath,
    string Sha256);
