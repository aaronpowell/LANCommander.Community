using System.IO.Compression;
using LANCommander.RecompCatalog.Infrastructure;

namespace LANCommander.RecompCatalog.Tests;

public class ArtifactNormalizerTests
{
    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("folder/../../escape.exe")]
    [InlineData("/rooted.exe")]
    [InlineData("game.exe.")]
    [InlineData("folder /game.exe")]
    [InlineData("CON.exe")]
    [InlineData("game.exe:payload")]
    public void RejectsUnsafeArchivePaths(string path)
    {
        Assert.Throws<InvalidDataException>(() => ArtifactNormalizer.NormalizeEntryPath(path));
    }

    [Fact]
    public void NormalizesSafeArchivePaths()
    {
        Assert.Equal("folder/game.exe", ArtifactNormalizer.NormalizeEntryPath(@"folder\game.exe"));
    }

    [Fact]
    public async Task NormalizesZipAndFindsExecutables()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var inputPath = Path.Combine(directory, "input.zip");
        var outputPath = Path.Combine(directory, "output.zip");

        try
        {
            using (var archive = ZipFile.Open(inputPath, ZipArchiveMode.Create))
            {
                var executable = archive.CreateEntry("bin/game.exe");
                await using var stream = executable.Open();
                await stream.WriteAsync(new byte[] { 1, 2, 3 });
            }

            var result = await ArtifactNormalizer.NormalizeAsync(
                new()
                {
                    Name = "Game",
                    Repository = "owner/repo",
                    FilesToAdd = ["portable.txt"],
                },
                new("game.zip", new Uri("https://example.test/game.zip"), 3, "application/zip"),
                inputPath,
                outputPath,
                CancellationToken.None);

            Assert.Equal(["bin/game.exe"], result.Executables);
            using var normalized = ZipFile.OpenRead(outputPath);
            Assert.NotNull(normalized.GetEntry("portable.txt"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsExcessiveCompressionRatio()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var inputPath = Path.Combine(directory, "bomb.zip");
        var outputPath = Path.Combine(directory, "output.zip");

        try
        {
            using (var archive = ZipFile.Open(inputPath, ZipArchiveMode.Create))
            {
                var executable = archive.CreateEntry("game.exe", CompressionLevel.SmallestSize);
                await using var stream = executable.Open();
                await stream.WriteAsync(new byte[1024 * 1024]);
            }

            await Assert.ThrowsAsync<InvalidDataException>(() => ArtifactNormalizer.NormalizeAsync(
                new() { Name = "Game", Repository = "owner/repo" },
                new("game.zip", new Uri("https://example.test/game.zip"), 1, "application/zip"),
                inputPath,
                outputPath,
                CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
