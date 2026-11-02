using LANCommander.RecompCatalog.Core;
using LANCommander.SDK.Enums;
using LANCommander.SDK.Helpers;
using LANCommander.SDK.Models.Manifest;
using System.IO.Compression;
using System.Text.Json;
using ManifestArchive = LANCommander.SDK.Models.Manifest.Archive;
using ManifestAction = LANCommander.SDK.Models.Manifest.Action;
using ManifestScript = LANCommander.SDK.Models.Manifest.Script;

namespace LANCommander.RecompCatalog.Infrastructure;

public sealed class LcxPackageBuilder : IPackageBuilder
{
    public async Task<PackageResult> BuildAsync(
        PackageRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var selectedExecutable = ExecutableSelection.RequireExplicit(
            request.Artifact.Executables,
            request.Executable);
        var repositoryKey = request.Entry.Repository.ToLowerInvariant();
        var gameId = StableId.Create($"game:{repositoryKey}:{request.Entry.Name}");
        var archiveId = StableId.Create(
            $"archive:{repositoryKey}:{request.Release.Tag}:{request.Asset.Name}:{request.Artifact.Sha256}");
        var directoryName = SanitizeDirectoryName(
            string.IsNullOrWhiteSpace(request.Entry.FolderName)
                ? request.Entry.Name
                : request.Entry.FolderName);
        var metadata = request.Metadata?.ToMetadataLookupResult();
        var packageScript = BuildPackageScript(request, selectedExecutable);
        var packageScriptId = StableId.Create($"script:package:{repositoryKey}:{request.Entry.Name}");

        var manifest = new Game
        {
            Id = gameId,
            Title = metadata?.Title ?? request.Entry.Name,
            SortTitle = metadata?.Title ?? request.Entry.Name,
            Version = request.Release.Tag,
            DirectoryName = directoryName,
            Description = metadata?.Description ?? BuildDescription(request),
            Notes = $"Source: https://github.com/{request.Entry.Repository}\n"
                + $"Release asset: {request.Asset.Name}\nSHA-256: {request.Artifact.Sha256}",
            Singleplayer = metadata?.Singleplayer ?? true,
            ReleasedOn = metadata?.ReleasedOn ?? default,
            Type = GameType.MainGame,
            Engine = string.IsNullOrWhiteSpace(metadata?.Engine)
                ? null
                : new Engine { Name = metadata.Engine },
            Developers = MapCompanies(metadata?.Developers),
            Publishers = MapCompanies(metadata?.Publishers),
            Genres = MapGenres(metadata?.Genres),
            Tags = MapTags(metadata?.Tags),
            ExternalIds = MapExternalIds(metadata),
            CustomFields = BuildCustomFields(request),
            Actions =
            [
                new ManifestAction
                {
                    Name = "Play",
                    Path = selectedExecutable.Replace('/', Path.DirectorySeparatorChar),
                    WorkingDirectory = Path.GetDirectoryName(
                        selectedExecutable.Replace('/', Path.DirectorySeparatorChar)) ?? "",
                    IsPrimaryAction = true,
                    SortOrder = 0,
                    Platforms = RuntimePlatform.Windows,
                },
            ],
        };

        var archive = new ManifestArchive
        {
            Id = archiveId,
            Version = request.Release.Tag,
            Changelog = $"Imported from {request.Entry.Repository} release {request.Release.Tag}.",
            UncompressedSize = request.Artifact.UncompressedSize,
        };

        var temporaryPath = $"{request.OutputPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath));
            if (!string.IsNullOrWhiteSpace(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var package = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                var now = DateTime.UtcNow;
                const string createdBy = "LANCommander Recomp Catalog";
                manifest.ManifestVersion = "1.0.0";
                manifest.CreatedBy = createdBy;
                manifest.CreatedOn = now;
                manifest.UpdatedBy = createdBy;
                manifest.UpdatedOn = now;

                archive.ObjectKey = archive.Id.ToString();
                archive.CreatedBy = createdBy;
                archive.CreatedOn = now;
                archive.CompressedSize = request.Artifact.CompressedSize;
                manifest.Archives.Add(archive);

                var script = new ManifestScript
                {
                    Id = packageScriptId,
                    Type = ScriptType.Package,
                    Name = "Check upstream release",
                    Description = "Checks the source repository for a new release asset.",
                    Platforms = RuntimePlatform.Windows,
                };
                manifest.Scripts.Add(script);

                progress?.Report($"Writing archive {archive.Version}...");
                var archiveEntry = package.CreateEntry(
                    $"Archives/{archive.Id}",
                    CompressionLevel.NoCompression);
                await using (var archiveOutput = archiveEntry.Open())
                await using (var content = File.OpenRead(request.Artifact.ZipPath))
                {
                    await content.CopyToAsync(archiveOutput, cancellationToken);
                }

                var scriptEntry = package.CreateEntry(
                    $"Scripts/{packageScriptId}",
                    CompressionLevel.NoCompression);
                await using (var scriptStream = scriptEntry.Open())
                await using (var scriptWriter = new StreamWriter(scriptStream))
                {
                    await scriptWriter.WriteAsync(packageScript.AsMemory(), cancellationToken);
                }

                progress?.Report("Writing manifest...");
                var manifestEntry = package.CreateEntry(
                    ManifestHelper.ManifestFilename,
                    CompressionLevel.NoCompression);
                await using (var manifestStream = manifestEntry.Open())
                await using (var manifestWriter = new StreamWriter(manifestStream))
                {
                    await manifestWriter.WriteAsync(
                        ManifestHelper.Serialize(manifest).AsMemory(),
                        cancellationToken);
                }
            }

            File.Move(temporaryPath, request.OutputPath, overwrite: true);
            progress?.Report("Package ready.");
        }
        catch
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            throw;
        }

        return new PackageResult(gameId, archiveId, request.OutputPath, request.Artifact.Sha256);
    }

    private static string BuildPackageScript(PackageRequest request, string executable)
    {
        var repository = EscapePowerShellLiteral(request.Entry.Repository);
        var assetFilter = EscapePowerShellLiteral(request.Entry.ReleaseAssetFilter ?? "");
        var selectedAssetName = EscapePowerShellLiteral(request.Asset.Name);
        var extension = EscapePowerShellLiteral(Path.GetExtension(request.Asset.Name));
        var executablePath = EscapePowerShellLiteral(executable);

        return $$"""
            # Generated by LANCommander Recomp Catalog. Runs on the server's package schedule.
            # Returning nothing means the imported release is already current.
            $repository = '{{repository}}'
            $assetFilter = '{{assetFilter}}'
            $selectedAssetName = '{{selectedAssetName}}'
            $extension = '{{extension}}'
            $executablePath = '{{executablePath}}'

            $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repository/releases/latest" -Headers @{
                'Accept'     = 'application/vnd.github+json'
                'User-Agent' = 'LANCommander'
            }

            $version = [string] $release.tag_name
            $latestArchive = $Game.Archives | Sort-Object CreatedOn -Descending | Select-Object -First 1
            if ($latestArchive -and $version -eq [string] $latestArchive.Version) {
                return
            }

            $candidates = @($release.assets | Where-Object {
                $_.name -match '\.(zip|exe)$' -and
                $_.name -notmatch '(source|symbols?|checksums?|sha\d*|signature)'
            })

            $asset = $null
            if ($assetFilter) {
                $asset = $candidates | Where-Object { $_.name -like "*$assetFilter*" } | Select-Object -First 1
            }
            if (-not $asset) {
                $asset = $candidates | Where-Object { $_.name -eq $selectedAssetName } | Select-Object -First 1
            }
            if (-not $asset) {
                $asset = $candidates | Where-Object {
                    [System.IO.Path]::GetExtension($_.name) -eq $extension -and
                    $_.name -match '(windows|win32|win64|x64|amd64)'
                } | Select-Object -First 1
            }
            if (-not $asset) {
                throw "Release $version has no compatible asset for the saved import policy"
            }

            $temp = [System.IO.Path]::GetTempPath()
            $staging = New-Item -ItemType Directory -Force -Path (Join-Path $temp "RecompCatalog-$([guid]::NewGuid().ToString('N'))")
            $download = Join-Path $temp $asset.name

            Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $download

            if ([System.IO.Path]::GetExtension($asset.name) -eq '.zip') {
                if ($PSVersionTable.PSEdition -eq 'Desktop') {
                    Add-Type -AssemblyName System.IO.Compression.FileSystem
                }
                Expand-Archive -Path $download -DestinationPath $staging.FullName -Force
                Remove-Item -LiteralPath $download -Force

                $entries = @(Get-ChildItem -LiteralPath $staging.FullName)
                if ($entries.Count -eq 1 -and $entries[0].PSIsContainer) {
                    $unwrapped = New-Item -ItemType Directory -Force -Path (Join-Path $temp "RecompCatalog-$([guid]::NewGuid().ToString('N'))")
                    Copy-Item -Path (Join-Path $entries[0].FullName '*') -Destination $unwrapped.FullName -Recurse -Force
                    Remove-Item -LiteralPath $staging.FullName -Recurse -Force
                    $staging = $unwrapped
                }
            }
            else {
                Move-Item -LiteralPath $download -Destination (Join-Path $staging.FullName $asset.name) -Force
            }

            if (-not (Test-Path -LiteralPath (Join-Path $staging.FullName $executablePath))) {
                throw "Updated release $version does not contain expected executable '$executablePath'"
            }

            $Return = New-Package -Path $staging.FullName -Version $version -Changelog ([string] $release.body)
            """;
    }

    private static string EscapePowerShellLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static ICollection<Company> MapCompanies(IReadOnlyList<string>? values) =>
        values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => new Company { Name = value })
            .ToList()
        ?? [];

    private static ICollection<Genre> MapGenres(IReadOnlyList<string>? values) =>
        values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => new Genre { Name = value })
            .ToList()
        ?? [];

    private static ICollection<Tag> MapTags(IReadOnlyList<string>? values) =>
        values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => new Tag { Name = value })
            .ToList()
        ?? [];

    private static ICollection<GameExternalId> MapExternalIds(MetadataLookupResult? metadata)
    {
        if (metadata is null)
            return [];

        var values = metadata.ExternalIds
            .Select(ParseExternalId)
            .Where(value => value is not null)
            .Select(value => value!)
            .ToList();

        if (!string.IsNullOrWhiteSpace(metadata.Provider)
            && !string.IsNullOrWhiteSpace(metadata.ProviderGameId)
            && !values.Any(value =>
                value.Provider.Equals(metadata.Provider, StringComparison.OrdinalIgnoreCase)
                && value.ExternalId.Equals(metadata.ProviderGameId, StringComparison.OrdinalIgnoreCase)))
        {
            values.Add(new GameExternalId
            {
                Provider = metadata.Provider,
                ExternalId = metadata.ProviderGameId,
            });
        }

        return values;
    }

    private static GameExternalId? ParseExternalId(string value)
    {
        var separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
            return null;

        return new GameExternalId
        {
            Provider = value[..separator].Trim(),
            ExternalId = value[(separator + 1)..].Trim(),
        };
    }

    private static ICollection<GameCustomField> BuildCustomFields(PackageRequest request)
    {
        if (request.Provenance is null)
            return [];

        List<GameCustomField> fields =
        [
            new GameCustomField
            {
                Name = "RecompCatalog.Identity",
                Value = request.Provenance.Catalog.Value,
            },
            new GameCustomField
            {
                Name = "RecompCatalog.Release",
                Value = request.Provenance.Release?.Value ?? request.Provenance.ImportedTag,
            },
        ];

        if (request.Provenance.UpdatePolicy is not null)
        {
            fields.Add(new GameCustomField
            {
                Name = "RecompCatalog.UpdatePolicy",
                Value = JsonSerializer.Serialize(request.Provenance.UpdatePolicy),
            });
        }

        return fields;
    }

    private static string BuildDescription(PackageRequest request)
    {
        var project = string.IsNullOrWhiteSpace(request.Entry.Project)
            ? request.Entry.Name
            : request.Entry.Project;
        return $"{project}, imported from GitHub release {request.Release.Tag}. "
            + "Catalog metadata uses a Quiver-compatible feed; this project is independent of Quiver.";
    }

    internal static string SanitizeDirectoryName(string value)
    {
        var result = string.Concat(value.Where(
            character => char.IsLetterOrDigit(character) || character is '-' or '_'));
        if (string.IsNullOrWhiteSpace(result))
            result = "Game";
        if (result.Length > 120)
            result = result[..120];

        return WindowsReservedNames.Contains(result) ? $"_{result}" : result;
    }

    private static readonly HashSet<string> WindowsReservedNames = new(
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5",
         "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4",
         "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"],
        StringComparer.OrdinalIgnoreCase);
}
