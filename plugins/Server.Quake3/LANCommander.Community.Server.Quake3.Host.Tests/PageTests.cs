using Bunit;
using QuakePage = LANCommander.Community.Server.Quake3.Pages.Index;
using LANCommander.Server.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace LANCommander.Community.Server.Quake3.Host.Tests;

public sealed class PageTests
{
    [Fact]
    public void UnconfiguredNativePageRendersWithoutServerMutationOrCredentials()
    {
        using var context = new BunitContext();
        var provider = new Mock<IServerProvider>(MockBehavior.Strict);
        provider.Setup(p => p.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        context.Services.AddLogging();
        context.Services.AddSingleton(provider.Object);
        context.Services.AddSingleton(new Quake3Settings(new ConfigurationBuilder().Build()));
        context.Services.AddSingleton<Quake3Runtime>();
        context.Services.AddSingleton(new Quake3Authorization(new TestAuthentication("Administrator", true)));
        context.Services.AddSingleton<Quake3Administration>();
        var page = context.Render<QuakePage>();
        page.WaitForAssertion(() => Assert.Contains("No associated servers", page.Markup));
        Assert.Empty(page.FindAll("iframe"));
        Assert.Empty(page.FindAll("input[type=password]"));
        Assert.Contains("Docker plugin", page.Markup);
        provider.Verify(p => p.GetAsync(It.IsAny<CancellationToken>()), Times.Once);
        provider.VerifyNoOtherCalls();
    }
}
