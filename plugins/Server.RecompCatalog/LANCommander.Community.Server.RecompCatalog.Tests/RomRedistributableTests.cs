using System.IO.Compression;
using System.Security.Cryptography;
using LANCommander.Community.Server.RecompCatalog.Core;
using LANCommander.Community.Server.RecompCatalog.Infrastructure;
using LANCommander.Community.Server.RecompCatalog.Plugin;
using LANCommander.SDK.Enums;
using LANCommander.SDK.Helpers;
using LANCommander.SDK.Models.Manifest;

namespace LANCommander.Community.Server.RecompCatalog.Tests;

public class RomRedistributableTests
{
    private static readonly byte[] Z64Header = [0x80, 0x37, 0x12, 0x40];

    [Theory]
    [InlineData("mm.n64.us.1.0.z64", "")]
    [InlineData("rom.z64", @"bin\data")]
    [InlineData("rom.z64", "bin/data/")]
    public void AcceptsSafePlacements(string fileName, string directory)
    {
        Assert.Null(RomPlacement.Validate(fileName, directory));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(@"..\rom.z64", "")]
    [InlineData("..", "")]
    [InlineData("rom.z64", @"..\outside")]
    [InlineData("rom.z64", @"C:\Games")]
    [InlineData("rom.z64", @"\rooted")]
    [InlineData("rom.z64", "/rooted")]
    [InlineData("rom?.z64", "")]
    [InlineData("CON.z64", "")]
    [InlineData("rom.z64", @"bin\COM1")]
    [InlineData("rom.z64.", "")]
    [InlineData("rom.z64", @"bin\.\data")]
    [InlineData("rom.z64", @"bin\\data")]
    public void RejectsUnsafePlacements(string fileName, string directory)
    {
        Assert.NotNull(RomPlacement.Validate(fileName, directory));
        Assert.Throws<ArgumentException>(() => RomPlacement.Create(fileName, directory));
    }

    [Fact]
    public void PlacementRoundTripsThroughOptionsWithNormalizedDirectory()
    {
        var placement = RomPlacement.Create(" rom.z64 ", "bin/data/");

        Assert.Equal("rom.z64", placement.TargetFileName);
        Assert.Equal(@"bin\data", placement.TargetDirectory);
        Assert.Equal(placement, RomPlacement.FromOptions(placement.ToOptions()));
        Assert.Null(RomPlacement.FromOptions(new Dictionary<string, string>
        {
            [RomPlacement.TargetFileNameOption] = "rom.z64",
            [RomPlacement.TargetDirectoryOption] = @"..\escape",
        }));
    }

    [Theory]
    [InlineData("Zelda64Recompiled.exe", "")]
    [InlineData(@"bin\Zelda64Recompiled.exe", "bin")]
    [InlineData("bin/x64/game.exe", @"bin\x64")]
    [InlineData(null, "")]
    public void DefaultsDirectoryToExecutableFolder(string? executable, string expected)
    {
        Assert.Equal(expected, RomPlacement.DirectoryForExecutable(executable));
    }

    [Theory]
    [InlineData(new byte[] { 0x80, 0x37, 0x12, 0x40 }, RomByteOrder.N64BigEndian)]
    [InlineData(new byte[] { 0x37, 0x80, 0x40, 0x12 }, RomByteOrder.N64ByteSwapped)]
    [InlineData(new byte[] { 0x40, 0x12, 0x37, 0x80 }, RomByteOrder.N64LittleEndian)]
    [InlineData(new byte[] { 0x01, 0x02, 0x03, 0x04 }, RomByteOrder.Unknown)]
    public void DetectsByteOrder(byte[] header, RomByteOrder expected)
    {
        Assert.Equal(expected, RomNormalizer.DetectByteOrder(header));
    }

    [Theory]
    [InlineData(RomByteOrder.N64BigEndian)]
    [InlineData(RomByteOrder.N64ByteSwapped)]
    [InlineData(RomByteOrder.N64LittleEndian)]
    public async Task NormalizesEveryByteOrderToBigEndianWithHashes(RomByteOrder order)
    {
        var canonical = CreateCanonicalRom(3 * 1024 * 1024 + 8);
        var source = Encode(canonical, order);
        using var directory = new TemporaryDirectory();

        var rom = await RomNormalizer.NormalizeAsync(new MemoryStream(source), "game.v64", directory.Path);

        Assert.Equal(canonical, await File.ReadAllBytesAsync(rom.Path));
        Assert.Equal(order, rom.SourceByteOrder);
        Assert.Equal(order != RomByteOrder.N64BigEndian, rom.WasByteSwapped);
        Assert.Equal(canonical.Length, rom.Size);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(canonical)), rom.Sha256);
        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(canonical)), rom.Sha1);
        Assert.Equal("game.v64", rom.OriginalFileName);
    }

    [Fact]
    public async Task PassesUnknownFormatsThroughUnchanged()
    {
        var data = Enumerable.Range(0, 4099).Select(value => (byte)(value * 7)).ToArray();
        data[0] = 0x01;
        using var directory = new TemporaryDirectory();

        var rom = await RomNormalizer.NormalizeAsync(new MemoryStream(data), "game.bin", directory.Path);

        Assert.Equal(RomByteOrder.Unknown, rom.SourceByteOrder);
        Assert.Equal(data, await File.ReadAllBytesAsync(rom.Path));
    }

    [Fact]
    public async Task RejectsEmptyOversizedAndMisalignedRoms()
    {
        using var directory = new TemporaryDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            RomNormalizer.NormalizeAsync(new MemoryStream([]), "empty.z64", directory.Path));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            RomNormalizer.NormalizeAsync(new MemoryStream(new byte[64]), "big.z64", directory.Path, maxBytes: 32));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            RomNormalizer.NormalizeAsync(new MemoryStream([0x37, 0x80, 0x40, 0x12, 0x00]), "odd.v64", directory.Path));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            RomNormalizer.NormalizeAsync(new MemoryStream([0x40, 0x12, 0x37, 0x80, 0x00, 0x00]), "odd.n64", directory.Path));

        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task BuildsImportableRedistributableLcx()
    {
        using var directory = new TemporaryDirectory();
        var canonical = CreateCanonicalRom(4096);
        var rom = await RomNormalizer.NormalizeAsync(
            new MemoryStream(Encode(canonical, RomByteOrder.N64ByteSwapped)),
            "Majora.v64",
            Path.Combine(directory.Path, "rom"));
        var outputPath = Path.Combine(directory.Path, "rom.lcx");

        var info = await RomRedistributableBuilder.BuildAsync(rom, "Majora's Mask (US)", outputPath);

        Assert.Equal(RomRedistributableBuilder.CreateId(rom.Sha256), info.Id);
        Assert.Equal(RomRedistributableBuilder.CreateId(rom.Sha256.ToUpperInvariant()), info.Id);
        Assert.Equal($"ROM: Majora's Mask (US) [{rom.Sha256[..12]}]", info.Name);

        using var package = ZipFile.OpenRead(outputPath);
        using var manifestReader = new StreamReader(package.GetEntry(ManifestHelper.ManifestFilename)!.Open());
        var manifest = ManifestHelper.Deserialize<Redistributable>(await manifestReader.ReadToEndAsync());

        Assert.Equal(info.Id, manifest.Id);
        Assert.Equal(info.Name, manifest.Name);
        Assert.Equal(RomRedistributableBuilder.OptionSchema, manifest.OptionSchema, ignoreLineEndingDifferences: true);
        Assert.Equal(rom.Sha256, RomRedistributableBuilder.ReadSha256(manifest.Notes));
        Assert.Contains($"SHA-1: {rom.Sha1}", manifest.Notes);
        Assert.Contains("Source byte order: N64ByteSwapped", manifest.Notes);

        var archive = Assert.Single(manifest.Archives);
        var archiveEntry = package.GetEntry($"Archives/{archive.Id}");
        Assert.NotNull(archiveEntry);
        using (var archiveZip = new ZipArchive(archiveEntry.Open()))
        {
            var romEntry = Assert.Single(archiveZip.Entries);
            Assert.Equal(RomNormalizer.NormalizedFileName, romEntry.FullName);
            using var romStream = new MemoryStream();
            await romEntry.Open().CopyToAsync(romStream);
            Assert.Equal(canonical, romStream.ToArray());
        }

        Assert.Equal(2, manifest.Scripts.Count);
        foreach (var script in manifest.Scripts)
        {
            Assert.Contains(script.Type, new[] { ScriptType.Install, ScriptType.DetectInstall });
            using var reader = new StreamReader(package.GetEntry($"Scripts/{script.Id}")!.Open());
            var contents = await reader.ReadToEndAsync();
            Assert.Contains(rom.Sha256, contents);
            Assert.Contains("Get-RedistributableOptions", contents);
            Assert.Contains(RomPlacement.TargetFileNameOption, contents);
        }
    }

    [Fact]
    public void OnlyReadsHashesFromPluginNotes()
    {
        Assert.Null(RomRedistributableBuilder.ReadSha256(null));
        Assert.Null(RomRedistributableBuilder.ReadSha256("SHA-256: abc"));
        Assert.Equal("abc", RomRedistributableBuilder.ReadSha256($"{RomRedistributableBuilder.NotesMarker}\nSHA-256: abc\n"));
    }

    [Fact]
    public void ReplacingRomKeepsOtherRedistributablesAndAllowsOneRom()
    {
        var directX = Guid.NewGuid();
        var oldRom = Guid.NewGuid();
        var otherRom = Guid.NewGuid();
        var newRom = Guid.NewGuid();
        var current = new[] { (directX, false), (oldRom, true), (otherRom, true) };

        Assert.Equal([directX, newRom], CatalogRomHost.ReplaceRom(current, newRom));
        Assert.Equal([directX], CatalogRomHost.ReplaceRom(current, null));
        Assert.Equal([directX, oldRom], CatalogRomHost.ReplaceRom([(directX, false), (oldRom, true)], oldRom));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public void IgnoresMissingOrInvalidOptions(string? json)
    {
        Assert.Null(CatalogRomHost.DeserializeOptions(json));
    }

    private static byte[] CreateCanonicalRom(int length)
    {
        var data = new byte[length];
        for (var index = 0; index < data.Length; index++)
            data[index] = (byte)(index * 31 + 7);
        Z64Header.CopyTo(data, 0);
        return data;
    }

    private static byte[] Encode(byte[] canonical, RomByteOrder order)
    {
        var data = (byte[])canonical.Clone();
        switch (order)
        {
            case RomByteOrder.N64ByteSwapped:
                for (var index = 0; index + 1 < data.Length; index += 2)
                    (data[index], data[index + 1]) = (data[index + 1], data[index]);
                break;
            case RomByteOrder.N64LittleEndian:
                for (var index = 0; index + 3 < data.Length; index += 4)
                    Array.Reverse(data, index, 4);
                break;
        }

        return data;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
