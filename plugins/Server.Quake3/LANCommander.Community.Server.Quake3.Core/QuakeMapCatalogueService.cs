using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LANCommander.Community.Server.Quake3.Core;

/// <summary>Bounds protect remote downloads and central-directory allocations. PK3 files are
/// staged in the caller's durable root, never extracted. Cache identity includes server, roots,
/// full file paths, sizes and modification times; refresh bypasses matching metadata.</summary>
public sealed class QuakeMapCatalogueService
{
    public const long MaximumPk3Bytes = 1L << 30;
    public const long MaximumScanBytes = 2L << 30;
    public const int MaximumListedFiles = 100_000;
    public const int MaximumArchiveEntries = 100_000;
    public const long MaximumCentralDirectoryBytes = 32L << 20;
    public const int MaximumMapCount = 8192;
    public const int MaximumContentRoots = 8;
    private readonly IQuakeStateStore store;
    private readonly string stagingRoot;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();

    public QuakeMapCatalogueService(IQuakeStateStore store, string durableRoot)
    {
        this.store = store;
        stagingRoot = Path.Combine(Path.GetFullPath(durableRoot), "quake3-staging");
    }

    public async Task<QuakeMapCatalogue> GetAsync(Guid serverId, ImmutableArray<string> contentRoots,
        IQuakeServerFiles files, bool refresh = false, CancellationToken cancellationToken = default)
    {
        ValidateRoots(contentRoots);
        var gate = gates.GetOrAdd(serverId, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var known = (await store.ReadAsync(serverId, cancellationToken)).Catalogue;
            try
            {
                var candidates = new List<QuakeFileEntry>();
                var loose = new List<QuakeFileEntry>();
                var listed = 0;
                foreach (var root in contentRoots)
                {
                    var entries = await files.ListAsync(serverId, root, cancellationToken);
                    listed = checked(listed + entries.Count);
                    if (listed > MaximumListedFiles) throw new QuakeCatalogueException("Map file listing exceeds the limit.");
                    candidates.AddRange(entries.Where(e => IsImmediateChild(root, e.Path) &&
                        e.Path.EndsWith(".pk3", StringComparison.OrdinalIgnoreCase)));
                    var mapsRoot = Combine(root, "maps");
                    entries = await files.ListAsync(serverId, mapsRoot, cancellationToken);
                    listed = checked(listed + entries.Count);
                    if (listed > MaximumListedFiles) throw new QuakeCatalogueException("Map file listing exceeds the limit.");
                    loose.AddRange(entries.Where(e => IsImmediateChild(mapsRoot, e.Path) &&
                        e.Path.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase)));
                }
                var all = candidates.Concat(loose).OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();
                if (all.Any(e => e.Length < 0) || candidates.Any(e => e.Length > MaximumPk3Bytes))
                    throw new QuakeCatalogueException("Map pack exceeds the size limit.");
                long advertisedBytes = 0;
                foreach (var entry in candidates)
                {
                    advertisedBytes += entry.Length;
                    if (advertisedBytes > MaximumScanBytes) throw new QuakeCatalogueException("Map scan exceeds the download limit.");
                }
                var signature = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
                    { roots = contentRoots, files = all })));
                if (!refresh && known is not null && known.ContentRoots.SequenceEqual(contentRoots) &&
                    known.Signature == signature)
                    return Fresh(known);
                var maps = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var entry in loose) AddMap(maps, FileStem(entry.Path));
                var buffer = new byte[81920];
                long downloaded = 0;
                var archiveEntries = 0;
                foreach (var pak in candidates)
                {
                    Directory.CreateDirectory(stagingRoot);
                    var path = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N") + ".pk3");
                    try
                    {
                        await using (var source = await files.OpenReadAsync(serverId, pak.Path, cancellationToken))
                        await using (var destination = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
                            FileShare.None, buffer.Length, FileOptions.Asynchronous))
                        {
                            long length = 0;
                            int read;
                            while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
                            {
                                length += read;
                                downloaded += read;
                                if (length > MaximumPk3Bytes || downloaded > MaximumScanBytes)
                                    throw new QuakeCatalogueException("Map scan exceeds the download limit.");
                                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            }
                            if (length != pak.Length) throw new QuakeCatalogueException("A map pack changed during the scan.");
                            await ValidateArchiveBoundsAsync(destination, MaximumArchiveEntries - archiveEntries, cancellationToken);
                            destination.Position = 0;
                            using var archive = new ZipArchive(destination, ZipArchiveMode.Read, true);
                            archiveEntries = checked(archiveEntries + archive.Entries.Count);
                            if (archiveEntries > MaximumArchiveEntries)
                                throw new QuakeCatalogueException("Map archive listing exceeds the limit.");
                            foreach (var entry in archive.Entries)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                var name = entry.FullName;
                                if (name.StartsWith("maps/", StringComparison.Ordinal) &&
                                    !name.AsSpan(5).Contains('/') && !name.Contains('\\') &&
                                    name.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase))
                                    AddMap(maps, FileStem(name));
                            }
                        }
                    }
                    finally
                    {
                        try { File.Delete(path); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            throw new IOException("Could not clean up staged Quake map pack.", ex);
                        }
                    }
                }
                var metadata = new QuakeCatalogueMetadata(contentRoots, signature, maps.ToImmutableArray(), DateTimeOffset.UtcNow);
                await store.UpdateAsync(serverId, state => state with { Catalogue = metadata }, cancellationToken);
                return Fresh(metadata);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                QuakeCatalogueException or OverflowException or NotSupportedException or InvalidOperationException)
            {
                return new(known is null ? QuakeCatalogueAvailability.Unavailable : QuakeCatalogueAvailability.Stale,
                    known?.Maps ?? [], known?.ScannedAt,
                    $"Server maps could not be read ({ex.GetType().Name}): {ex.Message}" +
                    (ex.InnerException is null ? "" : $" Cause ({ex.InnerException.GetType().Name}): {ex.InnerException.Message}") +
                    " Refresh when server files are available.");
            }
        }
        finally { gate.Release(); }
    }

    internal static void ValidateRoots(ImmutableArray<string> roots)
    {
        if (roots.IsDefaultOrEmpty || roots.Length > MaximumContentRoots || roots.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Supply between 1 and 8 map content roots.");
    }
    private static QuakeMapCatalogue Fresh(QuakeCatalogueMetadata value) =>
        new(QuakeCatalogueAvailability.Fresh, value.Maps, value.ScannedAt, null);
    private static void AddMap(SortedSet<string> maps, string name)
    {
        if (!QuakeNames.IsMap(name)) return;
        maps.Add(name);
        if (maps.Count > MaximumMapCount) throw new QuakeCatalogueException("Map catalogue exceeds the map limit.");
    }
    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
    private static string Combine(string root, string name) => root.TrimEnd('/', '\\') + "/" + name;
    private static bool IsImmediateChild(string root, string path)
    {
        var prefix = Normalize(root) + "/";
        var normalized = Normalize(path);
        return normalized.StartsWith(prefix, StringComparison.Ordinal) &&
            !normalized.AsSpan(prefix.Length).Contains('/');
    }
    private static string FileStem(string path)
    {
        var filename = Normalize(path).Split('/')[^1];
        return filename[..^4];
    }

    // Preflight the central-directory budget before ZipArchive materializes entry objects.
    // ZIP64 is supported within the same byte/entry limits; spanning archives are not.
    private static async Task ValidateArchiveBoundsAsync(FileStream stream, int remainingEntries, CancellationToken token)
    {
        var tailLength = (int)Math.Min(stream.Length, 65535 + 22);
        var tail = new byte[tailLength];
        stream.Position = stream.Length - tailLength;
        await stream.ReadExactlyAsync(tail, token);
        var offset = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == 0x06054b50 &&
                i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20)) == tail.Length)
            {
                offset = i;
                break;
            }
        }
        if (offset < 0) throw new InvalidDataException("Invalid map archive.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(offset + 4)) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(offset + 6)) != 0)
            throw new QuakeCatalogueException("Spanning map archives are unsupported.");
        ulong count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(offset + 10));
        var diskCount = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(offset + 8));
        ulong size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(offset + 12));
        ulong start = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(offset + 16));
        var endPosition = stream.Length - tailLength + offset;
        if (count == ushort.MaxValue || size == uint.MaxValue || start == uint.MaxValue)
        {
            if (endPosition < 20) throw new InvalidDataException("Invalid ZIP64 map archive.");
            var locator = new byte[20];
            stream.Position = endPosition - 20;
            await stream.ReadExactlyAsync(locator, token);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != 0x07064b50 ||
                BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(4)) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(16)) != 1)
                throw new InvalidDataException("Invalid ZIP64 map archive.");
            var zip64Position = BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
            if (zip64Position > (ulong)(endPosition - 20) || (ulong)(endPosition - 20) - zip64Position < 56)
                throw new InvalidDataException("Invalid ZIP64 map archive.");
            stream.Position = (long)zip64Position;
            var zip64 = new byte[56];
            await stream.ReadExactlyAsync(zip64, token);
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip64) != 0x06064b50 ||
                BinaryPrimitives.ReadUInt32LittleEndian(zip64.AsSpan(16)) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(zip64.AsSpan(20)) != 0)
                throw new InvalidDataException("Invalid ZIP64 map archive.");
            count = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(32));
            if (BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(24)) != count)
                throw new InvalidDataException("Invalid ZIP64 map archive.");
            size = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(40));
            start = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(48));
            endPosition = (long)zip64Position;
        }
        else if (count != diskCount) throw new InvalidDataException("Invalid map archive.");
        if (count > (ulong)remainingEntries || size > MaximumCentralDirectoryBytes)
            throw new QuakeCatalogueException("Map archive listing exceeds the limit.");
        if (start > (ulong)endPosition || size > (ulong)endPosition - start)
            throw new InvalidDataException("Invalid map archive.");
    }
}
