namespace LANCommander.RecompCatalog.Plugin;

// Progress<T> posts reports asynchronously, so a late report could overwrite a final status message.
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
