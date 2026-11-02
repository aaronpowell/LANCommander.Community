namespace LANCommander.Community.Server.RecompCatalog.Providers;

public sealed record GitHubAuthentication(string? Token, string Source)
{
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(Token);

    public string Description => IsAuthenticated
        ? $"authenticated via {Source}"
        : "unauthenticated (set GH_TOKEN or GITHUB_TOKEN)";

    public static GitHubAuthentication FromEnvironment(string? variableName = null) =>
        FromEnvironment(Environment.GetEnvironmentVariable, variableName);

    internal static GitHubAuthentication FromEnvironment(
        Func<string, string?> readVariable,
        string? variableName = null)
    {
        if (!string.IsNullOrWhiteSpace(variableName))
            return FromVariable(readVariable, variableName);

        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN" })
        {
            var authentication = FromVariable(readVariable, name);
            if (authentication.IsAuthenticated)
                return authentication;
        }

        return new GitHubAuthentication(null, "none");
    }

    private static GitHubAuthentication FromVariable(
        Func<string, string?> readVariable,
        string variableName)
    {
        var token = readVariable(variableName);
        return string.IsNullOrWhiteSpace(token)
            ? new GitHubAuthentication(null, variableName)
            : new GitHubAuthentication(token.Trim(), variableName);
    }
}
