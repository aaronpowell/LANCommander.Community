namespace LanCommander.HomeAssistant.Core;

public interface IGameLaunchHost
{
    Task<bool> IsReadyAsync(CancellationToken cancellationToken);

    Task<GameAvailability> GetGameAvailabilityAsync(Guid gameId, CancellationToken cancellationToken);

    Task<IGameLaunchSession> StartGameAsync(Guid gameId, CancellationToken cancellationToken);
}

public sealed record GameAvailability(bool IsInstalled, bool IsRunning);

public sealed record CatalogGame(
    Guid Id, string Title, bool IsInstalled, bool IsRunning = false,
    string InstallStatus = "not_installed", double? InstallProgress = null)
{
    public string Option => $"{(Title.Length > 210 ? Title[..210] : Title)} [{Id:D}]";
}

public interface IGameCatalogHost : IGameLaunchHost
{
    Task<IReadOnlyList<CatalogGame>> GetCatalogAsync(CancellationToken cancellationToken);
    Task<IGameLaunchSession> StartInstallAsync(Guid gameId, CancellationToken cancellationToken);
}

public interface IGameLaunchSession
{
    Task<bool> Started { get; }

    Task Completion { get; }
}

public sealed class RemoteOperationException(string message) : InvalidOperationException(message);
