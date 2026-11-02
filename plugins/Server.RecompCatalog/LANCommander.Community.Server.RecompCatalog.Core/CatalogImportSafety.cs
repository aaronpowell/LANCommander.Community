namespace LANCommander.Community.Server.RecompCatalog.Core;

public enum CatalogImportDisposition
{
    NewImport,
    AlreadyImported,
    UpdateAvailable,
    Blocked,
}

public sealed record CatalogImportAssessment(
    CatalogImportDisposition Disposition,
    string Message)
{
    public bool CanImport => Disposition == CatalogImportDisposition.NewImport;
}

public static class CatalogImportSafety
{
    public static CatalogImportAssessment Assess(
        CatalogEntryStateView state,
        ReleaseIdentity release)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(release);

        if (state.Status == CatalogEntryState.StateCheckFailed)
        {
            return new(CatalogImportDisposition.Blocked,
                $"Import blocked because LANCommander state could not be verified: {state.Message}");
        }

        if (state.Imported is null)
        {
            return state.Status == CatalogEntryState.NeverImported
                ? new(CatalogImportDisposition.NewImport, "This asset can be imported as a new game.")
                : new(CatalogImportDisposition.Blocked, "Import blocked: existing game provenance is unavailable.");
        }

        if (state.Imported.Release is null)
        {
            return new(CatalogImportDisposition.Blocked,
                "This game is already imported, but its exact release is unknown. Re-import is blocked "
                + "because it could overwrite locally edited metadata. Review the existing game in LANCommander.");
        }

        if (string.Equals(state.Imported.Release.Value, release.Value, StringComparison.Ordinal))
        {
            return new(CatalogImportDisposition.AlreadyImported,
                "This exact release and asset are already imported. No game, archive, or metadata will be changed.");
        }

        return new(CatalogImportDisposition.UpdateAvailable,
            "A different release or asset is available for the existing game. Re-import is blocked because "
            + "it could overwrite locally edited metadata. Use the existing game's LANCommander package-update "
            + "workflow; this browser does not replace existing games.");
    }
}
