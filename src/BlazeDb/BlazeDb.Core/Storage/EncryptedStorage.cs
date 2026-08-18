using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BlazeDb.Storage;

/// <summary>
/// Wraps any <see cref="IStorage"/> so everything the engine persists — WAL records, snapshots and
/// the manifest — is encrypted before it reaches the disk or the origin's file system.
/// <para>
/// Each write becomes a self-contained frame of <c>[version][nonce][length][ciphertext+tag]</c>.
/// Framing per write is what keeps <see cref="AppendAsync"/> a genuine append: the WAL can keep
/// growing without rewriting or re-encrypting what came before, and a read simply decrypts the
/// frames in order and concatenates them back into the original byte stream. The file name is
/// mixed in as associated data, so a frame lifted from one file cannot be replayed into another.
/// </para>
/// <para>
/// A short trailing frame is treated as a torn write and ignored, matching the WAL's own tolerance
/// for a half-written tail. A frame that is complete but fails authentication is reported as
/// corruption rather than silently skipped.
/// </para>
/// </summary>
public sealed class EncryptedStorage : IStorage, IDisposable
{
    private const byte FormatVersion = 1;
    private const int LengthPrefixSize = sizeof(int);

    private readonly IStorage _inner;
    private readonly IAeadCipher _cipher;
    private readonly bool _ownsCipher;

    /// <param name="inner">The storage that ends up holding the ciphertext.</param>
    /// <param name="cipher">The cipher to seal each frame with.</param>
    /// <param name="ownsCipher">When true, disposing this also disposes <paramref name="cipher"/>.</param>
    public EncryptedStorage(IStorage inner, IAeadCipher cipher, bool ownsCipher = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cipher = cipher ?? throw new ArgumentNullException(nameof(cipher));
        _ownsCipher = ownsCipher;
    }

    private int HeaderSize => 1 + _cipher.NonceSizeInBytes + LengthPrefixSize;

    /// <summary>
    /// Derives a key from a passphrase with PBKDF2-HMAC-SHA256, which is one of the few
    /// cryptographic primitives available both on desktop and inside the browser sandbox.
    /// The salt must be stored alongside the database and must not be reused across databases.
    /// </summary>
    public static byte[] DeriveKey(string passphrase, ReadOnlySpan<byte> salt, int iterations = 210_000, int keySizeInBytes = 32)
    {
        if (string.IsNullOrEmpty(passphrase))
        {
            throw new ArgumentException("Passphrase must be non-empty.", nameof(passphrase));
        }
        if (salt.Length < 8)
        {
            throw new ArgumentException("Salt must be at least 8 bytes.", nameof(salt));
        }
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, keySizeInBytes);
    }

    public async ValueTask<byte[]?> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        var raw = await _inner.ReadAsync(name, cancellationToken).ConfigureAwait(false);
        if (raw is null)
        {
            return null;
        }
        if (raw.Length == 0)
        {
            return [];
        }

        var associatedData = Encoding.UTF8.GetBytes(name);
        var plaintext = new List<byte[]>();
        var total = 0;
        var offset = 0;

        while (offset < raw.Length)
        {
            if (raw.Length - offset < HeaderSize)
            {
                break; // Torn header.
            }
            if (raw[offset] != FormatVersion)
            {
                throw new CorruptDatabaseException(
                    $"Unrecognized encryption frame version {raw[offset]} in '{name}'.");
            }

            var nonce = raw.AsMemory(offset + 1, _cipher.NonceSizeInBytes);
            var lengthAt = offset + 1 + _cipher.NonceSizeInBytes;
            var length = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(lengthAt, LengthPrefixSize));
            if (length < _cipher.TagSizeInBytes)
            {
                throw new CorruptDatabaseException($"Invalid encryption frame length {length} in '{name}'.");
            }

            var bodyAt = lengthAt + LengthPrefixSize;
            if (raw.Length - bodyAt < length)
            {
                break; // Torn body.
            }

            byte[] chunk;
            try
            {
                chunk = await _cipher
                    .DecryptAsync(nonce, raw.AsMemory(bodyAt, length), associatedData, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                throw new CorruptDatabaseException(
                    $"Failed to authenticate encrypted data in '{name}'. The file was tampered with, " +
                    "truncated mid-frame, or the key is wrong.", ex);
            }

            plaintext.Add(chunk);
            total += chunk.Length;
            offset = bodyAt + length;
        }

        if (plaintext.Count == 1)
        {
            return plaintext[0];
        }

        var result = new byte[total];
        var written = 0;
        foreach (var chunk in plaintext)
        {
            chunk.CopyTo(result, written);
            written += chunk.Length;
        }
        return result;
    }

    public async ValueTask WriteAtomicAsync(string name, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var frame = await SealAsync(name, data, cancellationToken).ConfigureAwait(false);
        await _inner.WriteAtomicAsync(name, frame, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask AppendAsync(string name, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.IsEmpty)
        {
            return;
        }
        var frame = await SealAsync(name, data, cancellationToken).ConfigureAwait(false);
        await _inner.AppendAsync(name, frame, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default) =>
        _inner.DeleteAsync(name, cancellationToken);

    private async ValueTask<byte[]> SealAsync(string name, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var nonce = new byte[_cipher.NonceSizeInBytes];
        RandomNumberGenerator.Fill(nonce);

        var associatedData = Encoding.UTF8.GetBytes(name);
        var ciphertext = await _cipher
            .EncryptAsync(nonce, data, associatedData, cancellationToken)
            .ConfigureAwait(false);

        var frame = new byte[HeaderSize + ciphertext.Length];
        frame[0] = FormatVersion;
        nonce.CopyTo(frame, 1);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1 + nonce.Length, LengthPrefixSize), ciphertext.Length);
        ciphertext.CopyTo(frame, HeaderSize);
        return frame;
    }

    public void Dispose()
    {
        if (_ownsCipher && _cipher is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
