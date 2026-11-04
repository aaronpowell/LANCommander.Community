using LANCommander.Community.Server.Quake3.Core;
using LANCommander.Server.Plugins;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace LANCommander.Community.Server.Quake3.Host.Tests;

public sealed class AdministrationTests
{
    [Fact]
    public async Task UnauthorizedActionsNeverResolveServerOrTouchFiles()
    {
        var provider = new Mock<IServerProvider>(MockBehavior.Strict);
        var service = new Quake3Administration(provider.Object,
            new Quake3Settings(new ConfigurationBuilder().Build()), new Quake3Runtime(),
            new Quake3Authorization(new TestAuthentication("Player", true)));
        var id = Guid.NewGuid();
        var config = MatchConfigurationView.Default;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetServersAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetSnapshotAsync(id, false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ApplyAsync(id, config));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SavePresetAsync(id, "Preset", config));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.NextAsync(id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RestartAsync(id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ChangeMapAsync(id, "q3dm17"));
        provider.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnassociatedServerIsRejectedBeforeHostLookup()
    {
        var provider = new Mock<IServerProvider>(MockBehavior.Strict);
        var service = new Quake3Administration(provider.Object,
            new Quake3Settings(new ConfigurationBuilder().Build()), new Quake3Runtime(),
            new Quake3Authorization(new TestAuthentication("Administrator", true)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestartAsync(Guid.NewGuid()));
        provider.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FileAdapterUsesHostRoutingAndDoesNotOwnHostReadStream()
    {
        var id = Guid.NewGuid();
        var provider = new Mock<IServerProvider>(MockBehavior.Strict);
        var modified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        provider.Setup(p => p.ListFilesAsync(id, "Server/baseq3", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ServerFileInfo("pak0.pk3", "Server/baseq3/pak0.pk3", false, 10, modified)]);
        var stream = new MemoryStream(new byte[10]);
        provider.Setup(p => p.OpenFileAsync(id, "Server/baseq3/pak0.pk3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(stream);
        var adapter = new HostServerFiles(provider.Object);
        var file = Assert.Single(await adapter.ListAsync(id, "Server/baseq3", CancellationToken.None));
        Assert.Equal(10, file.Length);
        Assert.Equal(TimeSpan.Zero, file.LastModified.Offset);
        Assert.Same(stream, await adapter.OpenReadAsync(id, file.Path, CancellationToken.None));
        Assert.True(stream.CanRead);
        stream.Dispose();
    }

    [Fact]
    public async Task MissingOptionalDirectoryDiffersFromUnavailableEngine()
    {
        var id = Guid.NewGuid();
        var provider = new Mock<IServerProvider>();
        provider.Setup(p => p.ListFilesAsync(id, "Overlay/baseq3", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DirectoryNotFoundException());
        provider.Setup(p => p.ListFilesAsync(id, "Server/baseq3", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Engine is stopped."));
        var adapter = new HostServerFiles(provider.Object);
        Assert.Empty(await adapter.ListAsync(id, "Overlay/baseq3", CancellationToken.None));
        await Assert.ThrowsAsync<QuakeFilesUnavailableException>(() =>
            adapter.ListAsync(id, "Server/baseq3", CancellationToken.None));
    }
}
