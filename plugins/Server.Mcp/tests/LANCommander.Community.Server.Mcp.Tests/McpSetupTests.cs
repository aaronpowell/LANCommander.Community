using Bunit;
using System.Reflection;
using System.Text.Json;
using LANCommander.Community.Server.Mcp;
using LANCommander.Community.Server.Mcp.Pages;
using LANCommander.Server.UI.Controls;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Xunit;

namespace LANCommander.Community.Server.Mcp.Tests;

public sealed class McpSetupTests : BunitContext
{
    public McpSetupTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        ComponentFactories.AddStub<PageHeader>();
        ComponentFactories.AddStub<PageContent>(parameters => parameters.Get(x => x.ChildContent) ?? (_ => { }));
        ComponentFactories.AddStub<Flex>(parameters => parameters.Get(x => x.ChildContent) ?? (_ => { }));
        ComponentFactories.AddStub<Alert>(parameters => parameters.Get(x => x.ChildContent) ?? (_ => { }));
        ComponentFactories.AddStub<Checkbox>();
        ComponentFactories.AddStub<Button>(parameters => builder =>
        {
            builder.OpenElement(0, "button");
            if (parameters.TryGetValue("aria-label", out var label))
                builder.AddAttribute(1, "aria-label", label);
            if (parameters.TryGetValue(nameof(Button.OnClick), out var onClick))
                builder.AddAttribute(2, "onclick", (EventCallback<MouseEventArgs>)onClick);
            if (parameters.TryGetValue(nameof(Button.ChildContent), out var content))
                builder.AddContent(3, (RenderFragment)content);
            builder.CloseElement();
        });
        ComponentFactories.AddStub<Tabs>(parameters => builder =>
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "tabs-stub");
            builder.AddAttribute(2, "data-render-all", parameters.Get(x => x.RenderAll).ToString());
            builder.AddContent(3, parameters.Get(x => x.ChildContent));
            builder.CloseElement();
        });
        ComponentFactories.AddStub<Tab>(parameters => builder =>
        {
            builder.OpenElement(0, "section");
            builder.AddAttribute(1, "class", "tab-stub");
            builder.OpenElement(2, "h3");
            builder.AddAttribute(3, "class", "tab-title");
            builder.AddContent(4, parameters.Get(x => x.Title));
            builder.CloseElement();
            builder.AddContent(5, parameters.Get(x => x.ChildContent));
            builder.CloseElement();
        });
    }

    [Fact]
    public void PluginReportsTheFullAssemblyInformationalVersion()
    {
        var assembly = typeof(McpPlugin).Assembly;
        var expected = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        Assert.Equal(expected, new McpPlugin().Version);
    }

    [Fact]
    public void InitialRenderDoesNotGenerateTokenAndExplainsWarning()
    {
        var cut = Render<McpSetup>();
        Assert.Contains("replaces this account's refresh token", cut.Markup);
        Assert.Equal("password", cut.Find("input[name=password]").GetAttribute("type"));
        Assert.Empty(cut.FindAll("input[aria-label='Access token']"));
        Assert.Empty(JSInterop.Invocations);
    }

    [Fact]
    public async Task GenerationRequiresAcknowledgmentAndTokenCanBeCleared()
    {
        var module = JSInterop.SetupModule("http://localhost/api/Plugins/Mcp/setup.mjs");
        module.Setup<McpSetup.TokenResult>("login", _ => true)
            .SetResult(new("test-access-token", DateTimeOffset.UtcNow.AddHours(1)));
        var cut = Render<McpSetup>();
        await cut.Find("form").SubmitAsync();
        Assert.Empty(cut.FindAll("input[aria-label='Access token']"));
        Assert.Empty(JSInterop.Invocations);

        var checkbox = cut.FindComponent<Bunit.TestDoubles.Stub<Checkbox>>();
        await cut.InvokeAsync(() => checkbox.Instance.Parameters.Get(x => x.ValueChanged).InvokeAsync(true));
        await cut.Find("form").SubmitAsync();
        Assert.Equal("test-access-token", cut.Find("input[aria-label='Access token']").GetAttribute("value"));
        Assert.Equal("password", cut.Find("input[aria-label='Access token']").GetAttribute("type"));
        module.SetupVoid("copy", _ => true).SetVoidResult();
        await ButtonByText(cut, "Copy VS Code configuration with token").ClickAsync(new MouseEventArgs());
        var copy = Assert.Single(module.Invocations["copy"]);
        using var configuration = JsonDocument.Parse(Assert.IsType<string>(copy.Arguments[0]));
        var server = configuration.RootElement.GetProperty("servers").GetProperty("lancommander");
        Assert.Equal("http://localhost/api/Plugins/Mcp", server.GetProperty("url").GetString());
        Assert.Equal("Bearer test-access-token", server.GetProperty("headers").GetProperty("Authorization").GetString());
        Assert.Contains("Copied VS Code configuration with token to clipboard.", cut.Markup);
        foreach (var snippet in cut.FindAll("pre code"))
            Assert.DoesNotContain("test-access-token", snippet.TextContent);
        await ButtonByText(cut, "Clear token").ClickAsync(new MouseEventArgs());
        Assert.Empty(cut.FindAll("input[aria-label='Access token']"));
        Assert.DoesNotContain("test-access-token", cut.Markup);
    }

    [Fact]
    public async Task LoginFailureIsVisibleWithoutEchoingCredentials()
    {
        var module = JSInterop.SetupModule("http://localhost/api/Plugins/Mcp/setup.mjs");
        module.Setup<McpSetup.TokenResult>("login", _ => true)
            .SetException(new JSException("Do not display secret details"));
        var cut = Render<McpSetup>();
        var checkbox = cut.FindComponent<Bunit.TestDoubles.Stub<Checkbox>>();
        await cut.InvokeAsync(() => checkbox.Instance.Parameters.Get(x => x.ValueChanged).InvokeAsync(true));
        await cut.Find("form").SubmitAsync();
        Assert.Contains("Could not generate a token", cut.Markup);
        Assert.DoesNotContain("Do not display secret details", cut.Markup);
        Assert.Empty(cut.FindAll("input[aria-label='Access token']"));
    }

    [Fact]
    public void ClientTabsRenderInOrderWithPlaceholderSnippets()
    {
        var cut = Render<McpSetup>();
        Assert.Equal("True", cut.Find(".tabs-stub").GetAttribute("data-render-all"));
        Assert.Equal(
            ["GitHub Copilot app", "Claude Code", "Codex", "General"],
            cut.FindAll(".tab-title").Select(x => x.TextContent));

        var tabs = cut.FindAll(".tab-stub");
        Assert.Contains("copilot mcp add --transport http", tabs[0].TextContent);
        Assert.Contains("Customize", tabs[0].TextContent);
        Assert.DoesNotContain(".vscode/mcp.json", tabs[0].TextContent);
        Assert.Contains("not verified that the form accepts an HTTP header", tabs[0].TextContent);
        Assert.Contains("Alternative: configure it through Copilot CLI", tabs[0].TextContent);
        Assert.Contains("check that LANCommander tools work in the app", Normalize(tabs[0].TextContent));
        Assert.Contains("does not say which environment the ChatGPT desktop app reads", Normalize(tabs[2].TextContent));
        Assert.Contains("claude mcp add --transport http --scope user lancommander \"http://localhost/api/Plugins/Mcp\"", tabs[1].TextContent);
        Assert.Contains("${LANCOMMANDER_TOKEN}", tabs[1].TextContent);
        Assert.Contains("--bearer-token-env-var LANCOMMANDER_TOKEN", tabs[2].TextContent);
        Assert.Contains("codex mcp login", tabs[2].TextContent);
        Assert.Contains(".vscode/mcp.json", tabs[3].TextContent);
        Assert.Contains("Streamable HTTP", tabs[3].TextContent);

        foreach (var link in cut.FindAll("a[target=_blank]"))
        {
            Assert.StartsWith("https://", link.GetAttribute("href"));
            Assert.Equal("noopener noreferrer", link.GetAttribute("rel"));
        }
        Assert.Empty(JSInterop.Invocations);
    }

    [Fact]
    public void LocalhostSessionWarnsThatClientsNeedReachableAddress()
    {
        var cut = Render<McpSetup>();
        Assert.Contains("http://localhost/api/Plugins/Mcp", cut.Find("pre[aria-label='MCP endpoint']").TextContent);
        Assert.Contains("only works for an MCP client on the LANCommander server itself", cut.Markup);
    }

    [Fact]
    public void AddressOverrideUpdatesAllSnippetsWithPathBase()
    {
        var cut = Render<McpSetup>();
        cut.Find("input[name=baseAddress]").Change("https://games.example.com/lc");
        const string expected = "https://games.example.com/lc/api/Plugins/Mcp";
        Assert.Equal(expected, cut.Find("pre[aria-label='MCP endpoint']").TextContent);
        var snippets = cut.FindAll("pre code").Select(x => x.TextContent).ToList();
        Assert.All(snippets.Where(x => x.Contains("http")), x => Assert.Contains(expected, x));
        Assert.DoesNotContain("http://localhost/api/Plugins/Mcp", string.Join("\n", snippets));
        Assert.DoesNotContain("only works for an MCP client", cut.Markup);
        Assert.DoesNotContain("sent unencrypted", cut.Markup);
    }

    [Fact]
    public void InvalidAddressOverrideFallsBackToDetectedAddress()
    {
        var cut = Render<McpSetup>();
        cut.Find("input[name=baseAddress]").Change("https://admin:secret@games.example.com/");
        Assert.Contains("Do not include a username or password", cut.Markup);
        Assert.Equal("http://localhost/api/Plugins/Mcp", cut.Find("pre[aria-label='MCP endpoint']").TextContent);
        Assert.DoesNotContain("secret@", string.Join("\n", cut.FindAll("pre code").Select(x => x.TextContent)));
    }

    [Fact]
    public async Task SnippetCopyButtonCopiesExactSnippetAndShowsFeedback()
    {
        var module = JSInterop.SetupModule("http://localhost/api/Plugins/Mcp/setup.mjs");
        module.SetupVoid("copy", _ => true).SetVoidResult();
        var cut = Render<McpSetup>();
        await cut.Find("button[aria-label='Copy Codex config.toml']").ClickAsync(new MouseEventArgs());
        var copy = Assert.Single(module.Invocations["copy"]);
        Assert.Equal(McpClientConfiguration.CodexConfiguration("http://localhost/api/Plugins/Mcp"), copy.Arguments[0]);
        Assert.Contains("Copied Codex config.toml to clipboard.", cut.Find("p[role=status]").TextContent);
    }

    private static string Normalize(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

    private static AngleSharp.Dom.IElement ButtonByText(IRenderedComponent<McpSetup> cut, string text) =>
        cut.FindAll("button").Single(x => x.TextContent.Trim() == text);
}