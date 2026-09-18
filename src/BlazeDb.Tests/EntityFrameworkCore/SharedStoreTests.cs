using BlazeDb.Storage;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BlazeDb.EntityFrameworkCore.Tests;

/// <summary>
/// What follows from queries handing back the table's own objects when more than one context, or
/// more than one save, is involved. Each of these was a way to corrupt an index or the change
/// tracker silently; now each is either correct or a clear error.
/// </summary>
public class SharedStoreTests
{
    private static DbContextOptions<PeopleContext> IsolatedOptions() =>
        new DbContextOptionsBuilder<PeopleContext>()
            .UseBlazeDb("shared-" + Guid.NewGuid().ToString("N"))
            .Options;

    private static Person Ada() => new() { Id = 1, Name = "Ada", City = "London", Age = 36 };

    [Fact]
    public void A_Second_Context_Saving_Stale_Originals_Gets_A_Concurrency_Error_Not_A_Duplicate_Index_Entry()
    {
        var options = IsolatedOptions();
        using var seed = new PeopleContext(options);
        seed.People.Add(Ada());
        seed.SaveChanges();

        using var a = new PeopleContext(options);
        using var b = new PeopleContext(options);
        var fromA = a.People.Single();
        var fromB = b.People.Single();
        Assert.Same(fromA, fromB); // The same live row, tracked twice with separate originals.

        fromA.City = "Paris";
        a.SaveChanges();

        // B's originals still say London. Applying them would retract nothing and add Paris again.
        fromB.Name = "Ada Lovelace";
        Assert.Throws<DbUpdateConcurrencyException>(() => b.SaveChanges());

        using var check = new PeopleContext(options);
        Assert.Single(check.People.Where(p => p.City == "Paris").ToList());
        Assert.Empty(check.People.Where(p => p.City == "London").ToList());
        Assert.Single(check.Database.GetBlazeDb().GetTable(Person.Table).Lookup(Person.Indexes.City, "Paris"));
    }

    [Fact]
    public void Indexed_Queries_Agree_With_The_Live_Object_Before_SaveChanges()
    {
        using var context = new PeopleContext(IsolatedOptions());
        context.People.Add(Ada());
        context.SaveChanges();

        var ada = context.People.Single();
        ada.City = "Paris";
        ada.Active = true;

        // The row has changed in memory; the index entries move on SaveChanges. Until then, a query
        // through the index has to give the same answer as one that reads the object.
        Assert.Same(ada, context.People.Single(p => p.City == "Paris"));
        Assert.Empty(context.People.Where(p => p.City == "London").ToList());
        Assert.Same(ada, context.People.Single(p => p.Active));
        Assert.Same(ada, context.People.Single(p => p.City == "Paris" && p.Age == 36));
        Assert.Equal([1], context.People.OrderBy(p => p.Age).Select(p => p.Id).ToList());

        context.SaveChanges();
        Assert.Same(ada, context.People.Single(p => p.City == "Paris"));
    }

    [Fact]
    public void A_Rolled_Back_Update_Leaves_The_Context_Usable_And_The_Data_Restored()
    {
        using var context = new PeopleContext(IsolatedOptions());
        context.People.Add(Ada());
        context.SaveChanges();

        var ada = context.People.Single();
        using (var transaction = context.Database.BeginTransaction())
        {
            ada.City = "Paris";
            context.SaveChanges();
            transaction.Rollback();
        }

        // The tracker no longer vouches for an object whose save was undone; the next query loads
        // the row as the database has it instead of tripping over a second entity with the same key.
        var reloaded = context.People.Single(p => p.Id == 1);
        Assert.Equal("London", reloaded.City);
        Assert.Same(reloaded, context.People.Single(p => p.City == "London"));
        Assert.Single(context.ChangeTracker.Entries());

        reloaded.City = "Berlin";
        Assert.Equal(1, context.SaveChanges());
        Assert.Equal("Berlin", context.Database.GetBlazeDb().GetTable(Person.Table).Get(1)!.City);
    }

    [Fact]
    public void A_Rollback_Keeps_Entities_That_Were_Only_Added_And_Never_Saved()
    {
        using var context = new PeopleContext(IsolatedOptions());
        using (var transaction = context.Database.BeginTransaction())
        {
            context.People.Add(Ada());
            transaction.Rollback();
        }

        Assert.Equal(EntityState.Added, context.ChangeTracker.Entries().Single().State);
        Assert.Equal(1, context.SaveChanges());
    }

    [Fact]
    public void Single_With_A_Predicate_Tracks_Only_The_Entity_It_Returns()
    {
        using var context = new PeopleContext(IsolatedOptions());
        for (var i = 1; i <= 5; i++)
        {
            context.People.Add(new Person { Id = i, Name = "P" + i, City = "C", Age = 20 + i });
        }
        context.SaveChanges();
        context.ChangeTracker.Clear();

        var two = context.People.Single(p => p.Name == "P2");
        Assert.Same(two, context.ChangeTracker.Entries().Single().Entity);

        context.ChangeTracker.Clear();
        var last = context.People.OrderBy(p => p.Name).Last();
        Assert.Same(last, context.ChangeTracker.Entries().Single().Entity);

        context.ChangeTracker.Clear();
        var first = context.People.First(p => p.Age > 22);
        Assert.Same(first, context.ChangeTracker.Entries().Single().Entity);

        context.ChangeTracker.Clear();
        var filtered = context.People.Where(p => p.Age > 22).Skip(1).Take(5).ToList();
        Assert.Equal(filtered.Count, context.ChangeTracker.Entries().Count());
    }

    [Fact]
    public void EnsureCreated_Reports_Whether_It_Opened_The_Store()
    {
        var options = IsolatedOptions();
        using var first = new PeopleContext(options);
        Assert.True(first.Database.EnsureCreated());
        Assert.False(first.Database.EnsureCreated());

        using var second = new PeopleContext(options);
        Assert.False(second.Database.EnsureCreated());
    }

    [Fact]
    public async Task EnsureCreatedAsync_Reports_Whether_It_Opened_The_Store()
    {
        var options = IsolatedOptions();
        using var first = new PeopleContext(options);
        Assert.True(await first.Database.EnsureCreatedAsync());
        using var second = new PeopleContext(options);
        Assert.False(await second.Database.EnsureCreatedAsync());
    }

    [Fact]
    public void A_Context_Survives_Another_Context_Deleting_The_Store()
    {
        var options = IsolatedOptions();
        using var a = new PeopleContext(options);
        a.People.Add(Ada());
        a.SaveChanges();
        var engine = a.Database.GetBlazeDb();

        using var b = new PeopleContext(options);
        Assert.True(b.Database.EnsureDeleted());

        // A's cached engine was disposed underneath it; it reopens rather than failing.
        Assert.Empty(a.People.ToList());
        Assert.NotSame(engine, a.Database.GetBlazeDb());
    }

    [Fact]
    public void A_Save_Cannot_Join_Another_Contexts_Transaction()
    {
        var options = IsolatedOptions();
        using var a = new PeopleContext(options);
        using var b = new PeopleContext(options);

        using var transaction = a.Database.BeginTransaction();
        b.People.Add(Ada());
        var ex = Assert.Throws<InvalidOperationException>(() => b.SaveChanges());
        Assert.Contains("Another DbContext", ex.Message);

        transaction.Rollback();
        Assert.Equal(1, b.SaveChanges());
    }

    [Fact]
    public async Task EnsureDeleted_On_Persistent_Storage_Removes_The_Log_Too()
    {
        // Deleting only the manifest is not a delete: the next open would treat the store as fresh
        // and replay wal-1.blz, bringing the rows back.
        var storage = new BlazeDbInMemoryStorage();
        var options = new DbContextOptionsBuilder<PeopleContext>().UseBlazeDb(storage).Options;

        using (var context = new PeopleContext(options))
        {
            context.People.Add(Ada());
            context.SaveChanges();
            await context.Database.GetBlazeDb().FlushAsync();
            Assert.True(await context.Database.EnsureDeletedAsync());
        }
        Assert.Empty(storage.FileNames);

        using (var context = new PeopleContext(options))
        {
            Assert.Empty(context.People.ToList());
            context.People.Add(new Person { Id = 2, Name = "Grace", City = "NYC", Age = 45 });
            context.SaveChanges();
            await context.Database.GetBlazeDb().FlushAsync();
            await context.Database.EnsureDeletedAsync();
        }

        using var again = new PeopleContext(options);
        Assert.Empty(again.People.ToList());
        await again.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task Overlapping_Async_And_Sync_Opens_Share_One_Engine()
    {
        var storage = new SlowOpenStorage();
        var options = new DbContextOptionsBuilder<PeopleContext>().UseBlazeDb(storage).Options;
        using var a = new PeopleContext(options);
        using var b = new PeopleContext(options);

        var opening = a.Database.EnsureCreatedAsync();
        await Task.Delay(50);
        Assert.False(opening.IsCompleted);

        // Reads block until the first open is through; nothing recovers the same storage twice.
        var sync = Task.Run(() => b.Database.EnsureCreated());
        storage.Release();
        Assert.True(await opening);
        Assert.False(await sync);
        Assert.Equal(1, storage.ManifestReads);
        Assert.Same(a.Database.GetBlazeDb(), b.Database.GetBlazeDb());

        await a.Database.EnsureDeletedAsync();
    }

    /// <summary>Holds the first manifest read open so an open can be caught in flight.</summary>
    private sealed class SlowOpenStorage : IBlazeDbStorage
    {
        private readonly BlazeDbInMemoryStorage _inner = new();
        private readonly TaskCompletionSource _gate = new();

        public int ManifestReads;

        public void Release() => _gate.TrySetResult();

        public async ValueTask<byte[]?> ReadAsync(string name, CancellationToken cancellationToken = default)
        {
            if (name == "manifest.blz")
            {
                Interlocked.Increment(ref ManifestReads);
                await _gate.Task;
            }
            return await _inner.ReadAsync(name, cancellationToken);
        }

        public ValueTask WriteAtomicAsync(string name, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
            _inner.WriteAtomicAsync(name, data, cancellationToken);

        public ValueTask AppendAsync(string name, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
            _inner.AppendAsync(name, data, cancellationToken);

        public ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default) =>
            _inner.DeleteAsync(name, cancellationToken);
    }
}
