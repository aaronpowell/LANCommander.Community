namespace LANCommander.Community.Server.RecompCatalog.Core;

public enum RomByteOrder
{
    Unknown,
    N64BigEndian,
    N64ByteSwapped,
    N64LittleEndian,
}

/// <summary>An administrator-supplied ROM after byte-order normalization and hashing.</summary>
public sealed record NormalizedRom(
    string Path,
    string OriginalFileName,
    long Size,
    string Sha1,
    string Sha256,
    RomByteOrder SourceByteOrder)
{
    public bool WasByteSwapped =>
        SourceByteOrder is RomByteOrder.N64ByteSwapped or RomByteOrder.N64LittleEndian;
}

/// <summary>A ROM redistributable created by this plugin.</summary>
public sealed record RomRedistributableInfo(
    Guid Id,
    string Name,
    string Sha256,
    string? Description = null);

/// <summary>A ROM redistributable linked to a game, with that game's placement.</summary>
public sealed record GameRomLink(
    RomRedistributableInfo Rom,
    RomPlacement Placement);

/// <summary>
/// Where a ROM is copied, relative to a game's install directory. Each game supplies its own,
/// so one ROM redistributable can serve several games.
/// </summary>
public sealed record RomPlacement(string TargetFileName, string TargetDirectory = "")
{
    public const string TargetFileNameOption = "TargetFileName";
    public const string TargetDirectoryOption = "TargetDirectory";

    public static string? Validate(string? targetFileName, string? targetDirectory)
    {
        var fileName = targetFileName?.Trim() ?? "";
        if (fileName.Length == 0)
            return "Enter the file name the recomp expects for its ROM.";
        if (fileName is "." or ".."
            || HasInvalidCharacters(fileName)
            || fileName.EndsWith('.')
            || fileName.EndsWith(' '))
        {
            return "The ROM file name must be a single file name without folders.";
        }

        var directory = NormalizeDirectory(targetDirectory);
        if (directory.Length == 0)
            return null;
        if (Path.IsPathRooted(directory)
            || directory.Contains(':')
            || directory.StartsWith('\\'))
        {
            return "The target directory must be relative to the game install directory.";
        }

        foreach (var segment in directory.Split('\\'))
        {
            if (segment.Length == 0 || segment is "." or "..")
                return "The target directory must not contain empty, '.' or '..' segments.";
            if (HasInvalidCharacters(segment) || segment.EndsWith('.') || segment.EndsWith(' '))
                return "The target directory contains invalid characters.";
        }

        return null;
    }

    public static RomPlacement Create(string? targetFileName, string? targetDirectory)
    {
        var error = Validate(targetFileName, targetDirectory);
        if (error is not null)
            throw new ArgumentException(error);

        return new RomPlacement(targetFileName!.Trim(), NormalizeDirectory(targetDirectory));
    }

    /// <summary>The folder of a game's primary executable, which portable recomps use for their data.</summary>
    public static string DirectoryForExecutable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return "";

        // '/' is a separator on every OS, so this also works on Linux servers.
        var directory = Path.GetDirectoryName(
            executablePath.Replace('\\', '/').TrimStart('/'));
        return NormalizeDirectory(directory);
    }

    public IReadOnlyDictionary<string, string> ToOptions() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TargetFileNameOption] = TargetFileName,
            [TargetDirectoryOption] = TargetDirectory,
        };

    public static RomPlacement? FromOptions(IReadOnlyDictionary<string, string>? options)
    {
        if (options is null
            || !options.TryGetValue(TargetFileNameOption, out var fileName))
        {
            return null;
        }

        options.TryGetValue(TargetDirectoryOption, out var directory);
        return Validate(fileName, directory) is null
            ? Create(fileName, directory)
            : null;
    }

    // Clients install on Windows, so validate against Windows rules whatever the server OS is.
    private static bool HasInvalidCharacters(string value) =>
        value.Any(character => character < ' ' || "<>:\"|?*/\\".Contains(character))
        || IsReservedName(value);

    private static bool IsReservedName(string value)
    {
        var baseName = value.Split('.')[0].TrimEnd().ToUpperInvariant();
        return baseName is "CON" or "PRN" or "AUX" or "NUL"
            || (baseName.Length == 4
                && (baseName.StartsWith("COM", StringComparison.Ordinal)
                    || baseName.StartsWith("LPT", StringComparison.Ordinal))
                && baseName[3] is >= '1' and <= '9');
    }

    private static string NormalizeDirectory(string? directory) =>
        (directory ?? "")
            .Trim()
            .Replace('/', '\\')
            .TrimEnd('\\');
}
