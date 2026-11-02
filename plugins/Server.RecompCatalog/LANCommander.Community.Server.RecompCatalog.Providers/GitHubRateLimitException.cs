namespace LANCommander.Community.Server.RecompCatalog.Providers;

public sealed class GitHubRateLimitException(
    DateTimeOffset? resetsAt,
    bool authenticated,
    string message) : HttpRequestException(message)
{
    public DateTimeOffset? ResetsAt { get; } = resetsAt;
    public bool Authenticated { get; } = authenticated;
}
