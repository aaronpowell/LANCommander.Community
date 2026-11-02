using LANCommander.RecompCatalog.Core;

namespace LANCommander.RecompCatalog.Tests;

public class SelectionTests
{
    [Fact]
    public void FiltersUnsupportedAndNonWindowsAssets()
    {
        var assets = new[]
        {
            Asset("game-win.zip"),
            Asset("game.exe"),
            Asset("game-linux.zip"),
            Asset("Source code.zip"),
            Asset("checksums.txt"),
        };

        var selected = ReleaseAssetSelection.WindowsPackages(assets);

        Assert.Equal(["game-win.zip", "game.exe"], selected.Select(asset => asset.Name));
    }

    [Fact]
    public void RequiresAnExplicitExecutableCandidate()
    {
        var candidates = new[] { "bin/game.exe", "tools/config.exe" };

        Assert.Throws<InvalidOperationException>(() =>
            ExecutableSelection.RequireExplicit(candidates, null));
        Assert.Throws<InvalidOperationException>(() =>
            ExecutableSelection.RequireExplicit(candidates, "other.exe"));
        Assert.Equal("bin/game.exe",
            ExecutableSelection.RequireExplicit(candidates, "BIN/GAME.EXE"));
        Assert.Equal("bin/game.exe",
            ExecutableSelection.RequireExplicit(candidates, @"bin\game.exe"));
    }

    [Fact]
    public void SearchesNameRepositoryAndTags()
    {
        var entries = new[]
        {
            new CatalogEntry
            {
                Name = "Alpha",
                Repository = "owner/alpha",
                FolderName = "Alpha",
                Tags = ["recomp"],
            },
            new CatalogEntry
            {
                Name = "Beta",
                Repository = "owner/beta",
                FolderName = "Beta",
                Tags = ["recreation"],
            },
        };

        Assert.Equal("Alpha", Assert.Single(CatalogSearch.Filter(entries, "recomp")).Name);
        Assert.Equal("Beta", Assert.Single(CatalogSearch.Filter(entries, "owner/beta")).Name);
    }

    private static ReleaseAsset Asset(string name) =>
        new(name, new Uri($"https://example.test/{Uri.EscapeDataString(name)}"), 1, "");
}
