using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace LANCommander.Community.Server.Mcp;

public enum McpEndpointReachability
{
    Remote,
    InsecureRemote,
    LocalOnly
}

/// <summary>
/// Builds client setup snippets for the plugin's Streamable HTTP endpoint and bearer-token
/// authentication. Snippets contain placeholders or environment-variable references, never tokens.
/// </summary>
public static class McpClientConfiguration
{
    public const string ServerName = "lancommander";
    public const string EndpointPath = "api/Plugins/Mcp";
    public const string TokenVariable = "LANCOMMANDER_TOKEN";
    public const string TokenPlaceholder = "YOUR_LANCOMMANDER_TOKEN";
    public const string VsCodeInputId = "lancommander-token";
    public const string AuthorizationHeader = "Authorization: Bearer " + TokenPlaceholder;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // Snippets are rendered as HTML-encoded text, so readable URLs are safe here.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Creates the MCP endpoint from the LANCommander base address (scheme, host and path base).
    /// </summary>
    public static bool TryCreateEndpoint(string? baseAddress, out string endpoint, out string? error)
    {
        endpoint = null!;
        error = null;
        if (string.IsNullOrWhiteSpace(baseAddress)
            || !Uri.TryCreate(baseAddress.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(uri.Host))
        {
            error = "Enter an absolute http:// or https:// LANCommander address.";
            return false;
        }
        if (uri.UserInfo.Length > 0)
        {
            error = "Do not include a username or password in the address.";
            return false;
        }
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            error = "Enter the LANCommander base address without a query string or fragment.";
            return false;
        }

        var path = uri.AbsolutePath.EndsWith('/') ? uri.AbsolutePath : uri.AbsolutePath + "/";
        var host = uri.HostNameType == UriHostNameType.Dns ? uri.IdnHost : uri.Host;
        var port = uri.IsDefaultPort ? "" : ":" + uri.Port;
        endpoint = ShellSafe($"{uri.Scheme}://{host}{port}{path}{EndpointPath}");
        return true;
    }

    public static McpEndpointReachability Classify(string url)
    {
        var endpoint = new Uri(url, UriKind.Absolute);
        if (endpoint.IsLoopback
            || endpoint.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(endpoint.IdnHost.Trim('[', ']'), out var address)
                && (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))))
            return McpEndpointReachability.LocalOnly;
        return endpoint.Scheme == Uri.UriSchemeHttps
            ? McpEndpointReachability.Remote
            : McpEndpointReachability.InsecureRemote;
    }

    public static string CopilotCliCommand(string endpoint) =>
        $"copilot mcp add --transport http --header \"{AuthorizationHeader}\" {ServerName} \"{endpoint}\"";

    public static string CopilotCliConfiguration(string endpoint) => Serialize(new
    {
        mcpServers = new Dictionary<string, object>
        {
            [ServerName] = new
            {
                type = "http",
                url = endpoint,
                headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + TokenPlaceholder },
                tools = new[] { "*" }
            }
        }
    });

    public static string ClaudeCodeCommand(string endpoint) =>
        $"claude mcp add --transport http --scope user {ServerName} \"{endpoint}\" --header \"{AuthorizationHeader}\"";

    public static string ClaudeCodeProjectConfiguration(string endpoint) => Serialize(new
    {
        mcpServers = new Dictionary<string, object>
        {
            [ServerName] = new
            {
                type = "http",
                url = endpoint,
                headers = new Dictionary<string, string> { ["Authorization"] = "Bearer ${" + TokenVariable + "}" }
            }
        }
    });

    public static string CodexCommand(string endpoint) =>
        $"codex mcp add {ServerName} --url \"{endpoint}\" --bearer-token-env-var {TokenVariable}";

    public static string CodexConfiguration(string endpoint) =>
        $"[mcp_servers.{ServerName}]\nurl = {TomlString(endpoint)}\nbearer_token_env_var = {TomlString(TokenVariable)}\n";

    public static string VsCodeConfiguration(string endpoint) => Serialize(new
    {
        inputs = new[]
        {
            new
            {
                id = VsCodeInputId,
                type = "promptString",
                description = "LANCommander administrator bearer token",
                password = true
            }
        },
        servers = new Dictionary<string, object>
        {
            [ServerName] = new
            {
                type = "http",
                url = endpoint,
                headers = new Dictionary<string, string> { ["Authorization"] = "Bearer ${input:" + VsCodeInputId + "}" }
            }
        }
    });

    /// <summary>VS Code format containing a real token; only built on explicit copy.</summary>
    public static string VsCodeConfigurationWithToken(string endpoint, string accessToken) => Serialize(new
    {
        servers = new Dictionary<string, object>
        {
            [ServerName] = new
            {
                type = "http",
                url = endpoint,
                headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + accessToken }
            }
        }
    });

    public static string BashTokenVariable => $"export {TokenVariable}='{TokenPlaceholder}'";
    public static string PowerShellTokenVariable => $"$env:{TokenVariable} = '{TokenPlaceholder}'";

    private static string Serialize(object value) => JsonSerializer.Serialize(value, Json);

    private static string TomlString(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                default:
                    if (char.IsControl(c))
                        builder.Append($"\\u{(int)c:X4}");
                    else
                        builder.Append(c);
                    break;
            }
        }
        return builder.Append('"').ToString();
    }

    /// <summary>
    /// Percent-encodes characters that shells could interpret inside double quotes, so commands
    /// are literal in POSIX shells and PowerShell. The URL remains equivalent.
    /// </summary>
    private static string ShellSafe(string url)
    {
        var builder = new StringBuilder(url.Length);
        foreach (var c in url)
        {
            if (char.IsAsciiLetterOrDigit(c) || "-._~:/?#[]@&+,;=%*()".Contains(c))
                builder.Append(c);
            else
                foreach (var b in Encoding.UTF8.GetBytes(c.ToString()))
                    builder.Append('%').Append(b.ToString("X2"));
        }
        return builder.ToString();
    }
}
