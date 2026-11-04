using System.Collections.Immutable;
using System.IO.Compression;
using LANCommander.Community.Server.Quake3.Core;

namespace LANCommander.Community.Server.Quake3.Tests;

internal sealed class TestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory,
        "test-data", Guid.NewGuid().ToString("N"));
    public TestDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, true);
}

internal sealed class FakeFiles : IQuakeServerFiles
{
    public Dictionary<string, byte[]> Data { get; } = new(StringComparer.Ordinal);
    public bool Unavailable { get; set; }
    public bool FailOpen { get; set; }
    public bool FailWrite { get; set; }
    public bool NonSeekable { get; set; } = true;
    public bool MalformedListing { get; set; }
    public Func<string, IReadOnlyList<QuakeFileEntry>>? ListingOverride { get; set; }
    public Func<Stream, Stream>? ReadOverride { get; set; }
    public int Opens { get; private set; }
    public List<(Guid ServerId, string Path, byte[] Content)> Writes { get; } = [];
    public Action? AfterWrite { get; set; }
    public bool PartialWrite { get; set; }
    public bool CancelPartialWrite { get; set; }
    public DateTimeOffset Modified { get; set; } = DateTimeOffset.UnixEpoch;
    public Task<IReadOnlyList<QuakeFileEntry>> ListAsync(Guid serverId, string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Unavailable) throw new QuakeFilesUnavailableException();
        if (ListingOverride is not null) return Task.FromResult(ListingOverride(directory));
        var normalized = directory.Replace('\\', '/').TrimEnd('/') + "/";
        IReadOnlyList<QuakeFileEntry> entries = Data.Where(pair => pair.Key.StartsWith(normalized, StringComparison.Ordinal) &&
                !pair.Key[normalized.Length..].Contains('/'))
            .Select(pair => new QuakeFileEntry(pair.Key, pair.Value.Length, Modified)).ToArray();
        if (MalformedListing) entries = [new(normalized + "oversized.pk3", QuakeMapCatalogueService.MaximumPk3Bytes + 1, Modified)];
        return Task.FromResult(entries);
    }
    public Task<Stream> OpenReadAsync(Guid serverId, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Opens++;
        if (FailOpen) throw new IOException("read failed");
        Stream stream = new MemoryStream(Data[path], false);
        if (ReadOverride is not null) return Task.FromResult(ReadOverride(stream));
        return Task.FromResult(NonSeekable ? new ForwardOnlyStream(stream) : stream);
    }
    public Task WriteAsync(Guid serverId, string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (PartialWrite)
        {
            Data[path] = content.Span[..Math.Min(20, content.Length)].ToArray();
            if (CancelPartialWrite) throw new OperationCanceledException(cancellationToken);
            throw new IOException("partial disk write");
        }
        if (FailWrite) throw new IOException("disk full");
        Writes.Add((serverId, path, content.ToArray()));
        AfterWrite?.Invoke();
        return Task.CompletedTask;
    }
    public void AddPak(string path, params string[] names)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var name in names) archive.CreateEntry(name);
        Data[path] = stream.ToArray();
    }
}

internal sealed class ForwardOnlyStream(Stream inner, Action? afterDispose = null) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer, cancellationToken);
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) { inner.Dispose(); afterDispose?.Invoke(); }
        base.Dispose(disposing);
    }
}

internal sealed class FakeRcon : IRcon
{
    public List<string> Commands { get; } = [];
    public Func<string, CancellationToken, Task<string>>? Execute { get; set; }
    public Task<string> CommandAsync(string command, CancellationToken cancellationToken = default)
    {
        Commands.Add(command);
        return Execute?.Invoke(command, cancellationToken) ?? Task.FromResult("acknowledged");
    }
}

internal sealed class FailingStore(IQuakeStateStore inner) : IQuakeStateStore
{
    public bool FailActive { get; set; }
    public bool FailAll { get; set; }
    public bool FailStartupConfirmation { get; set; }
    public Task<QuakeServerState> ReadAsync(Guid serverId, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(serverId, cancellationToken);
    public Task<QuakeServerState> UpdateAsync(Guid serverId, Func<QuakeServerState, QuakeServerState> update,
        CancellationToken cancellationToken = default) => inner.UpdateAsync(serverId, state =>
        {
            var next = update(state);
            if (FailAll || FailActive && next.Active != state.Active ||
                FailStartupConfirmation && next.StartupAttempt?.Status == QuakeStartupStatus.SavedLiveUncertain)
                throw new QuakePersistenceException("disk full");
            return next;
        }, cancellationToken);
}

internal sealed class ControlFixture : IDisposable
{
    public TestDirectory Directory { get; } = new();
    public Guid ServerId { get; } = Guid.NewGuid();
    public FakeFiles Files { get; } = new();
    public FakeRcon Rcon { get; } = new();
    public FailingStore Store { get; }
    public QuakeControlFactory Factory { get; }
    public QuakeServerOptions Options { get; } = new(["Server/baseq3", "Overlay/baseq3"],
        "Server/baseq3/dadlan-admin/server.cfg", "dadlan-admin/server.cfg",
        new("127.0.0.1", 27600, "abcdefghijklmnopqrstuvwx"));
    public QuakeControlService Control { get; }
    public ControlFixture()
    {
        Store = new(new QuakeStateStore(Directory.Path));
        Factory = new(Store, Directory.Path);
        Control = Factory.Create(ServerId, Options, Files, Rcon);
        Files.AddPak("Server/baseq3/pak0.pk3", "maps/q3dm17.bsp", "maps/q3ctf1.bsp");
    }
    public void Dispose() => Directory.Dispose();
}
