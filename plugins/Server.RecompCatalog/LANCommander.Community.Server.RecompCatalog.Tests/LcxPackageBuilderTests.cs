using System.IO.Compression;
using LANCommander.Community.Server.RecompCatalog.Core;
using LANCommander.Community.Server.RecompCatalog.Infrastructure;
using LANCommander.SDK.Helpers;
using LANCommander.SDK.Models.Manifest;

namespace LANCommander.Community.Server.RecompCatalog.Tests;

public class LcxPackageBuilderTests
{
    [Theory]
    [InlineData("..", "Game")]
    [InlineData(@"..\outside", "outside")]
    [InlineData(@"nested/folder", "nestedfolder")]
    [InlineData("CON", "_CON")]
    public void ProducesSafeSingleSegmentDirectoryNames(string value, string expected)
    {
        Assert.Equal(expected, LcxPackageBuilder.SanitizeDirectoryName(value));
    }

    [Fact]
    public async Task BuildsImportableLcxWithStableIdsAndProvenance()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var normalizedPath = Path.Combine(directory, "normalized.zip");
        var outputPath = Path.Combine(directory, "game.lcx");

        try
        {
            using (var normalized = ZipFile.Open(normalizedPath, ZipArchiveMode.Create))
            {
                var executable = normalized.CreateEntry("bin/game.exe");
                await using var content = executable.Open();
                await content.WriteAsync(new byte[] { 1, 2, 3 });
            }

            var entry = new CatalogEntry
            {
                Name = "Test Game",
                Project = "Test Recomp",
                Repository = "owner/repository",
                FolderName = "TestGame-TestRecomp",
            };
            var release = new ReleaseInfo("v1.2.3", "Release", DateTimeOffset.UtcNow, []);
            var asset = new ReleaseAsset(
                "game-win.zip",
                new Uri("https://example.test/game-win.zip"),
                3,
                "application/zip");
            var artifact = new NormalizedArtifact(
                normalizedPath,
                "0123456789abcdef",
                new FileInfo(normalizedPath).Length,
                3,
                ["bin/game.exe"]);
            var catalogIdentity = CatalogIdentity.Create(
                new Uri("https://catalog.test/index.json"),
                "test",
                new Uri("https://catalog.test/list.json"),
                entry);
            var releaseIdentity = ReleaseIdentity.Create(catalogIdentity, release, asset);
            var provenance = new CatalogImportProvenance(
                StableId.Create("game:owner/repository:Test Game"),
                entry.Name,
                catalogIdentity,
                release.Tag,
                releaseIdentity,
                new PackageUpdatePolicy(
                    "github",
                    entry.Repository,
                    null,
                    asset.Name,
                    "zip",
                    true,
                    "bin/game.exe"));
            var metadata = new EditableMetadataDraft(
                "PCGamingWiki",
                "Test Game",
                "Edited Test Game",
                "An edited description.",
                new DateTime(2024, 4, 5),
                false,
                "Recomp Engine",
                "Developer One, Developer Two",
                "Publisher One",
                "Adventure, Action",
                "Recomp, Community",
                "PCGamingWiki: Test Game, IGDB: 123");

            var result = await new LcxPackageBuilder().BuildAsync(
                new PackageRequest(
                    entry,
                    release,
                    asset,
                    artifact,
                    "bin/game.exe",
                    outputPath,
                    provenance,
                    metadata));

            Assert.True(File.Exists(outputPath));
            Assert.Equal(StableId.Create("game:owner/repository:Test Game"), result.GameId);

            using var package = ZipFile.OpenRead(outputPath);
            Assert.NotNull(package.GetEntry($"Archives/{result.ArchiveId}"));
            var manifestEntry = package.GetEntry(ManifestHelper.ManifestFilename);
            Assert.NotNull(manifestEntry);
            await using var manifestStream = manifestEntry.Open();
            using var reader = new StreamReader(manifestStream);
            var manifest = ManifestHelper.Deserialize<Game>(await reader.ReadToEndAsync());

            Assert.Equal(result.GameId, manifest.Id);
            Assert.Equal("Edited Test Game", manifest.Title);
            Assert.Equal("An edited description.", manifest.Description);
            Assert.Equal(new DateTime(2024, 4, 5), manifest.ReleasedOn);
            Assert.False(manifest.Singleplayer);
            Assert.Equal("Recomp Engine", manifest.Engine.Name);
            Assert.Equal(["Developer One", "Developer Two"], manifest.Developers.Select(value => value.Name));
            Assert.Equal(["Publisher One"], manifest.Publishers.Select(value => value.Name));
            Assert.Equal(["Adventure", "Action"], manifest.Genres.Select(value => value.Name));
            Assert.Equal(["Recomp", "Community"], manifest.Tags.Select(value => value.Name));
            Assert.Contains(
                manifest.ExternalIds,
                value => value.Provider == "PCGamingWiki" && value.ExternalId == "Test Game");
            Assert.Contains(
                manifest.ExternalIds,
                value => value.Provider == "IGDB" && value.ExternalId == "123");
            Assert.Equal("v1.2.3", manifest.Version);
            Assert.Contains("owner/repository", manifest.Notes);
            Assert.Equal(Path.Combine("bin", "game.exe"), Assert.Single(manifest.Actions).Path);
            Assert.Equal(LANCommander.SDK.Enums.ActionType.Primary, Assert.Single(manifest.Actions).Type);
            Assert.Equal(result.ArchiveId, Assert.Single(manifest.Archives).Id);
            Assert.Contains(
                manifest.CustomFields,
                field => field.Name == "RecompCatalog.Identity"
                    && field.Value == catalogIdentity.Value);
            Assert.Contains(
                manifest.CustomFields,
                field => field.Name == "RecompCatalog.Release"
                    && field.Value == releaseIdentity.Value);
            Assert.Contains(
                manifest.CustomFields,
                field => field.Name == "RecompCatalog.UpdatePolicy"
                    && field.Value.Contains(asset.Name, StringComparison.Ordinal));
            var packageScript = Assert.Single(
                manifest.Scripts,
                script => script.Type == LANCommander.SDK.Enums.ScriptType.Package);
            var packageScriptEntry = package.GetEntry($"Scripts/{packageScript.Id}");
            Assert.NotNull(packageScriptEntry);
            await using var packageScriptStream = packageScriptEntry.Open();
            using var packageScriptReader = new StreamReader(packageScriptStream);
            var packageScriptContents = await packageScriptReader.ReadToEndAsync();
            Assert.Contains("owner/repository", packageScriptContents, StringComparison.Ordinal);
            Assert.Contains("$Game.Archives", packageScriptContents, StringComparison.Ordinal);
            Assert.Contains("$latestArchive.Version", packageScriptContents, StringComparison.Ordinal);
            Assert.Contains(
                "if ($latestArchive -and $version -eq [string] $latestArchive.Version) {",
                packageScriptContents,
                StringComparison.Ordinal);
            Assert.DoesNotContain("$Game.Version", packageScriptContents, StringComparison.Ordinal);
            Assert.Contains("New-Package", packageScriptContents, StringComparison.Ordinal);
            Assert.Contains("bin/game.exe", packageScriptContents, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
