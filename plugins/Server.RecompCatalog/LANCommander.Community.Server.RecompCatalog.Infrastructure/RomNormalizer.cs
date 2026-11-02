using LANCommander.Community.Server.RecompCatalog.Core;
using System.Security.Cryptography;

namespace LANCommander.Community.Server.RecompCatalog.Infrastructure;

/// <summary>
/// Copies an administrator-supplied ROM to a working file, converting N64 dumps to big-endian
/// (.z64) order when an N64 header is detected so every game receives the byte order recomps expect.
/// Other files are copied unchanged.
/// </summary>
public static class RomNormalizer
{
    public const long DefaultMaxBytes = 512L * 1024 * 1024;
    public const string NormalizedFileName = "rom.bin";

    private const int BufferSize = 1024 * 1024;

    public static RomByteOrder DetectByteOrder(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4)
            return RomByteOrder.Unknown;

        return (header[0], header[1], header[2], header[3]) switch
        {
            (0x80, 0x37, 0x12, 0x40) => RomByteOrder.N64BigEndian,
            (0x37, 0x80, 0x40, 0x12) => RomByteOrder.N64ByteSwapped,
            (0x40, 0x12, 0x37, 0x80) => RomByteOrder.N64LittleEndian,
            _ => RomByteOrder.Unknown,
        };
    }

    public static async Task<NormalizedRom> NormalizeAsync(
        Stream source,
        string originalFileName,
        string workingDirectory,
        long maxBytes = DefaultMaxBytes,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));

        Directory.CreateDirectory(workingDirectory);
        var outputPath = Path.Combine(workingDirectory, NormalizedFileName);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        var byteOrder = RomByteOrder.Unknown;
        long total = 0;

        try
        {
            await using (var output = new FileStream(
                outputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var first = true;
                while (true)
                {
                    // Fill whole buffers so word swaps never straddle a read boundary.
                    var read = await source.ReadAtLeastAsync(
                        buffer,
                        buffer.Length,
                        throwOnEndOfStream: false,
                        cancellationToken);
                    if (read == 0)
                        break;

                    total += read;
                    if (total > maxBytes)
                        throw new InvalidDataException(
                            $"The ROM is larger than the {maxBytes / 1024 / 1024:N0} MiB limit.");

                    if (first)
                    {
                        byteOrder = DetectByteOrder(buffer.AsSpan(0, read));
                        first = false;
                    }

                    var chunk = buffer.AsSpan(0, read);
                    Swap(chunk, byteOrder);
                    sha1.AppendData(chunk);
                    sha256.AppendData(chunk);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Report(total);
                }
            }

            if (total == 0)
                throw new InvalidDataException("The ROM file is empty.");

            return new NormalizedRom(
                outputPath,
                Path.GetFileName(originalFileName ?? ""),
                total,
                Convert.ToHexStringLower(sha1.GetHashAndReset()),
                Convert.ToHexStringLower(sha256.GetHashAndReset()),
                byteOrder);
        }
        catch
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
            throw;
        }
    }

    private static void Swap(Span<byte> chunk, RomByteOrder byteOrder)
    {
        switch (byteOrder)
        {
            case RomByteOrder.N64ByteSwapped:
                if (chunk.Length % 2 != 0)
                    throw new InvalidDataException("The byte-swapped N64 ROM has an odd length.");
                for (var index = 0; index < chunk.Length; index += 2)
                    (chunk[index], chunk[index + 1]) = (chunk[index + 1], chunk[index]);
                break;

            case RomByteOrder.N64LittleEndian:
                if (chunk.Length % 4 != 0)
                    throw new InvalidDataException("The little-endian N64 ROM length is not a multiple of four bytes.");
                for (var index = 0; index < chunk.Length; index += 4)
                {
                    (chunk[index], chunk[index + 3]) = (chunk[index + 3], chunk[index]);
                    (chunk[index + 1], chunk[index + 2]) = (chunk[index + 2], chunk[index + 1]);
                }
                break;
        }
    }
}
