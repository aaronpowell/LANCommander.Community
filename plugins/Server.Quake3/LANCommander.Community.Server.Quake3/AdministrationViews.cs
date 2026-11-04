namespace LANCommander.Community.Server.Quake3;

public sealed record AssociatedServer(Guid Id, string Name);
public sealed record MatchConfigurationView(
    int Mode, int TimeLimit, int FragLimit, int CaptureLimit, IReadOnlyList<string> Rotation)
{
    public static MatchConfigurationView Default => new(0, 15, 30, 8, ["q3dm17"]);
}

public sealed record Quake3View(
    bool Online, string Status, bool CatalogKnown, bool CatalogIsStale,
    string? CatalogError, IReadOnlyList<string> Maps, MatchConfigurationView? Active,
    IReadOnlyDictionary<string, MatchConfigurationView> Presets,
    string? StartupWarning = null, MatchConfigurationView? StartupConfiguration = null);

public sealed record AdministrationResult(string Message, bool Failed = false);
