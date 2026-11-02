using LANCommander.RecompCatalog.Core;
using LANCommander.SDK.Enums;
using LANCommander.SDK.Helpers;
using LANCommander.SDK.Models.Manifest;
using System.IO.Compression;
using ManifestArchive = LANCommander.SDK.Models.Manifest.Archive;
using ManifestScript = LANCommander.SDK.Models.Manifest.Script;

namespace LANCommander.RecompCatalog.Infrastructure;

/// <summary>
/// Builds a LANCommander redistributable package for one administrator-supplied ROM. Each game
/// that links it supplies its own target file name and directory as redistributable options.
/// </summary>
public static class RomRedistributableBuilder
{
    public const string NotesMarker = "Created by LANCommander Recomp Catalog (ROM redistributable).";

    public const string OptionSchema = """
        Options:
          TargetFileName:
            Type: string
            DisplayName: ROM file name
            Description: File name the recomp expects for its ROM, such as the game ID followed by .z64.
            Required: true
          TargetDirectory:
            Type: string
            DisplayName: Target directory
            Description: Folder relative to the game install directory. Leave blank for the install root.
            Default: ''
        """;

    public static Guid CreateId(string sha256) =>
        StableId.Create($"rom:{sha256.ToLowerInvariant()}");

    public static string CreateName(string displayName, string sha256)
    {
        var label = string.IsNullOrWhiteSpace(displayName) ? "ROM" : displayName.Trim();
        if (label.Length > 80)
            label = label[..80].TrimEnd();
        return $"ROM: {label} [{sha256[..12].ToLowerInvariant()}]";
    }

    public static async Task<RomRedistributableInfo> BuildAsync(
        NormalizedRom rom,
        string displayName,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rom);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var sha256 = rom.Sha256.ToLowerInvariant();
        var id = CreateId(sha256);
        var archiveId = StableId.Create($"rom-archive:{sha256}");
        var installScriptId = StableId.Create($"rom-script:install:{sha256}");
        var detectScriptId = StableId.Create($"rom-script:detect:{sha256}");
        var name = CreateName(displayName, sha256);
        var description = $"ROM supplied by a LANCommander administrator ({rom.Size:N0} bytes).";
        var now = DateTime.UtcNow;
        const string createdBy = "LANCommander Recomp Catalog";

        var manifest = new Redistributable
        {
            Id = id,
            Name = name,
            Version = "1.0",
            Description = description,
            Notes = string.Join('\n',
                NotesMarker,
                $"SHA-256: {sha256}",
                $"SHA-1: {rom.Sha1}",
                $"Original file: {rom.OriginalFileName}",
                $"Source byte order: {rom.SourceByteOrder}"),
            OptionSchema = OptionSchema,
            ReleasedOn = now,
            ManifestVersion = "1.0.0",
            CreatedBy = createdBy,
            CreatedOn = now,
            UpdatedBy = createdBy,
            UpdatedOn = now,
        };

        var temporaryPath = $"{outputPath}.{Guid.NewGuid():N}.tmp";
        var archivePath = $"{outputPath}.{Guid.NewGuid():N}.zip";
        try
        {
            var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrWhiteSpace(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            using (var archiveZip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                archiveZip.CreateEntryFromFile(
                    rom.Path,
                    RomNormalizer.NormalizedFileName,
                    CompressionLevel.Optimal);
            }

            manifest.Archives.Add(new ManifestArchive
            {
                Id = archiveId,
                Version = manifest.Version,
                ObjectKey = archiveId.ToString(),
                Changelog = "ROM supplied by an administrator.",
                CompressedSize = new FileInfo(archivePath).Length,
                UncompressedSize = rom.Size,
                CreatedBy = createdBy,
                CreatedOn = now,
            });
            manifest.Scripts.Add(new ManifestScript
            {
                Id = installScriptId,
                Type = ScriptType.Install,
                Name = "Copy ROM into game",
                Description = "Copies the ROM to the location configured for each game.",
                Platforms = RuntimePlatform.Windows,
            });
            manifest.Scripts.Add(new ManifestScript
            {
                Id = detectScriptId,
                Type = ScriptType.DetectInstall,
                Name = "Detect ROM",
                Description = "Checks whether the configured ROM file is already present and matches.",
                Platforms = RuntimePlatform.Windows,
            });

            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var package = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                var archiveEntry = package.CreateEntry($"Archives/{archiveId}", CompressionLevel.NoCompression);
                await using (var archiveOutput = archiveEntry.Open())
                await using (var content = File.OpenRead(archivePath))
                {
                    await content.CopyToAsync(archiveOutput, cancellationToken);
                }

                await WriteEntryAsync(package, $"Scripts/{installScriptId}", BuildInstallScript(sha256), cancellationToken);
                await WriteEntryAsync(package, $"Scripts/{detectScriptId}", BuildDetectInstallScript(sha256), cancellationToken);
                await WriteEntryAsync(package, ManifestHelper.ManifestFilename, ManifestHelper.Serialize(manifest), cancellationToken);
            }

            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            throw;
        }
        finally
        {
            if (File.Exists(archivePath))
                File.Delete(archivePath);
        }

        return new RomRedistributableInfo(id, name, sha256, description);
    }

    /// <summary>Reads the SHA-256 recorded in a plugin-created ROM redistributable's notes.</summary>
    public static string? ReadSha256(string? notes)
    {
        if (notes is null || !notes.StartsWith(NotesMarker, StringComparison.Ordinal))
            return null;

        const string prefix = "SHA-256: ";
        return notes.Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    }

    internal static string BuildInstallScript(string sha256) => $$"""
        # Generated by LANCommander Recomp Catalog.
        # Copies the administrator-supplied ROM to the location configured for this game.
        $ErrorActionPreference = 'Stop'
        {{BuildPrelude(sha256)}}
        $Source = Get-RecompRomSource
        if ((Get-RecompRomSha256 $Source) -ne $ExpectedSha256) {
            throw 'The downloaded ROM does not match its expected SHA-256 hash.'
        }

        $Target = Resolve-RecompRomTarget
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($Target)) | Out-Null
        [System.IO.File]::Copy($Source, $Target, $true)
        Write-Host "Copied ROM to $Target"
        """;

    internal static string BuildDetectInstallScript(string sha256) => $$"""
        # Generated by LANCommander Recomp Catalog.
        # Reports whether the configured ROM file is already present with the expected hash.
        $ErrorActionPreference = 'Stop'
        {{BuildPrelude(sha256)}}
        $Return = $false
        try {
            $Target = Resolve-RecompRomTarget
            if ([System.IO.File]::Exists($Target)) {
                $Return = (Get-RecompRomSha256 $Target) -eq $ExpectedSha256
            }
        }
        catch {
            Write-Warning $_.Exception.Message
            $Return = $false
        }

        $Return
        """;

    private static string BuildPrelude(string sha256) => $$"""
        $ExpectedSha256 = '{{sha256}}'

        function Get-RecompRomSha256([string] $Path) {
            $Stream = [System.IO.File]::OpenRead($Path)
            try {
                return [System.Convert]::ToHexString(
                    [System.Security.Cryptography.SHA256]::HashData($Stream)).ToLowerInvariant()
            }
            finally {
                $Stream.Dispose()
            }
        }

        function Resolve-RecompRomTarget {
            $Options = Get-RedistributableOptions -Path $InstallDirectory -Id $GameManifest.Id -Name $RedistributableManifest.Name
            $FileName = "$($Options.TargetFileName)".Trim()
            $Directory = "$($Options.TargetDirectory)".Trim().Replace('/', '\').TrimEnd('\')

            if ([string]::IsNullOrWhiteSpace($FileName) -or $FileName -eq '.' -or $FileName -eq '..' -or $FileName.IndexOfAny([char[]]'\/:*?"<>|') -ge 0) {
                throw "The ROM file name '$FileName' must be a single file name."
            }

            if ($Directory.Length -gt 0) {
                if ([System.IO.Path]::IsPathRooted($Directory) -or $Directory.Contains(':')) {
                    throw "The ROM directory '$Directory' must be relative to the game install directory."
                }

                foreach ($Segment in $Directory.Split('\')) {
                    if ($Segment -eq '' -or $Segment -eq '.' -or $Segment -eq '..') {
                        throw "The ROM directory '$Directory' must not contain empty, '.' or '..' segments."
                    }
                }
            }

            $Root = [System.IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\') + '\'
            $Target = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($Root, $Directory, $FileName))
            if (-not $Target.StartsWith($Root, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw 'The ROM target resolves outside the game install directory.'
            }

            return $Target
        }

        function Get-RecompRomSource {
            $Candidates = @(
                [System.IO.Path]::Combine($InstallDirectory, '.lancommander', "$($RedistributableManifest.Id)", 'Files', '{{RomNormalizer.NormalizedFileName}}'),
                [System.IO.Path]::Combine((Get-Location).Path, '{{RomNormalizer.NormalizedFileName}}')
            )

            foreach ($Candidate in $Candidates) {
                if ([System.IO.File]::Exists($Candidate)) {
                    return $Candidate
                }
            }

            throw 'The ROM was not found in the extracted redistributable.'
        }
        """;

    private static async Task WriteEntryAsync(
        ZipArchive package,
        string name,
        string content,
        CancellationToken cancellationToken)
    {
        var entry = package.CreateEntry(name, CompressionLevel.NoCompression);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
    }
}
