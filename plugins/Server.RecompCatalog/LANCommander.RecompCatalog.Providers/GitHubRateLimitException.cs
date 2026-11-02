namespace LANCommander.RecompCatalog.Infrastructure;

public sealed class GitHubRateLimitException(
    DateTimeOffset? resetsAt,
    bool authenticated,
    string message) : HttpRequestException(message)
{
    public DateTimeOffset? ResetsAt { get; } = resetsAt;
    public bool Authenticated { get; } = authenticated;
}
