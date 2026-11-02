using LANCommander.RecompCatalog.Core;

namespace LANCommander.RecompCatalog.Plugin;

internal sealed class CatalogImportGate : IDisposable
{
    private readonly SemaphoreSlim _imports = new(1, 1);

    internal async Task<CatalogImportResult> RunAsync(
        ImportPreparation preparation,
        ICatalogHostIntegration host,
        Func<CancellationToken, Task<CatalogImportResult>> import,
        CancellationToken cancellationToken = default)
    {
        // Shared across plugin scopes: the state read and import must not interleave with another import.
        await _imports.WaitAsync(cancellationToken);
        try
        {
            var states = await host.LoadImportedStatesAsync([preparation.Entry], cancellationToken);
            if (!states.TryGetValue(preparation.Entry.Id, out var state))
                throw new InvalidOperationException("Import blocked: LANCommander returned no state for this entry.");

            var release = ReleaseIdentity.Create(
                preparation.Entry.Identity, preparation.Release, preparation.Asset);
            var assessment = CatalogImportSafety.Assess(state, release);
            if (assessment.Disposition == CatalogImportDisposition.AlreadyImported)
                return new CatalogImportResult(state.Imported!.GameId, 0, state.Imported, AlreadyImported: true);

            if (!assessment.CanImport)
                throw new InvalidOperationException(assessment.Message);

            cancellationToken.ThrowIfCancellationRequested();
            return await import(cancellationToken);
        }
        finally
        {
            _imports.Release();
        }
    }

    public void Dispose() => _imports.Dispose();
}
