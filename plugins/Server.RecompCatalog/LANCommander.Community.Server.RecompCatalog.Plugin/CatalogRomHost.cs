using LANCommander.Community.Server.RecompCatalog.Core;
using LANCommander.Community.Server.RecompCatalog.Infrastructure;
using LANCommander.SDK.Enums;
using LANCommander.Server.Plugins;
using System.Text.Json;

namespace LANCommander.Community.Server.RecompCatalog.Plugin;

/// <summary>Host operations for ROM redistributables created by this plugin.</summary>
public interface ICatalogRomHost
{
    Task<IReadOnlyList<RomRedistributableInfo>> ListRomsAsync(CancellationToken cancellationToken = default);

    Task<RomRedistributableInfo> ImportRomAsync(
        NormalizedRom rom,
        string displayName,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    Task<GameRomLink?> GetGameRomAsync(Guid gameId, CancellationToken cancellationToken = default);

    Task LinkAsync(
        Guid gameId,
        Guid romId,
        RomPlacement placement,
        CancellationToken cancellationToken = default);

    Task UnlinkAsync(Guid gameId, CancellationToken cancellationToken = default);
}

public sealed class CatalogRomHost(
    IRedistributableProvider redistributables,
    IGameVersionProvider versions,
    IPackageImporter importer) : ICatalogRomHost
{
    public async Task<IReadOnlyList<RomRedistributableInfo>> ListRomsAsync(
        CancellationToken cancellationToken = default)
    {
        var all = await redistributables.GetAsync(cancellationToken);

        return all
            .Select(Map)
            .OfType<RomRedistributableInfo>()
            .OrderBy(rom => rom.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<RomRedistributableInfo> ImportRomAsync(
        NormalizedRom rom,
        string displayName,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rom);

        var id = RomRedistributableBuilder.CreateId(rom.Sha256);
        var existing = await redistributables.GetAsync(id, cancellationToken);
        if (existing is not null)
        {
            progress?.Report("This ROM is already a redistributable; reusing it.");
            return Map(existing)
                ?? throw new InvalidOperationException(
                    $"Redistributable {id} exists but was not created by this plugin.");
        }

        var name = RomRedistributableBuilder.CreateName(displayName, rom.Sha256);
        if (await redistributables.FindAsync(name, cancellationToken) is not null)
        {
            // The importer matches by name as well as ID, so a clash would overwrite another redistributable.
            throw new InvalidOperationException(
                $"A different redistributable is already named '{name}'. Rename it and try again.");
        }

        var packagePath = Path.Combine(
            Path.GetDirectoryName(rom.Path) ?? Path.GetTempPath(),
            $"{Guid.NewGuid():N}.lcx");
        try
        {
            progress?.Report("Building ROM redistributable package...");
            var info = await RomRedistributableBuilder.BuildAsync(rom, displayName, packagePath, cancellationToken);

            progress?.Report("Importing ROM redistributable into LANCommander...");
            await using var packageStream = File.OpenRead(packagePath);
            await importer.ImportStreamAsync(
                packageStream,
                manifestType: ManifestType.Redistributable,
                cancellationToken: cancellationToken);

            return info;
        }
        finally
        {
            if (File.Exists(packagePath))
                File.Delete(packagePath);
        }
    }

    public async Task<GameRomLink?> GetGameRomAsync(
        Guid gameId,
        CancellationToken cancellationToken = default)
    {
        var version = await versions.GetLatestAsync(gameId, cancellationToken);
        if (version is null)
            return null;

        var rows = await versions.GetRedistributablesAsync(version.Id, cancellationToken);
        foreach (var row in rows)
        {
            var rom = Map(row.Redistributable);
            if (rom is null)
                continue;

            var placement = RomPlacement.FromOptions(DeserializeOptions(row.Options))
                ?? new RomPlacement("", "");
            return new GameRomLink(rom, placement);
        }

        return null;
    }

    public async Task LinkAsync(
        Guid gameId,
        Guid romId,
        RomPlacement placement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placement);
        var error = RomPlacement.Validate(placement.TargetFileName, placement.TargetDirectory);
        if (error is not null)
            throw new ArgumentException(error, nameof(placement));

        var rom = await redistributables.GetAsync(romId, cancellationToken);
        if (Map(rom) is null)
            throw new InvalidOperationException("The selected ROM redistributable no longer exists.");

        var version = await versions.GetOrCreateLatestAsync(gameId, cancellationToken);
        var rows = await versions.GetRedistributablesAsync(version.Id, cancellationToken);
        await versions.SetRedistributablesAsync(
            version.Id,
            ReplaceRom(rows.Select(row => (row.Redistributable.Id, IsPluginRom(row.Redistributable))), romId),
            cancellationToken);
        await versions.SetRedistributableOptionsAsync(
            version.Id,
            romId,
            JsonSerializer.Serialize(placement.ToOptions()),
            cancellationToken);
    }

    public async Task UnlinkAsync(Guid gameId, CancellationToken cancellationToken = default)
    {
        var version = await versions.GetLatestAsync(gameId, cancellationToken);
        if (version is null)
            return;

        var rows = await versions.GetRedistributablesAsync(version.Id, cancellationToken);
        if (!rows.Any(row => IsPluginRom(row.Redistributable)))
            return;

        await versions.SetRedistributablesAsync(
            version.Id,
            ReplaceRom(rows.Select(row => (row.Redistributable.Id, IsPluginRom(row.Redistributable))), null),
            cancellationToken);
    }

    /// <summary>
    /// Keeps every redistributable that is not a plugin ROM and adds the chosen ROM, so a game has at
    /// most one plugin ROM while redistributables added by hand are preserved.
    /// </summary>
    internal static IReadOnlyList<Guid> ReplaceRom(
        IEnumerable<(Guid Id, bool IsPluginRom)> current,
        Guid? romId)
    {
        var result = current
            .Where(item => !item.IsPluginRom && item.Id != romId)
            .Select(item => item.Id)
            .Distinct()
            .ToList();
        if (romId is { } id)
            result.Add(id);
        return result;
    }

    internal static IReadOnlyDictionary<string, string>? DeserializeOptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsPluginRom(RedistributableInfo? redistributable) => Map(redistributable) is not null;

    private static RomRedistributableInfo? Map(RedistributableInfo? redistributable)
    {
        var sha256 = RomRedistributableBuilder.ReadSha256(redistributable?.Notes);
        return redistributable is null || sha256 is null
            ? null
            : new RomRedistributableInfo(redistributable.Id, redistributable.Name, sha256, redistributable.Description);
    }
}
