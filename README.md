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

## Install

```bash
dotnet add package BlazeDb                     # engine + source generator
dotnet add package BlazeDb.Browser             # OPFS, IndexedDB, WebCrypto, tabs
dotnet add package BlazeDb.EntityFrameworkCore # UseBlazeDb()
```

The `BlazeDb` package includes the Roslyn source generator. A `PackageReference` to
`BlazeDb.EntityFrameworkCore` or `BlazeDb.Browser` pulls `BlazeDb` in as a dependency, and the
generator still runs (including when those packages are your only direct reference).

Requires the .NET 10 SDK.

## Projects

| Project | NuGet | Purpose |
| --- | --- | --- |
| `src/BlazeDb/BlazeDb` | `BlazeDb` | The engine: tables, indexes, transactions, WAL/snapshot persistence, storage abstraction. No browser dependencies. The source generator is packed into this package. |
| `src/BlazeDb/BlazeDb.SourceGen` | (bundled) | Roslyn incremental source generator: binary serializers, key extractors, schema metadata for `[BlazeDbTable]` types. |
| `src/BlazeDb/BlazeDb.Browser` | `BlazeDb.Browser` | OPFS and IndexedDB storage backends (`[JSImport]` ES modules), Web Locks single-writer election, WebCrypto cipher, cross-tab sync. |
| `src/BlazeDb/BlazeDb.EntityFrameworkCore` | `BlazeDb.EntityFrameworkCore` | EF Core provider: `UseBlazeDb`, change tracking over live rows, LINQ translated to query plans. |
| `src/BlazeDb.Benchmarks` | — | BenchmarkDotNet suite comparing against SQLite. |
| `src/BlazeDb.Demo` | — | Blazor WASM app exercising persistence end-to-end in the browser. |
| `src/BlazeDb.Tests` | — | Engine and EF Core provider tests, including crash recovery, torn writes, tracking and query plans. |

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
`previousValues` must carry every indexed property as the table last saw it; if the row has been
written since those values were taken, the call throws `BlazeDbStaleRowException` instead of
leaving the row indexed twice.

Null values are indexed like any other: `todos.Lookup(TodoItem.Indexes.Category, null)` returns
the uncategorized rows, ordering by an index puts them first (as `OrderBy` does), and a bounded
`Range` excludes them. A unique index still allows any number of nulls.

On desktop, `BlazeDbFileStorage` takes an exclusive lock on its directory for as long as it lives -
dispose it when the database is closed - and `BlazeDbFileStorage.OpenReadOnly(dir)` opens the same
directory for a follower that only reads. `BlazeDbDatabase.DeleteAsync(storage)` removes a database
from any backend.

### Threading model

BlazeDb is built for Blazor WebAssembly, which has one thread, and its guarantees are those of a
**single logical writer with no concurrent readers**. Writes are serialized by a lock so the
background flusher can snapshot consistently, but reads - `Get`, `Scan`, index lookups, every LINQ
query through EF Core - take no lock at all, because on the platform the engine is designed for
there is nothing to lock against. A read that overlaps a write on another thread is a data race on
the underlying dictionaries and may throw or return garbage.

That matters as soon as the engine leaves the browser. On ASP.NET Core or Blazor Server, `UseBlazeDb`
shares one engine between every context of a type, and requests run on different threads; that is
not a supported configuration unless the application serializes all access itself (one request at
a time, or a lock around every query and save). The desktop file backend is for tools, tests and
single-threaded hosts, not for a multi-threaded server.

### Opening with part of the model

A database may hold tables the current options do not register - a context type that owns some of
a shared store's tables, or an application that has dropped a table type. Such tables are still
opened: their rows are carried as raw bytes through recovery and written back out by every
checkpoint, so nothing is lost, and `db.UnregisteredTables` names them. They cannot be queried until
a descriptor for them is registered, which the EF Core provider does automatically when a context
that knows the table shares the engine. Snapshots written by versions before this behavior (format 1)
still open, but only with every table registered.

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

What this does and does not protect: every frame is sealed with a fresh random 96-bit nonce and the
file name as associated data, so contents cannot be read or altered without the key and a frame
cannot be moved between files. It does not prevent *rollback* - someone with access to the files
can put back an older, internally consistent manifest, snapshot and log - and there is no key
rotation: to change the key, open the database, checkpoint, and write it out through a storage
wrapped with the new cipher. Random nonces are safe for about 2^32 frames per key; at the default
flush interval that is decades of continuous writing, but a long-lived database should rotate
before then.

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
invent a database of its own. The storage it returns refuses every write, so forgetting
`ReadOnly = true` cannot turn a replica into a second, unelected writer on the same files.

A reload reads the whole generation - manifest, snapshot and log - before it touches a single row,
and re-reads the manifest at the end. A checkpoint that lands halfway through is noticed and the
read starts again; a reload that fails leaves the replica serving what it had.

Where OPFS is unavailable or cannot be written from the main thread (Firefox private windows;
Safari before 26, which can read OPFS but lacks `createWritable`), swap in `BlazeDbIndexedDbStorage`,
which has the same `CreateAsync` / `CreateReadOnlyAsync` pair. `IsOpfsAvailableAsync` checks for
the write API, not just the directory, so it says no on exactly those browsers:

```csharp
IBlazeDbStorage storage = await BlazeDbIndexedDbStorage.IsOpfsAvailableAsync()
    ? await BlazeDbOpfsStorage.CreateAsync("mydb")
    : await BlazeDbIndexedDbStorage.CreateAsync("mydb");
```

Both backends need the Web Locks API to elect the writer. On a browser without it `CreateAsync`
throws rather than guess; pass `allowWithoutWebLocks: true` if the application guarantees a single
tab. A tab restored from the back/forward cache re-checks that it still holds the lock and stops
writing - the data stays in memory and `db.LastBackgroundError` says why - if another tab has
taken over in the meantime.

## EF Core

The provider maps entity types onto the same `[BlazeDbTable]` types the engine uses - the descriptor the
source generator emits is the model - and is configured the same way as any other EF Core provider:

```csharp
public sealed class AppContext(DbContextOptions<AppContext> options) : DbContext(options)
{
    public DbSet<TodoItem> Todos => Set<TodoItem>();
}

builder.Services.AddDbContext<AppContext>(options =>
    options.UseBlazeDb());                 // in-memory
    // options.UseBlazeDb(storage);        // OPFS, files, encrypted storage, …

var open = context.Todos.Where(t => !t.Done).OrderBy(t => t.CreatedAt).Take(20).ToList();
open[0].Done = true;
context.SaveChanges();
```

`UseBlazeDb()` with no arguments names the store after the context type, so every `AppContext`
shares one engine. Pass a string to choose the name (`UseInMemoryDatabase` style), or an
`IBlazeDbStorage` to persist. Tables are discovered from `DbSet` properties; there is no
`OpenAsync` and no `AddTable` on this path.

`OnConfiguring` works too:

```csharp
protected override void OnConfiguring(DbContextOptionsBuilder options)
    => options.UseBlazeDb();
```

When the storage backend is asynchronous (OPFS in the browser), call
`await context.Database.EnsureCreatedAsync()` once before the first synchronous query.
In-memory stores open synchronously and need no extra step. `EnsureCreated` returns `true` for
the call that opened the store and `false` after that, so `if (EnsureCreated()) Seed();` works
as it does with other providers. `EnsureDeleted` removes the store's files as well as closing it.

Three things are worth knowing, because they follow from the engine being memory-first rather than
from anything EF does:

- **A query returns the row itself, not a copy.** Mutating a tracked entity changes the database in
  memory immediately; `SaveChanges` is what commits it as one transaction and one WAL record, and
  what moves the secondary index entries onto the new values. Reading a value you have modified but
  not yet saved therefore gives you the modified one - from a query as well: while a tracked entity
  of that type has unsaved changes in *any* live context on the engine, queries read the rows
  instead of the indexes so both agree. (Contexts on one engine register with it for exactly this
  purpose; the check costs a change-detection pass over the other contexts' trackers, paid only by
  queries that would otherwise use an index.)
- **`SaveChanges` is a memory operation.** It returns as soon as the transaction commits; the log
  reaches storage on the flush interval, or immediately if you call `context.Database.GetBlazeDb().FlushAsync()`.
  A duplicate key or unique-index violation fails the save with `DbUpdateException` (the engine's
  exception is the `InnerException`); the whole save is rolled back. When the save runs inside a
  transaction you opened - `Database.BeginTransaction()` or one on the engine itself - it joins that
  transaction, so a failure part-way leaves the entries already applied in it for your rollback to
  undo, as with a relational provider.
- **Two contexts on one store track the same objects.** Each keeps its own original values, so if
  both load a row and both save it, the second save describes a state the row has moved past and
  fails with `DbUpdateConcurrencyException`; reload and retry, as with any provider. Updating or
  deleting a row another context has since deleted is a `DbUpdateConcurrencyException` too. A save
  cannot join a transaction another context has open. And after a rollback, the entities whose
  saves were undone are detached - the objects still hold the undone values, and the next query
  loads the rows as the database actually has them.

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

Because the provider replaces EF's query pipeline, it also replaces EF's compiled-query cache. The
predicates and orderings a plan runs are compiled once per query *shape* and re-bound to the values
each execution captures, so repeating a query does not compile anything; the expression walk that
chooses the plan is repeated each time and is a few microseconds. Operators left to LINQ to Objects
(a `Select`, a `GroupBy`) are evaluated by `System.Linq`'s own enumerable query provider, which does
compile them per execution - keep the heavy lifting in the `Where`/`OrderBy`/`Skip`/`Take` prefix
the plan absorbs when a query is hot.

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
dotnet pack src/BlazeDb.slnx -c Release -o artifacts
```

The demo app: `dotnet run --project src/BlazeDb.Demo` and open the printed URL.

Pack produces `BlazeDb`, `BlazeDb.Browser` and `BlazeDb.EntityFrameworkCore` (plus `.snupkg`
symbol packages) under `artifacts/`. Version comes from `VersionPrefix` in
`src/Directory.Build.props`; override it with `-p:Version=1.2.3`.

Pushing a `v*` tag (for example `v0.1.0`) runs `.github/workflows/release.yml`, which builds and
tests that tag, packs it, checks the packages carry the source generator and the browser modules
(`.github/workflows/verify-packages.sh`), and pushes to NuGet.org. Create a `NUGET_API_KEY`
repository secret first.

### Schema evolution

Field numbers identify properties in the binary format. Without `[BlazeDbField(n)]` they follow
declaration order, so adding, removing or reordering a property renumbers the ones after it and
rows already on disk decode into the wrong properties without an error. Pin every property's number
before a type persists real data; the generator warns (`BLZ013`) when a type pins some numbers but
not others, which is the case where an automatic number moves most easily.
