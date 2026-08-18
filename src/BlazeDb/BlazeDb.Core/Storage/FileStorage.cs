namespace BlazeDb.Storage;

/// <summary>
/// File-system storage backend for desktop hosts and integration tests. Atomic writes go
/// through a temp file + rename; appends fsync before returning.
/// </summary>
public sealed class FileStorage : IStorage
{
    private readonly string _directory;

    public FileStorage(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    private string PathOf(string name) => Path.Combine(_directory, name);

    public async ValueTask<byte[]?> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        var path = PathOf(name);
        if (!File.Exists(path))
        {
            return null;
        }
        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteAtomicAsync(string name, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var path = PathOf(name);
        var tempPath = path + ".tmp";
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tempPath, path, overwrite: true);
    }

    public async ValueTask AppendAsync(string name, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(PathOf(name), FileMode.Append, FileAccess.Write, FileShare.Read);
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    public ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        var path = PathOf(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return default;
    }
}
