using LANCommander.Community.Server.Quake3.Core;
using Xunit;

namespace LANCommander.Community.Server.Quake3.Tests;

public sealed class CatalogueTests
{
    [Fact]
    public async Task ScansBothRootsLooseAndNonseekablePk3WithoutExtraction()
    {
        using var fixture = new ControlFixture();
        fixture.Files.AddPak("Overlay/baseq3/custom.pk3", "maps/custom.bsp", "maps/q3dm17.bsp",
            "maps/bad;quit.bsp", "maps/nested/hidden.bsp", "Maps/ignored.bsp", "textures/test.tga");
        fixture.Files.Data["Overlay/baseq3/maps/loose.BSP"] = [];
        var result = await fixture.Control.GetCatalogueAsync();
        Assert.Equal(QuakeCatalogueAvailability.Fresh, result.Availability);
        Assert.Equal<string>(["custom", "loose", "q3ctf1", "q3dm17"], result.Maps);
        Assert.True(result.HasKnownCatalogue);
        Assert.Empty(System.IO.Directory.EnumerateFiles(fixture.Directory.Path, "*.pk3", SearchOption.AllDirectories));
        Assert.Empty(System.IO.Directory.EnumerateFiles(fixture.Directory.Path, "*.bsp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CacheUsesServerRootsPathSizeMtimeAndExplicitRefresh()
    {
        using var fixture = new ControlFixture();
        await fixture.Control.GetCatalogueAsync();
        Assert.Equal(1, fixture.Files.Opens);
        await fixture.Control.GetCatalogueAsync();
        Assert.Equal(1, fixture.Files.Opens);
        await fixture.Control.GetCatalogueAsync(true);
        Assert.Equal(2, fixture.Files.Opens);
        fixture.Files.Modified = DateTimeOffset.UtcNow;
        await fixture.Control.GetCatalogueAsync();
        Assert.Equal(3, fixture.Files.Opens);
        fixture.Files.Data["Server/baseq3/renamed.pk3"] = fixture.Files.Data["Server/baseq3/pak0.pk3"];
        fixture.Files.Data.Remove("Server/baseq3/pak0.pk3");
        await fixture.Control.GetCatalogueAsync();
        Assert.Equal(4, fixture.Files.Opens);
        var other = fixture.Factory.Create(Guid.NewGuid(), fixture.Options, fixture.Files, fixture.Rcon);
        await other.GetCatalogueAsync();
        Assert.Equal(5, fixture.Files.Opens);
        var roots = fixture.Factory.Create(fixture.ServerId,
            fixture.Options with { ContentRoots = ["Overlay/baseq3"] }, fixture.Files, fixture.Rcon);
        var empty = await roots.GetCatalogueAsync();
        Assert.Empty(empty.Maps);
        Assert.Equal(QuakeCatalogueAvailability.Fresh, empty.Availability);
    }

    [Fact]
    public async Task UnavailableWithoutKnownSnapshotIsNotEmptySuccess()
    {
        using var fixture = new ControlFixture();
        fixture.Files.Unavailable = true;
        var result = await fixture.Control.GetCatalogueAsync();
        Assert.Equal(QuakeCatalogueAvailability.Unavailable, result.Availability);
        Assert.False(result.HasKnownCatalogue);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task FailedScanKeepsDurableLastKnownAndDoesNotPoisonCache()
    {
        using var fixture = new ControlFixture();
        var first = await fixture.Control.GetCatalogueAsync();
        fixture.Files.AddPak("Server/baseq3/pak0.pk3", "maps/newmap.bsp");
        fixture.Files.FailOpen = true;
        var stale = await fixture.Control.GetCatalogueAsync();
        Assert.Equal(QuakeCatalogueAvailability.Stale, stale.Availability);
        Assert.Equal<string>(first.Maps, stale.Maps);
        Assert.Equal(first.ScannedAt, stale.ScannedAt);
        fixture.Files.FailOpen = false;
        Assert.Equal<string>(["newmap"], (await fixture.Control.GetCatalogueAsync()).Maps);
        fixture.Files.Unavailable = true;
        var recreated = new QuakeControlFactory(new QuakeStateStore(fixture.Directory.Path), fixture.Directory.Path)
            .Create(fixture.ServerId, fixture.Options, fixture.Files, fixture.Rcon);
        Assert.Equal<string>(["newmap"], (await recreated.GetCatalogueAsync()).Maps);
    }

    [Fact]
    public async Task CorruptZipAndOversizedAdvertisedPackAreExplicit()
    {
        using var fixture = new ControlFixture();
        fixture.Files.Data["Server/baseq3/pak0.pk3"] = [1, 2, 3];
        Assert.Equal(QuakeCatalogueAvailability.Unavailable, (await fixture.Control.GetCatalogueAsync()).Availability);
        fixture.Files.MalformedListing = true;
        var opens = fixture.Files.Opens;
        Assert.Equal(QuakeCatalogueAvailability.Unavailable, (await fixture.Control.GetCatalogueAsync()).Availability);
        Assert.Equal(opens, fixture.Files.Opens);
    }

    [Fact]
    public async Task CancellationDoesNotReplaceKnownSnapshot()
    {
        using var fixture = new ControlFixture();
        var before = await fixture.Control.GetCatalogueAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Control.GetCatalogueAsync(true, cancelled.Token));
        Assert.Equal(before.ScannedAt, (await fixture.Control.GetCatalogueAsync()).ScannedAt);
    }

    [Fact]
    public async Task CentralDirectoryBudgetIsCheckedBeforeMaterializingEntries()
    {
        using var fixture = new ControlFixture();
        var bytes = fixture.Files.Data["Server/baseq3/pak0.pk3"];
        // A false oversized central directory must be rejected before ZipArchive attempts allocation.
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 10),
            (uint)QuakeMapCatalogueService.MaximumCentralDirectoryBytes + 1);
        var result = await fixture.Control.GetCatalogueAsync();
        Assert.Equal(QuakeCatalogueAvailability.Unavailable, result.Availability);
        Assert.Empty(System.IO.Directory.EnumerateFiles(fixture.Directory.Path, "*.pk3", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MapCountBudgetDoesNotReplaceKnownSnapshot()
    {
        using var fixture = new ControlFixture();
        var known = await fixture.Control.GetCatalogueAsync();
        for (var i = 0; i <= QuakeMapCatalogueService.MaximumMapCount; i++)
            fixture.Files.Data["Overlay/baseq3/maps/map" + i + ".bsp"] = [];
        Assert.Equal(QuakeCatalogueAvailability.Stale, (await fixture.Control.GetCatalogueAsync()).Availability);
        Assert.Equal<string>(known.Maps, (await fixture.Control.GetStateAsync()).Catalogue!.Maps);
    }

    [Fact]
    public async Task SeekableRemoteStreamsUseSameBoundedScan()
    {
        using var fixture = new ControlFixture();
        fixture.Files.NonSeekable = false;
        Assert.Equal(QuakeCatalogueAvailability.Fresh, (await fixture.Control.GetCatalogueAsync()).Availability);
    }

    [Fact]
    public async Task TotalAdvertisedDownloadAndListingCountsAreBounded()
    {
        using var fixture = new ControlFixture();
        fixture.Files.ListingOverride = root => root.EndsWith("/maps", StringComparison.Ordinal) ? [] :
            Enumerable.Range(0, 3).Select(i => new QuakeFileEntry(root + "/pak" + i + ".pk3",
                QuakeMapCatalogueService.MaximumPk3Bytes, DateTimeOffset.UnixEpoch)).ToArray();
        Assert.Equal(QuakeCatalogueAvailability.Unavailable, (await fixture.Control.GetCatalogueAsync()).Availability);
        Assert.Equal(0, fixture.Files.Opens);
        fixture.Files.ListingOverride = root => Enumerable.Repeat(new QuakeFileEntry(root + "/map.bsp", 0,
            DateTimeOffset.UnixEpoch), QuakeMapCatalogueService.MaximumListedFiles + 1).ToArray();
        Assert.Equal(QuakeCatalogueAvailability.Unavailable, (await fixture.Control.GetCatalogueAsync()).Availability);
        Assert.Equal(0, fixture.Files.Opens);
    }

    [Fact]
    public async Task AdvertisedAndActualLengthMismatchIsFailedScanNotFreshEmpty()
    {
        using var fixture = new ControlFixture();
        var first = await fixture.Control.GetCatalogueAsync();
        fixture.Files.Modified = DateTimeOffset.UtcNow;
        fixture.Files.ReadOverride = stream =>
        {
            stream.Dispose();
            return new ForwardOnlyStream(new MemoryStream([1, 2, 3]));
        };
        var result = await fixture.Control.GetCatalogueAsync();
        Assert.Equal(QuakeCatalogueAvailability.Stale, result.Availability);
        Assert.Equal<string>(first.Maps, result.Maps);
        Assert.Empty(System.IO.Directory.EnumerateFiles(fixture.Directory.Path, "*.pk3", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HostAvailabilityFailuresProduceDiagnosticUnavailableResult(bool notSupported)
    {
        using var fixture = new ControlFixture();
        fixture.Files.ListingOverride = _ => throw (notSupported
            ? new NotSupportedException("Engine cannot list files.")
            : new InvalidOperationException("Engine is stopped."));
        var result = await fixture.Control.GetCatalogueAsync();
        Assert.Equal(QuakeCatalogueAvailability.Unavailable, result.Availability);
        Assert.Contains(notSupported ? "Engine cannot list files." : "Engine is stopped.", result.Error);
        Assert.Contains(notSupported ? nameof(NotSupportedException) : nameof(InvalidOperationException), result.Error);
    }

    [Fact]
    public async Task FailedStagingCleanupIsReportedAndNeverCachesSuccessfulScan()
    {
        using var fixture = new ControlFixture();
        fixture.Files.ReadOverride = stream => new ForwardOnlyStream(stream, () =>
        {
            var stagingRoot = System.IO.Path.Combine(fixture.Directory.Path, "quake3-staging");
            foreach (var file in System.IO.Directory.EnumerateFiles(stagingRoot, "*.pk3"))
            {
                File.Delete(file);
                System.IO.Directory.CreateDirectory(file);
            }
        });
        var result = await fixture.Control.GetCatalogueAsync();
        Assert.Equal(QuakeCatalogueAvailability.Unavailable, result.Availability);
        Assert.Contains("Could not clean up staged Quake map pack.", result.Error);
        Assert.Contains("Cause (", result.Error);
        Assert.Null((await fixture.Control.GetStateAsync()).Catalogue);
        fixture.Files.ReadOverride = null;
        Assert.Equal(QuakeCatalogueAvailability.Fresh, (await fixture.Control.GetCatalogueAsync()).Availability);
    }
}
