using Microsoft.Extensions.Configuration;
using Xunit;

namespace LANCommander.Community.Server.Quake3.Host.Tests;

public sealed class SettingsTests
{
    private static Dictionary<string, string?> Valid() => new()
    {
        ["Plugins:Quake3:Servers:0:ServerId"] = Guid.NewGuid().ToString(),
        ["Plugins:Quake3:Servers:0:ContentRoot"] = "",
        ["Plugins:Quake3:Servers:0:StartupConfigPath"] = "Overlay/baseq3/dadlan-admin/server.cfg",
        ["Plugins:Quake3:Servers:0:ExecPath"] = "dadlan-admin/server.cfg",
        ["Plugins:Quake3:Servers:0:RconHost"] = "127.0.0.1",
        ["Plugins:Quake3:Servers:0:RconPort"] = "27600",
        ["Plugins:Quake3:Servers:0:RconPassword"] = new string('x', 32),
    };

    [Theory]
    [InlineData("ContentRoot", "/config")]
    [InlineData("ContentRoot", "../config")]
    [InlineData("StartupConfigPath", "C:\\game\\server.cfg")]
    [InlineData("StartupConfigPath", "foo//server.cfg")]
    [InlineData("ExecPath", "server.cfg;quit")]
    [InlineData("RconPort", "65536")]
    [InlineData("RconHost", "invalid host")]
    [InlineData("RconPassword", "unsafe secret;quit")]
    [InlineData("ServerId", "not-a-guid")]
    public void InvalidAssociationFailsExplicitly(string key, string value)
    {
        var values = Valid();
        values[$"Plugins:Quake3:Servers:0:{key}"] = value;
        Assert.Throws<InvalidDataException>(() =>
            new Quake3Settings(new ConfigurationBuilder().AddInMemoryCollection(values).Build()));
    }

    [Fact]
    public void PathsRespectDistinctGameAndEngineRoots()
    {
        var settings = new Quake3Settings(new ConfigurationBuilder()
            .AddInMemoryCollection(Valid()).Build());
        var server = Assert.Single(settings.Servers).Value;
        Assert.Equal(["Server/baseq3", "Overlay/baseq3"], server.MapRoots);
        Assert.NotEqual(server.StartupConfigPath, server.ExecPath);
    }

    [Fact]
    public void MissingConfigurationDoesNotAssociateOrModifyAnyServer()
    {
        var settings = new Quake3Settings(new ConfigurationBuilder().Build());
        Assert.Empty(settings.Servers);
    }
}
