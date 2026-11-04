using System.Collections.Immutable;
using LANCommander.Community.Server.Quake3.Core;
using Xunit;

namespace LANCommander.Community.Server.Quake3.Tests;

public sealed class DomainTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void SupportedModesAndBoundaryLimits(int mode)
    {
        var config = new QuakeConfiguration((QuakeGameMode)mode, 120, 1000, 100,
            Enumerable.Repeat("q3dm17", 32).ToImmutableArray());
        config.ValidateInstalled(["q3dm17"]);
        Assert.Equal(32, config.Rotation.Length);
        _ = new QuakeConfiguration((QuakeGameMode)mode, 0, 0, 0, ["q3dm17"]);
    }

    [Theory]
    [InlineData(2, 15, 30, 8)]
    [InlineData(5, 15, 30, 8)]
    [InlineData(0, -1, 30, 8)]
    [InlineData(0, 121, 30, 8)]
    [InlineData(0, 15, -1, 8)]
    [InlineData(0, 15, 1001, 8)]
    [InlineData(0, 15, 30, -1)]
    [InlineData(0, 15, 30, 101)]
    public void RejectsLimitsAndModes(int mode, int time, int frag, int capture) =>
        Assert.Throws<ArgumentException>(() => new QuakeConfiguration((QuakeGameMode)mode, time, frag, capture, ["q3dm17"]));

    [Theory]
    [InlineData("")]
    [InlineData("q3dm17;quit")]
    [InlineData("../q3dm17")]
    [InlineData("q3dm17\n")]
    [InlineData("map name")]
    public void RejectsUnsafeMaps(string map) =>
        Assert.Throws<ArgumentException>(() => new QuakeConfiguration(QuakeGameMode.FreeForAll, 0, 0, 0, [map]));

    [Fact]
    public void RejectsInvalidRotationAndUnknownInstalledMap()
    {
        Assert.Throws<ArgumentException>(() => new QuakeConfiguration(QuakeGameMode.FreeForAll, 0, 0, 0, []));
        Assert.Throws<ArgumentException>(() => new QuakeConfiguration(QuakeGameMode.FreeForAll, 0, 0, 0, default));
        Assert.Throws<ArgumentException>(() => new QuakeConfiguration(QuakeGameMode.FreeForAll, 0, 0, 0,
            Enumerable.Repeat("q3dm17", 33).ToImmutableArray()));
        Assert.Throws<ArgumentException>(() => QuakeConfiguration.Default.ValidateInstalled(["missing"]));
        Assert.False(QuakeNames.IsMap(new string('a', 65)));
        Assert.True(QuakeNames.IsMap(new string('a', 64)));
    }

    [Fact]
    public void RendererMatchesSpecAndWrapsRotation()
    {
        var text = QuakeConfigRenderer.Render(new(QuakeGameMode.CaptureTheFlag, 15, 30, 8, ["q3ctf1", "q3dm17"]));
        Assert.Equal("// dadlan-admin-state: {\"mode\":\"4\",\"timelimit\":15,\"fraglimit\":30,\"capturelimit\":8,\"rotation\":[\"q3ctf1\",\"q3dm17\"]}\n" +
            "set g_gametype 4\nset timelimit 15\nset fraglimit 30\nset capturelimit 8\n" +
            "set dadlan_r0 \"map q3ctf1; set nextmap vstr dadlan_r1\"\n" +
            "set dadlan_r1 \"map q3dm17; set nextmap vstr dadlan_r0\"\nvstr dadlan_r0\n", text);
        Assert.Equal("// dadlan-admin-state: null\n", QuakeConfigRenderer.Render(null));
        Assert.Contains("nextmap vstr dadlan_r0", QuakeConfigRenderer.Render(QuakeConfiguration.Default));
    }

    [Fact]
    public void SettingsAndExecRejectInjectionWithoutExposingSecrets()
    {
        const string secret = "bad secret;quit";
        Assert.DoesNotContain(secret, Assert.Throws<ArgumentException>(() => new RconSettings("localhost", 27600, secret)).Message);
        Assert.Throws<ArgumentException>(() => new RconSettings("host;quit", 27600, new('a', 24)));
        Assert.Throws<ArgumentException>(() => new RconSettings("localhost", 0, new('a', 24)));
        using var fixture = new ControlFixture();
        foreach (var path in new[] { "../server.cfg", "/server.cfg", "a//b", "server.cfg;quit", "a\nb", "a\\b" })
            Assert.Throws<ArgumentException>(() => fixture.Factory.Create(fixture.ServerId, fixture.Options with { ExecPath = path }, fixture.Files));
        Assert.DoesNotContain(fixture.Options.Rcon.Password, fixture.Options.ToString());
    }
}
