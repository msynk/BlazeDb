# BlazeDb

A memory-first, SQL-free embedded database engine for .NET, designed for Blazor WebAssembly
running client-side in the browser.

## Why

Existing client-side database options for Blazor WASM (sqlite-wasm bridges, IndexedDB wrappers)
pay heavy costs on every operation: JS interop marshalling, worker round-trips, and SQL parsing.
BlazeDb eliminates all three:

- **All live data resides in managed memory** as strongly-typed .NET objects. Reads are plain
  method calls into hash/ordered indexes - no I/O, no interop, no query language.
- **Durability is a background concern**: commits apply to memory synchronously and are appended
  to a write-ahead log (WAL) that is flushed asynchronously to OPFS (Origin Private File System).
  Periodic snapshots compact the log.
- **No SQL**: the engine exposes low-level, typed query primitives (`Get`, `Range`, `Scan`,
  `BlazeDbQuery`). The EF Core provider translates LINQ directly into those primitives - there is no
  intermediate query language to generate or parse.
- **AOT/trimming friendly**: serialization and table metadata are produced by a Roslyn source
  generator - zero runtime reflection.
- **Built for the browser's constraints**: cooperative yielding so long scans do not freeze the
  UI, storage-quota awareness instead of surprise write failures, encryption through WebCrypto,
  and a single-writer model with read-only replica tabs.

## Projects

| Project | Purpose |
| --- | --- |
| `src/BlazeDb/BlazeDb.Core` | The engine: tables, indexes, transactions, WAL/snapshot persistence, storage abstraction. No browser dependencies. |
| `src/BlazeDb/BlazeDb.SourceGen` | Roslyn incremental source generator: binary serializers, key extractors, schema metadata for `[BlazeDbTable]` types. |
| `src/BlazeDb/BlazeDb.Browser` | OPFS and IndexedDB storage backends (`[JSImport]` ES modules), Web Locks single-writer election, WebCrypto cipher, cross-tab sync. |
| `src/BlazeDb/BlazeDb.EntityFrameworkCore` | EF Core provider: `UseBlazeDb`, change tracking over live rows, LINQ translated to query plans. |
| `src/BlazeDb.Benchmarks` | BenchmarkDotNet suite comparing against SQLite. |
| `src/BlazeDb.Demo` | Blazor WASM app exercising persistence end-to-end in the browser. |
| `src/BlazeDb.Tests` | Engine and EF Core provider tests, including crash recovery, torn writes, tracking and query plans. |

The solution file (`src/BlazeDb.slnx`) and shared build settings (`src/Directory.Build.props`)
also live under `src/`.

## Quick start

```csharp
[BlazeDbTable("todos")]
[BlazeDbCompoundIndex("CategoryDone", nameof(Category), nameof(Done))]
public partial class TodoItem
{
    [BlazeDbKey] public Guid Id { get; set; }
    [BlazeDbIndex(Unique = true)] public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Category { get; set; }
    [BlazeDbIndex] public bool Done { get; set; }
    [BlazeDbOrderedIndex] public DateTime CreatedAt { get; set; }
}

var db = await BlazeDbDatabase.OpenAsync(new BlazeDbDatabaseOptions
{
    Storage = await BlazeDbOpfsStorage.CreateAsync("mydb"), // or BlazeDbFileStorage / BlazeDbInMemoryStorage / null
}.AddTable(TodoItem.Table));

var todos = db.GetTable(TodoItem.Table);

var id = Guid.NewGuid();
todos.Insert(new TodoItem { Id = id, Slug = "hello", Title = "hello", CreatedAt = DateTime.UtcNow });
var one = todos.Get(id);                                  // O(1), no I/O
var open = todos.Lookup(TodoItem.Indexes.Done, false);    // hash index
var latest = todos.OrderBy(TodoItem.Indexes.CreatedAt, descending: true);
var range = todos.Range(TodoItem.Indexes.CreatedAt, from, to);
var both = todos.Lookup(TodoItem.Indexes.CategoryDone, ("work", false)); // one compound lookup

using (var txn = db.BeginTransaction())
{
    todos.Update(new TodoItem { Id = id, Slug = "hello", Title = "hello", Done = true, CreatedAt = one!.CreatedAt });
    txn.Commit();
}

await db.FlushAsync(); // force durability now (otherwise flushed in the background)
```

Rows are live objects - a read hands back the instance the table holds. `Update` therefore takes a
*new* instance (as above): the table retracts the old index entries through the values of the row it
currently holds, so mutating that instance's indexed properties and passing it back is refused
rather than left to strand index entries. To write a row that was changed in place, hand the table
the values it had: `todos.UpdateInPlace(row, previousValues)` / `todos.DeleteInPlace(key,
previousValues)` - which is what the EF Core provider does with the change tracker's originals.

Streaming a large result set without blocking the browser's only thread:

```csharp
await foreach (var todo in BlazeDbQuery<Guid, TodoItem>.From(todos)
    .UseIndex(TodoItem.Indexes.CreatedAt, from, to)
    .Where(t => !t.Done)
    .ExecuteAsync(batchSize: 256))
{
    Render(todo);
}
```

### Encryption at rest

`BlazeDbEncryptedStorage` wraps any backend and seals each write into its own AES-GCM frame, so WAL
appends stay appends. In the browser, use the WebCrypto cipher - the managed `AesGcm` throws
inside the browser sandbox:

```csharp
var key = BlazeDbEncryptedStorage.DeriveKey(passphrase, salt);   // PBKDF2, works everywhere
var cipher = await BlazeDbWebCryptoCipher.CreateAsync(key);      // BlazeDbAesGcmCipher on desktop
var storage = new BlazeDbEncryptedStorage(await BlazeDbOpfsStorage.CreateAsync("mydb"), cipher, ownsCipher: true);
```

### Storage quota

When the backend can report the origin's allowance, the engine checks it before flushing,
compacts if space is short, and refuses deliberately rather than failing mid-append. The data
stays intact in memory and the flush can be retried once space is freed:

```csharp
var options = new BlazeDbDatabaseOptions
{
    Storage = storage,
    QuotaReserveBytes = 4 * 1024 * 1024,
    OnQuotaPressure = quota => ShowStorageWarning(quota.UsedFraction),
};

var quota = await db.GetStorageQuotaAsync();   // null when the backend cannot say
await BlazeDbOpfsStorage.RequestPersistenceAsync();   // ask not to be evicted
```

Wrapping a browser backend in `BlazeDbEncryptedStorage` keeps its quota awareness. When a background
flush is refused (or fails for any other reason) the data stays committed in memory and the flusher
keeps retrying; `db.LastBackgroundError` reports the standing failure so a UI can say durability is
lagging, and clears again once a flush succeeds.

### Multiple tabs

One tab holds the writer lock; the others open the same database read-only and reload when the
writer checkpoints. Only the notification crosses tabs - the data always travels through storage,
so a replica can never observe an uncommitted state:

```csharp
// Writer tab
var sync = await BlazeDbTabSync.CreatePublisherAsync("mydb");
var db = await BlazeDbDatabase.OpenAsync(new BlazeDbDatabaseOptions
{
    Storage = await BlazeDbOpfsStorage.CreateAsync("mydb"),
    OnCheckpoint = sync.PublishCheckpoint,
}.AddTable(TodoItem.Table));

// Replica tab
var replica = await BlazeDbDatabase.OpenAsync(new BlazeDbDatabaseOptions
{
    Storage = await BlazeDbOpfsStorage.CreateReadOnlyAsync("mydb"),
    ReadOnly = true,
}.AddTable(TodoItem.Table));
await BlazeDbTabSync.CreateReplicaAsync("mydb", replica, onChanged: RefreshUiAsync);
```

`CreateReadOnlyAsync` returns `null` when the database does not exist yet, rather than creating an
empty one: a replica that opens before the writer has written anything should wait for it, not
invent a database of its own.

Where OPFS is unavailable (Firefox private windows, older Safari), swap in `BlazeDbIndexedDbStorage`:

```csharp
IBlazeDbStorage storage = await BlazeDbIndexedDbStorage.IsOpfsAvailableAsync()
    ? await BlazeDbOpfsStorage.CreateAsync("mydb")
    : await BlazeDbIndexedDbStorage.CreateAsync("mydb");
```

## EF Core

The provider maps entity types onto the same `[BlazeDbTable]` types the engine uses - the descriptor the
source generator emits is the model - and needs no configuration beyond the database itself:

```csharp
public sealed class AppContext(DbContextOptions<AppContext> options) : DbContext(options)
{
    public DbSet<TodoItem> Todos => Set<TodoItem>();
}

var db = await BlazeDbDatabase.OpenAsync(new BlazeDbDatabaseOptions { ... }.AddTable(TodoItem.Table));
var context = new AppContext(new DbContextOptionsBuilder<AppContext>().UseBlazeDb(db).Options);

var open = context.Todos.Where(t => !t.Done).OrderBy(t => t.CreatedAt).Take(20).ToList();
open[0].Done = true;
context.SaveChanges();
```

Two things are worth knowing, because they follow from the engine being memory-first rather than
from anything EF does:

- **A query returns the row itself, not a copy.** Mutating a tracked entity changes the database in
  memory immediately; `SaveChanges` is what commits it as one transaction and one WAL record, and
  what moves the secondary index entries onto the new values. Reading a value you have modified but
  not yet saved therefore gives you the modified one.
- **`SaveChanges` is a memory operation.** It returns as soon as the transaction commits; the log
  reaches storage on the flush interval, or immediately if you call `db.FlushAsync()`.

The model comes from the same attributes the engine uses: `[BlazeDbKey]` names the primary key whatever it
is called, and `[BlazeDbIgnore]` keeps a property out of the entity type as well as out of the row bytes.

LINQ is translated as far as the engine's plan model reaches: a source chosen from the predicates -
the primary-key dictionary for `Where(t => t.Id == id)`, `ids.Contains(t.Id)` and `Find(id)`, a
compound or single-property index for equalities and `Contains` lists, an ordered index for ranges
and `OrderBy` - then filters, ordering and paging.
Everything else - projections, grouping, aggregates, joins between local sequences - runs as LINQ to
Objects over the rows the plan returned, which costs nothing extra because those rows are already
objects. The exception is `Include`: BlazeDb rows have no navigations, so relationships are modelled
as key properties and queried against the other table directly.

The demo site walks through all of this against a live database at `/efcore`.

## Performance

BenchmarkDotNet, .NET 10, 10,000-row table, vs native SQLite (in-memory, prepared statements).
Native SQLite is a *favorable* proxy for sqlite-wasm solutions, which additionally pay JS
interop, worker round-trips and wasm execution overhead on every query.

| Operation | BlazeDb | SQLite | Notes |
| --- | ---: | ---: | --- |
| Point get by key | 2.6 ns (0 alloc) | 876 ns | ~340x |
| Count by hash index | 2.7 ns (0 alloc) | 67,649 ns | O(1) vs table scan |
| Ordered range scan (~5,100 rows) | 55.6 us | 69.3 us | comparable; both index-driven |
| Upsert (autocommit) | 462 ns (with WAL) | 752 ns | memory-only: 328 ns |
| Upsert in 1,000-op transaction | 487 ns/op | 682 ns/op | with WAL journaling |

Reproduce with `dotnet run --project src/BlazeDb.Benchmarks -c Release -- --filter *`.
The demo app also has an in-browser micro-benchmark page (`/bench`) that measures the same
operations in the WebAssembly runtime, including per-row storage cost.

## Demo site

`src/BlazeDb.Demo` is an interactive tour of the engine with no external dependencies - no CSS
framework, no icon font, no JavaScript library, no web fonts. Every page drives the real engine
in the browser rather than describing it.

| Page | What it demonstrates |
| --- | --- |
| `/` | Architecture, benchmarks and a quick start, with live stats from the demo database. |
| `/todos` | A working CRUD app: index-backed filters, sorting, paging and persisted preferences. |
| `/schema` | Attributes, the generated descriptor inspected at runtime, supported types, diagnostics. |
| `/indexes` | Hash lookups, ordered ranges, `BlazeDbBound<T>`, and indexed versus scanned counting. |
| `/queries` | A query composer that shows the generated C#, the plan and the results. |
| `/efcore` | The EF Core provider: `UseBlazeDb`, live LINQ, SaveChanges, and what the translator absorbs. |
| `/transactions` | Commit, rollback, dispose-without-commit, nesting errors and crash-recovery proof. |
| `/durability` | WAL, snapshots, checkpoints, and simulated crashes including torn tails and corruption. |
| `/storage` | The `IBlazeDbStorage` contract, a live trace of engine I/O, OPFS and Web Locks notes. |
| `/serialization` | Build a row and inspect the exact bytes, field by field, with a hex dump. |
| `/errors` | Every exception the engine raises, triggered live against throwaway databases. |
| `/bench` | Read, write and encoding benchmarks measured in your own browser. |
| `/roadmap` | What ships, what never will, and how the repository is laid out. |

## Building

```
dotnet build src/BlazeDb.slnx
dotnet test src/BlazeDb.slnx
```

The demo app: `dotnet run --project src/BlazeDb.Demo` and open the printed URL.
