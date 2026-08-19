// BlazeDb OPFS storage module.
// Files live in the Origin Private File System under a per-database directory.
// createWritable() commits atomically on close() (the browser writes to a swap file),
// which gives BlazeDb its atomic manifest/snapshot writes for free.

const lockReleases = new Map();

export async function acquireLock(lockName) {
  if (!navigator.locks) {
    // No Web Locks API (very old browser): proceed, single-tab usage assumed.
    return true;
  }
  return await new Promise((resolve) => {
    navigator.locks.request(lockName, { ifAvailable: true }, (lock) => {
      if (lock === null) {
        resolve(false);
        return null;
      }
      resolve(true);
      // Hold the lock until releaseLock is called (or the tab goes away).
      return new Promise((release) => {
        lockReleases.set(lockName, release);
      });
    });
  });
}

export function releaseLock(lockName) {
  const release = lockReleases.get(lockName);
  if (release) {
    lockReleases.delete(lockName);
    release();
  }
}

export async function openDatabaseDirectory(databaseName) {
  const root = await navigator.storage.getDirectory();
  return await root.getDirectoryHandle(databaseName, { create: true });
}

// Opens an existing database directory without creating it; returns null when the writer tab has
// not created the database yet, so a replica can wait rather than materialize an empty one.
export async function openExistingDatabaseDirectory(databaseName) {
  try {
    const root = await navigator.storage.getDirectory();
    return await root.getDirectoryHandle(databaseName);
  } catch (error) {
    if (error.name === "NotFoundError") {
      return null;
    }
    throw error;
  }
}

// Returns the file contents as a Uint8Array, or null if the file does not exist.
export async function readFile(directory, name) {
  try {
    const handle = await directory.getFileHandle(name);
    const file = await handle.getFile();
    return new Uint8Array(await file.arrayBuffer());
  } catch (error) {
    if (error.name === "NotFoundError") {
      return null;
    }
    throw error;
  }
}

// Copies a staged Uint8Array into a .NET-provided span (zero extra allocations on the JS side).
export function copyBytes(source, destination) {
  destination.set(source);
}

export async function writeAtomic(directory, name, bytes) {
  const handle = await directory.getFileHandle(name, { create: true });
  const writable = await handle.createWritable();
  await writable.write(bytes);
  await writable.close();
}

// On the main thread the only writable API is createWritable(), which stages the whole file in a
// swap copy and swaps it in on close(). An append is therefore an atomic rewrite whose cost grows
// with the WAL - fine at the default checkpoint size, and the reason the engine compacts the log
// rather than letting it grow. (Sync access handles would avoid the copy but exist only in workers.)
export async function appendFile(directory, name, bytes) {
  const handle = await directory.getFileHandle(name, { create: true });
  const file = await handle.getFile();
  const writable = await handle.createWritable({ keepExistingData: true });
  await writable.write({ type: "write", position: file.size, data: bytes });
  await writable.close();
}

// { usage, quota } for this origin, or null when the browser does not implement estimate().
// Values are deliberately coarse (browsers pad them) and cover the whole origin, not just BlazeDb.
export async function storageEstimate() {
  if (!navigator.storage || !navigator.storage.estimate) {
    return null;
  }
  const estimate = await navigator.storage.estimate();
  return { usage: estimate.usage ?? 0, quota: estimate.quota ?? 0 };
}

// Asks the browser to exempt this origin from automatic eviction under storage pressure.
// Resolves to whether persistence is granted; browsers may grant it silently based on engagement.
export async function requestPersistence() {
  if (!navigator.storage || !navigator.storage.persist) {
    return false;
  }
  if (await navigator.storage.persisted()) {
    return true;
  }
  return await navigator.storage.persist();
}

export async function deleteFile(directory, name) {
  try {
    await directory.removeEntry(name);
  } catch (error) {
    if (error.name !== "NotFoundError") {
      throw error;
    }
  }
}
