using System.IO.Compression;
using System.Security.Cryptography;
using LANCommander.Community.Server.RecompCatalog.Core;

namespace LANCommander.Community.Server.RecompCatalog.Infrastructure;

public sealed class ArtifactNormalizer(HttpClient httpClient) : IArtifactNormalizer
{
    public const long MaxDownloadBytes = 4L * 1024 * 1024 * 1024;
    public const long MaxExpandedBytes = 20L * 1024 * 1024 * 1024;
    public const int MaxEntries = 10_000;
    public const double MaxExpansionRatio = 200;

    public async Task<NormalizedArtifact> DownloadAndNormalizeAsync(
        CatalogEntry entry,
        ReleaseAsset asset,
        string workingDirectory,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(workingDirectory);
        var downloadPath = Path.Combine(workingDirectory, $"{Guid.NewGuid():N}{Path.GetExtension(asset.Name)}");
        var outputPath = Path.Combine(workingDirectory, $"{Guid.NewGuid():N}.zip");

        try
        {
            progress?.Report($"Downloading {asset.Name}...");
            var sha256 = await DownloadAsync(asset, downloadPath, progress, cancellationToken);
            progress?.Report("Validating and normalizing archive...");
            var result = await NormalizeAsync(entry, asset, downloadPath, outputPath, cancellationToken);
            return result with { Sha256 = sha256 };
        }
        catch
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
            throw;
        }
        finally
        {
            if (File.Exists(downloadPath))
                File.Delete(downloadPath);
        }
    }

    public static async Task<NormalizedArtifact> NormalizeLocalAsync(
        CatalogEntry entry,
        string artifactPath,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(workingDirectory);
        var outputPath = Path.Combine(workingDirectory, $"{Guid.NewGuid():N}.zip");
        var fileInfo = new FileInfo(artifactPath);
        var asset = new ReleaseAsset(
            fileInfo.Name,
            new Uri("https://example.invalid/" + Uri.EscapeDataString(fileInfo.Name)),
            fileInfo.Length,
            "application/octet-stream");

        try
        {
            var result = await NormalizeAsync(entry, asset, artifactPath, outputPath, cancellationToken);
            await using var stream = File.OpenRead(artifactPath);
            var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
                .ToLowerInvariant();
            return result with { Sha256 = sha256 };
        }
        catch
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
            throw;
        }
    }

    private async Task<string> DownloadAsync(
        ReleaseAsset asset,
        string destination,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            asset.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Release assets must be downloaded over HTTPS.");

        if (response.Content.Headers.ContentLength is > MaxDownloadBytes)
            throw new InvalidDataException("The release asset exceeds the download size limit.");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long total = 0;

        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;

            total += read;
            if (total > MaxDownloadBytes)
                throw new InvalidDataException("The release asset exceeds the download size limit.");

            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            var totalText = response.Content.Headers.ContentLength is long length
                ? $" / {length:N0}"
                : "";
            progress?.Report($"Downloaded {total:N0}{totalText} bytes...");
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static async Task<NormalizedArtifact> NormalizeAsync(
        CatalogEntry entry,
        ReleaseAsset asset,
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        long uncompressedSize;
        var executables = new List<string>();
        var writtenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var outputStream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write);
        using (var output = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (Path.GetExtension(asset.Name).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var safeName = NormalizeEntryPath(Path.GetFileName(asset.Name));
                var target = output.CreateEntry(safeName, CompressionLevel.Optimal);
                await using var targetStream = target.Open();
                await using var source = File.OpenRead(inputPath);
                await source.CopyToAsync(targetStream, cancellationToken);
                uncompressedSize = source.Length;
                executables.Add(safeName);
                writtenPaths.Add(safeName);
            }
            else
            {
                using var input = ZipFile.OpenRead(inputPath);
                if (input.Entries.Count > MaxEntries)
                    throw new InvalidDataException("The archive exceeds the entry-count limit.");

                uncompressedSize = 0;
                long compressedSize = 0;
                foreach (var item in input.Entries)
                {
                    uncompressedSize = checked(uncompressedSize + item.Length);
                    compressedSize = checked(compressedSize + item.CompressedLength);
                }
                compressedSize = Math.Max(1, compressedSize);
                if (uncompressedSize > MaxExpandedBytes
                    || uncompressedSize / (double)compressedSize > MaxExpansionRatio)
                    throw new InvalidDataException("The archive exceeds expansion safety limits.");

                foreach (var sourceEntry in input.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RejectSymbolicLink(sourceEntry);
                    var path = NormalizeEntryPath(sourceEntry.FullName);
                    if (path.Length == 0 || sourceEntry.FullName.EndsWith('/'))
                        continue;
                    if (!writtenPaths.Add(path))
                        throw new InvalidDataException($"The archive contains duplicate path '{path}'.");

                    var target = output.CreateEntry(path, CompressionLevel.Optimal);
                    await using var source = sourceEntry.Open();
                    await using var targetStream = target.Open();
                    await source.CopyToAsync(targetStream, cancellationToken);
                    if (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                        executables.Add(path);
                }
            }

            foreach (var file in entry.FilesToAdd)
            {
                var path = NormalizeEntryPath(file);
                if (path.Length > 0 && writtenPaths.Add(path))
                    output.CreateEntry(path, CompressionLevel.NoCompression);
            }
        }

        if (executables.Count == 0)
            throw new InvalidDataException("The normalized artifact does not contain an executable.");

        return new NormalizedArtifact(
            outputPath,
            "",
            new FileInfo(outputPath).Length,
            uncompressedSize,
            executables.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList());
    }

    internal static string NormalizeEntryPath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized))
            return "";

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (Path.IsPathRooted(path) || segments.Any(segment => segment is "." or ".."))
            throw new InvalidDataException($"Unsafe archive path '{path}'.");
        foreach (var segment in segments)
            ValidateWindowsPathSegment(path, segment);

        return string.Join('/', segments);
    }

    private static void ValidateWindowsPathSegment(string path, string segment)
    {
        if (segment.EndsWith('.') || segment.EndsWith(' ')
            || segment.Any(character => character < 32 || "<>:\"|?*".Contains(character)))
            throw new InvalidDataException($"Unsafe Windows archive path '{path}'.");

        var baseName = segment.Split('.')[0];
        if (WindowsReservedNames.Contains(baseName))
            throw new InvalidDataException($"Reserved Windows archive path '{path}'.");
    }

    private static readonly HashSet<string> WindowsReservedNames = new(
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5",
         "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4",
         "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"],
        StringComparer.OrdinalIgnoreCase);

    private static void RejectSymbolicLink(ZipArchiveEntry entry)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixSymbolicLink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        if (unixMode == UnixSymbolicLink)
            throw new InvalidDataException($"Symbolic link entry '{entry.FullName}' is not supported.");
    }
}
