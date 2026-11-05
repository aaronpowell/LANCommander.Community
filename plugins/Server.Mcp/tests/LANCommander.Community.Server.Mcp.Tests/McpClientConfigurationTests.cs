using System.Text.Json;
using LANCommander.Community.Server.Mcp;
using Xunit;

namespace LANCommander.Community.Server.Mcp.Tests;

public sealed class McpClientConfigurationTests
{
    [Theory]
    [InlineData("http://localhost/", "http://localhost/api/Plugins/Mcp")]
    [InlineData("https://games.example.com", "https://games.example.com/api/Plugins/Mcp")]
    [InlineData("https://games.example.com/lc", "https://games.example.com/lc/api/Plugins/Mcp")]
    [InlineData("https://games.example.com/lc/", "https://games.example.com/lc/api/Plugins/Mcp")]
    [InlineData("  https://games.example.com:8443/lc/  ", "https://games.example.com:8443/lc/api/Plugins/Mcp")]
    [InlineData("https://games.example.com:443/", "https://games.example.com/api/Plugins/Mcp")]
    [InlineData("http://[2001:db8::1]:1337/", "http://[2001:db8::1]:1337/api/Plugins/Mcp")]
    [InlineData("https://spiele.bücher.example/", "https://spiele.xn--bcher-kva.example/api/Plugins/Mcp")]
    [InlineData("https://games.example.com/my lc/", "https://games.example.com/my%20lc/api/Plugins/Mcp")]
    [InlineData("https://games.example.com/a\"b$c`d'e!f/", "https://games.example.com/a%22b%24c%60d%27e%21f/api/Plugins/Mcp")]
    public void EndpointUsesBaseAddressIncludingPathBase(string baseAddress, string expected)
    {
        Assert.True(McpClientConfiguration.TryCreateEndpoint(baseAddress, out var endpoint, out var error));
        Assert.Null(error);
        Assert.Equal(expected, endpoint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("games.example.com")]
    [InlineData("/lc/")]
    [InlineData("ftp://games.example.com/")]
    [InlineData("file:///c:/lc/")]
    [InlineData("https://admin:secret@games.example.com/")]
    [InlineData("https://games.example.com/?token=abc")]
    [InlineData("https://games.example.com/#token")]
    public void EndpointRejectsUnsafeOrNonHttpAddresses(string? baseAddress)
    {
        Assert.False(McpClientConfiguration.TryCreateEndpoint(baseAddress, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        if (baseAddress is not null)
        {
            Assert.DoesNotContain("secret", error);
            Assert.DoesNotContain("abc", error);
        }
    }

    [Theory]
    [InlineData("http://localhost/api/Plugins/Mcp", McpEndpointReachability.LocalOnly)]
    [InlineData("https://localhost:5001/api/Plugins/Mcp", McpEndpointReachability.LocalOnly)]
    [InlineData("http://127.0.0.1/api/Plugins/Mcp", McpEndpointReachability.LocalOnly)]
    [InlineData("http://[::1]/api/Plugins/Mcp", McpEndpointReachability.LocalOnly)]
    [InlineData("http://0.0.0.0/api/Plugins/Mcp", McpEndpointReachability.LocalOnly)]
    [InlineData("http://lc.localhost/api/Plugins/Mcp", McpEndpointReachability.LocalOnly)]
    [InlineData("http://192.168.1.10/api/Plugins/Mcp", McpEndpointReachability.InsecureRemote)]
    [InlineData("https://games.example.com/api/Plugins/Mcp", McpEndpointReachability.Remote)]
    public void ClassifiesEndpointReachability(string endpoint, McpEndpointReachability expected) =>
        Assert.Equal(expected, McpClientConfiguration.Classify(endpoint));

    private const string Endpoint = "https://games.example.com/lc/api/Plugins/Mcp";

    [Fact]
    public void CopilotCliSnippetsUseHttpTransportAndPlaceholderHeader()
    {
        Assert.Equal(
            "copilot mcp add --transport http --header \"Authorization: Bearer YOUR_LANCOMMANDER_TOKEN\" lancommander \"" + Endpoint + "\"",
            McpClientConfiguration.CopilotCliCommand(Endpoint));

        using var json = JsonDocument.Parse(McpClientConfiguration.CopilotCliConfiguration(Endpoint));
        var server = json.RootElement.GetProperty("mcpServers").GetProperty("lancommander");
        Assert.Equal("http", server.GetProperty("type").GetString());
        Assert.Equal(Endpoint, server.GetProperty("url").GetString());
        Assert.Equal("Bearer YOUR_LANCOMMANDER_TOKEN", server.GetProperty("headers").GetProperty("Authorization").GetString());
        Assert.Equal("*", Assert.Single(server.GetProperty("tools").EnumerateArray()).GetString());
    }

    [Fact]
    public void ClaudeCodeSnippetsUseHttpTransportAndEnvironmentExpansion()
    {
        Assert.Equal(
            "claude mcp add --transport http --scope user lancommander \"" + Endpoint + "\" --header \"Authorization: Bearer YOUR_LANCOMMANDER_TOKEN\"",
            McpClientConfiguration.ClaudeCodeCommand(Endpoint));

        using var json = JsonDocument.Parse(McpClientConfiguration.ClaudeCodeProjectConfiguration(Endpoint));
        var server = json.RootElement.GetProperty("mcpServers").GetProperty("lancommander");
        Assert.Equal("http", server.GetProperty("type").GetString());
        Assert.Equal(Endpoint, server.GetProperty("url").GetString());
        Assert.Equal("Bearer ${LANCOMMANDER_TOKEN}", server.GetProperty("headers").GetProperty("Authorization").GetString());
    }

    [Fact]
    public void CodexSnippetsReadBearerTokenFromEnvironment()
    {
        Assert.Equal(
            "codex mcp add lancommander --url \"" + Endpoint + "\" --bearer-token-env-var LANCOMMANDER_TOKEN",
            McpClientConfiguration.CodexCommand(Endpoint));
        Assert.Equal(
            "[mcp_servers.lancommander]\nurl = \"" + Endpoint + "\"\nbearer_token_env_var = \"LANCOMMANDER_TOKEN\"\n",
            McpClientConfiguration.CodexConfiguration(Endpoint));
    }

    [Fact]
    public void TomlAndJsonEscapeUnusualCharacters()
    {
        const string unusual = "https://example.com/a\"b\\c\n/api/Plugins/Mcp";
        Assert.Contains("url = \"https://example.com/a\\\"b\\\\c\\u000A/api/Plugins/Mcp\"",
            McpClientConfiguration.CodexConfiguration(unusual));

        using var json = JsonDocument.Parse(McpClientConfiguration.ClaudeCodeProjectConfiguration(unusual));
        Assert.Equal(unusual, json.RootElement.GetProperty("mcpServers").GetProperty("lancommander").GetProperty("url").GetString());
    }

    [Fact]
    public void VsCodeConfigurationPromptsForTokenWithoutEmbeddingIt()
    {
        using var json = JsonDocument.Parse(McpClientConfiguration.VsCodeConfiguration(Endpoint));
        var input = Assert.Single(json.RootElement.GetProperty("inputs").EnumerateArray());
        Assert.Equal("lancommander-token", input.GetProperty("id").GetString());
        Assert.Equal("promptString", input.GetProperty("type").GetString());
        Assert.True(input.GetProperty("password").GetBoolean());
        var server = json.RootElement.GetProperty("servers").GetProperty("lancommander");
        Assert.Equal("http", server.GetProperty("type").GetString());
        Assert.Equal(Endpoint, server.GetProperty("url").GetString());
        Assert.Equal("Bearer ${input:lancommander-token}", server.GetProperty("headers").GetProperty("Authorization").GetString());
    }

    [Fact]
    public void ShellVariableSnippetsUsePlaceholders()
    {
        Assert.Equal("export LANCOMMANDER_TOKEN='YOUR_LANCOMMANDER_TOKEN'", McpClientConfiguration.BashTokenVariable);
        Assert.Equal("$env:LANCOMMANDER_TOKEN = 'YOUR_LANCOMMANDER_TOKEN'", McpClientConfiguration.PowerShellTokenVariable);
    }
}
