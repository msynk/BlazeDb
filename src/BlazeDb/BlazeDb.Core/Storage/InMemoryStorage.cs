namespace BlazeDb.Storage;

/// <summary>
/// Storage backend keeping "files" in memory. Useful for tests (including crash simulation:
/// reopen a new Database over the same storage instance) and for ephemeral databases that
/// still want the full persistence pipeline.
/// </summary>
public sealed class InMemoryStorage : IStorage
{
    private readonly object _lock = new();
    private readonly Dictionary<string, byte[]> _files = new();

    public ValueTask<byte[]?> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return ValueTask.FromResult(_files.TryGetValue(name, out var data) ? data.ToArray() : (byte[]?)null);
        }
    }

    public ValueTask WriteAtomicAsync(string name, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _files[name] = data.ToArray();
        }
        return default;
    }

    public ValueTask AppendAsync(string name, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_files.TryGetValue(name, out var existing))
            {
                var combined = new byte[existing.Length + data.Length];
                existing.CopyTo(combined, 0);
                data.Span.CopyTo(combined.AsSpan(existing.Length));
                _files[name] = combined;
            }
            else
            {
                _files[name] = data.ToArray();
            }
        }
        return default;
    }

    public ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _files.Remove(name);
        }
        return default;
    }

    // ---- Test helpers ----

    public IReadOnlyCollection<string> FileNames
    {
        get
        {
            lock (_lock)
            {
                return _files.Keys.ToArray();
            }
        }
    }

    public byte[]? GetFile(string name)
    {
        lock (_lock)
        {
            return _files.TryGetValue(name, out var data) ? data.ToArray() : null;
        }
    }

    public void SetFile(string name, byte[] data)
    {
        lock (_lock)
        {
            _files[name] = data.ToArray();
        }
    }
}
