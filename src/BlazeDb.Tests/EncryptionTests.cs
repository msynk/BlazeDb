using System.Security.Cryptography;
using System.Text;
using BlazeDb;
using BlazeDb.Storage;
using Xunit;

namespace BlazeDb.Tests;

public class EncryptionTests
{
    private static byte[] Key(byte seed = 1)
    {
        var key = new byte[32];
        Array.Fill(key, seed);
        return key;
    }

    private static BlazeDbEncryptedStorage Wrap(IBlazeDbStorage inner, byte seed = 1) =>
        new(inner, new BlazeDbAesGcmCipher(Key(seed)), ownsCipher: true);

    [Fact]
    public async Task Roundtrips_An_Atomic_Write()
    {
        var inner = new BlazeDbInMemoryStorage();
        using var storage = Wrap(inner);
        var payload = Encoding.UTF8.GetBytes("the quick brown fox");

        await storage.WriteAtomicAsync("snapshot", payload);

        Assert.Equal(payload, await storage.ReadAsync("snapshot"));
    }

    [Fact]
    public async Task Ciphertext_Does_Not_Contain_The_Plaintext()
    {
        var inner = new BlazeDbInMemoryStorage();
        using var storage = Wrap(inner);
        var payload = Encoding.UTF8.GetBytes("super-secret-value");

        await storage.WriteAtomicAsync("snapshot", payload);

        var stored = await inner.ReadAsync("snapshot");
        Assert.NotNull(stored);
        Assert.DoesNotContain("super-secret-value", Encoding.UTF8.GetString(stored!));
        Assert.True(stored!.Length > payload.Length);
    }

    [Fact]
    public async Task Appends_Concatenate_Back_Into_One_Stream()
    {
        var inner = new BlazeDbInMemoryStorage();
        using var storage = Wrap(inner);

        await storage.AppendAsync("wal", "alpha"u8.ToArray());
        await storage.AppendAsync("wal", "beta"u8.ToArray());
        await storage.AppendAsync("wal", "gamma"u8.ToArray());

        var read = await storage.ReadAsync("wal");
        Assert.Equal("alphabetagamma", Encoding.UTF8.GetString(read!));
    }

    [Fact]
    public async Task Each_Write_Uses_A_Fresh_Nonce()
    {
        var inner = new BlazeDbInMemoryStorage();
        using var storage = Wrap(inner);

        await storage.AppendAsync("wal", "same"u8.ToArray());
        await storage.AppendAsync("wal", "same"u8.ToArray());

        var stored = (await inner.ReadAsync("wal"))!;
        var half = stored.Length / 2;
        Assert.NotEqual(stored[..half], stored[half..]);
    }

    [Fact]
    public async Task Missing_File_Still_Reads_As_Null()
    {
        using var storage = Wrap(new BlazeDbInMemoryStorage());
        Assert.Null(await storage.ReadAsync("nope"));
    }

    [Fact]
    public async Task Quota_Awareness_Passes_Through_The_Wrapper()
    {
        // Encrypting a browser database must not cost it the engine's quota handling.
        using var quotaAware = Wrap(new QuotaLimitedStorage { QuotaBytes = 1234 });
        Assert.Equal(1234, (await quotaAware.GetQuotaAsync())!.Value.QuotaBytes);

        using var plain = Wrap(new BlazeDbInMemoryStorage());
        Assert.Null(await plain.GetQuotaAsync());
    }

    [Fact]
    public async Task Wrong_Key_Is_Reported_As_Corruption()
    {
        var inner = new BlazeDbInMemoryStorage();
        using (var writer = Wrap(inner, seed: 1))
        {
            await writer.WriteAtomicAsync("snapshot", "payload"u8.ToArray());
        }

        using var reader = Wrap(inner, seed: 2);
        await Assert.ThrowsAsync<BlazeDbCorruptDatabaseException>(async () => await reader.ReadAsync("snapshot"));
    }

    [Fact]
    public async Task Tampered_Ciphertext_Is_Reported_As_Corruption()
    {
        var inner = new BlazeDbInMemoryStorage();
        using var storage = Wrap(inner);
        await storage.WriteAtomicAsync("snapshot", "payload"u8.ToArray());

        var stored = (await inner.ReadAsync("snapshot"))!;
        stored[^1] ^= 0xFF;
        await inner.WriteAtomicAsync("snapshot", stored);

        await Assert.ThrowsAsync<BlazeDbCorruptDatabaseException>(async () => await storage.ReadAsync("snapshot"));
    }

    [Fact]
    public async Task A_Frame_Cannot_Be_Replayed_Into_Another_File()
    {
        var inner = new BlazeDbInMemoryStorage();
        using var storage = Wrap(inner);
        await storage.WriteAtomicAsync("wal", "payload"u8.ToArray());

        // The file name is bound in as associated data, so moving the bytes breaks the seal.
        await inner.WriteAtomicAsync("snapshot", (await inner.ReadAsync("wal"))!);

        await Assert.ThrowsAsync<BlazeDbCorruptDatabaseException>(async () => await storage.ReadAsync("snapshot"));
    }

    [Fact]
    public async Task A_Torn_Trailing_Frame_Is_Ignored()
    {
        var inner = new BlazeDbInMemoryStorage();
        using var storage = Wrap(inner);
        await storage.AppendAsync("wal", "committed"u8.ToArray());
        var complete = (await inner.ReadAsync("wal"))!;
        await storage.AppendAsync("wal", "interrupted"u8.ToArray());

        // Simulate a crash midway through the second append.
        var torn = (await inner.ReadAsync("wal"))!;
        await inner.WriteAtomicAsync("wal", torn[..(complete.Length + 9)]);

        Assert.Equal("committed", Encoding.UTF8.GetString((await storage.ReadAsync("wal"))!));
    }

    [Fact]
    public async Task A_Database_Round_Trips_Through_Encrypted_Storage()
    {
        var inner = new BlazeDbInMemoryStorage();
        var id = Guid.NewGuid();

        using (var storage = Wrap(inner))
        {
            var db = await BlazeDbDatabase.OpenAsync(new BlazeDbDatabaseOptions
            {
                Storage = storage,
                FlushInterval = TimeSpan.FromHours(1),
            }.AddTable(TodoItem.Table));
            db.GetTable(TodoItem.Table).Insert(new TodoItem { Id = id, Title = "encrypted at rest" });
            await db.FlushAsync();
            await db.DisposeAsync();
        }

        // Nothing readable is left behind on the underlying storage.
        var walBytes = await inner.ReadAsync("wal-1.blz");
        Assert.NotNull(walBytes);
        Assert.DoesNotContain("encrypted at rest", Encoding.UTF8.GetString(walBytes!));

        using (var storage = Wrap(inner))
        {
            var reopened = await BlazeDbDatabase.OpenAsync(new BlazeDbDatabaseOptions
            {
                Storage = storage,
                FlushInterval = TimeSpan.FromHours(1),
            }.AddTable(TodoItem.Table));
            await using var owned = reopened;

            Assert.Equal("encrypted at rest", reopened.GetTable(TodoItem.Table).Get(id)!.Title);
        }
    }

    [Fact]
    public async Task Encrypted_Database_Survives_A_Checkpoint()
    {
        var inner = new BlazeDbInMemoryStorage();
        using var storage = Wrap(inner);
        var options = new BlazeDbDatabaseOptions
        {
            Storage = storage,
            FlushInterval = TimeSpan.FromHours(1),
        }.AddTable(TodoItem.Table);

        var db = await BlazeDbDatabase.OpenAsync(options);
        var todos = db.GetTable(TodoItem.Table);
        for (var i = 0; i < 50; i++)
        {
            todos.Insert(new TodoItem { Id = Guid.NewGuid(), Title = "row " + i });
        }
        await db.CheckpointAsync();
        await db.DisposeAsync();

        var reopened = await BlazeDbDatabase.OpenAsync(new BlazeDbDatabaseOptions
        {
            Storage = storage,
            FlushInterval = TimeSpan.FromHours(1),
        }.AddTable(TodoItem.Table));
        await using var owned = reopened;

        Assert.Equal(50, reopened.GetTable(TodoItem.Table).Count);
    }

    [Fact]
    public void Derived_Keys_Are_Stable_And_Salt_Dependent()
    {
        var salt = "0123456789abcdef"u8.ToArray();
        var other = "fedcba9876543210"u8.ToArray();

        var a = BlazeDbEncryptedStorage.DeriveKey("correct horse battery staple", salt, iterations: 1000);
        var b = BlazeDbEncryptedStorage.DeriveKey("correct horse battery staple", salt, iterations: 1000);
        var c = BlazeDbEncryptedStorage.DeriveKey("correct horse battery staple", other, iterations: 1000);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(32, a.Length);
    }

    [Fact]
    public void Derive_Key_Rejects_Weak_Inputs()
    {
        Assert.Throws<ArgumentException>(() => BlazeDbEncryptedStorage.DeriveKey("", "0123456789abcdef"u8));
        Assert.Throws<ArgumentException>(() => BlazeDbEncryptedStorage.DeriveKey("passphrase", "short"u8));
    }

    [Fact]
    public async Task Passphrase_Derived_Key_Decrypts_What_It_Encrypted()
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var inner = new BlazeDbInMemoryStorage();
        var key = BlazeDbEncryptedStorage.DeriveKey("hunter2-but-longer", salt, iterations: 1000);

        using (var storage = new BlazeDbEncryptedStorage(inner, new BlazeDbAesGcmCipher(key), ownsCipher: true))
        {
            await storage.WriteAtomicAsync("snapshot", "payload"u8.ToArray());
        }

        var sameKey = BlazeDbEncryptedStorage.DeriveKey("hunter2-but-longer", salt, iterations: 1000);
        using var reader = new BlazeDbEncryptedStorage(inner, new BlazeDbAesGcmCipher(sameKey), ownsCipher: true);
        Assert.Equal("payload"u8.ToArray(), await reader.ReadAsync("snapshot"));
    }
}
