using LANCommander.Community.Server.RecompCatalog.Core;
using LANCommander.Community.Server.RecompCatalog.Infrastructure;
using LANCommander.SDK.Enums;
using LANCommander.Server.Plugins;

namespace LANCommander.Community.Server.RecompCatalog.Plugin;

internal sealed record PreparedImportArtifact(
    NormalizedArtifact Artifact,
    string WorkingDirectory);

internal sealed record CatalogImportResult(
    Guid GameId,
    int ImportedCount,
    CatalogImportProvenance Provenance,
    bool AlreadyImported = false);

internal sealed class CatalogImportWorkflow(
    RecompCatalogHttpClient httpClient,
    IPackageImporter importer,
    ICatalogHostIntegration host,
    CatalogImportGate gate)
{
    private readonly IArtifactNormalizer _normalizer = new ArtifactNormalizer(httpClient.Client);
    private readonly IPackageBuilder _packages = new LcxPackageBuilder();

    internal async Task<PreparedImportArtifact> PrepareAsync(
        ImportPreparation preparation,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);

        var workingDirectory = Path.Combine(
            Path.GetTempPath(),
            $"lancommander-recomp-{Guid.NewGuid():N}");

        try
        {
            var artifact = await _normalizer.DownloadAndNormalizeAsync(
                preparation.Entry.CatalogEntry,
                preparation.Asset,
                workingDirectory,
                progress,
                cancellationToken);

            return new PreparedImportArtifact(artifact, workingDirectory);
        }
        catch
        {
            _ = DeleteWorkingDirectory(workingDirectory);
            throw;
        }
    }

    internal Task<CatalogImportResult> ImportAsync(
        ImportPreparation preparation,
        PreparedImportArtifact prepared,
        string executable,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        return gate.RunAsync(
            preparation,
            host,
            token => ImportNewAsync(preparation, prepared, executable, progress, token),
            cancellationToken);
    }

    private async Task<CatalogImportResult> ImportNewAsync(
        ImportPreparation preparation,
        PreparedImportArtifact prepared,
        string executable,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var packagePath = Path.Combine(
            prepared.WorkingDirectory,
            $"{Guid.NewGuid():N}.lcx");
        var policy = preparation.Provenance.UpdatePolicy is null
            ? null
            : preparation.Provenance.UpdatePolicy with { ExecutablePath = executable };
        var provenance = preparation.Provenance with
        {
            UpdatePolicy = policy,
            GameTitle = preparation.Metadata?.ToMetadataLookupResult().Title ?? preparation.Provenance.GameTitle,
        };

        try
        {
            progress?.Report("Building LANCommander package...");
            var package = await _packages.BuildAsync(
                new PackageRequest(
                    preparation.Entry.CatalogEntry,
                    preparation.Release,
                    preparation.Asset,
                    prepared.Artifact,
                    executable,
                    packagePath,
                    provenance,
                    preparation.Metadata),
                progress,
                cancellationToken);

            progress?.Report("Importing package into LANCommander...");
            await using var packageStream = File.OpenRead(package.OutputPath);
            var result = await importer.ImportStreamAsync(
                packageStream,
                manifestType: ManifestType.Game,
                copyProgress: new Progress<long>(
                    bytes => progress?.Report($"Uploading package to importer ({bytes:N0} bytes)...")),
                cancellationToken: cancellationToken);

            return new CatalogImportResult(
                result.RecordId,
                result.ImportedCount,
                provenance);
        }
        finally
        {
            if (File.Exists(packagePath))
                File.Delete(packagePath);
        }
    }

    internal static string? Cleanup(PreparedImportArtifact? prepared)
    {
        if (prepared is null)
            return null;

        return DeleteWorkingDirectory(prepared.WorkingDirectory)?.Message;
    }

    private static Exception? DeleteWorkingDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
