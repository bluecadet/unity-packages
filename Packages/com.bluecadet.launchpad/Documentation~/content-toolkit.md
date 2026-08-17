---
title: Content toolkit
description: The store, the mapper seam, the on-disk layout it expects, item diffing, and reference-counted asset caching.
---

For apps whose promoted versions carry content, `ContentManager<T>` delegates the
content-specific work to the pieces on this page. Each one is a seam you can
replace, but the manager needs an implementation of each.

## `ContentStore<T>`

Resolves a version folder on disk and its configured source directories, lists
files, and hands them to an `IContentMapper<T>`. It is fully shape-agnostic; all
parsing, joins, and item ordering live in the mapper.

```csharp
public ContentStore(
    string contentRoot,
    IReadOnlyList<string> sourceFolders,
    IContentMapper<T> mapper)

public Task<LoadedVersion<T>> LoadVersionAsync(string versionId, CancellationToken ct);
```

It implements `IContentStore<T>`, the one-method seam `ContentManager` depends on,
so you can substitute your own for a CMS that is not file-based:

```csharp
public interface IContentStore<T>
{
    Task<LoadedVersion<T>> LoadVersionAsync(string versionId, CancellationToken ct);
}
```

`LoadedVersion<T>` is the plain result type any implementation has to hand back —
three public fields:

| Field | Type |
| --- | --- |
| `VersionId` | `string` |
| `VersionFolder` | `string` |
| `Items` | `IReadOnlyList<ContentItem<T>>` |

### The on-disk layout it expects

Exactly the layout the Launchpad downloader writes:

```
contentRoot/
  manifest.json          declares the current versionId
  versions/
    <versionId>/         folder name IS the version id
      <sourceFolder>/    one per configured sourceFolders entry
```

A version folder may sit directly under `contentRoot` instead of under
`versions/`. Beyond that, resolution is strict: **nothing is matched by substring
and no directory tree is crawled.**

- A promoted `versionId` resolves to the folder of exactly that name, or the load
  fails.
- A cold-boot load (`versionId: null`) takes the version `manifest.json` declares,
  falling back to the newest folder that has all configured source folders.

## `IContentMapper<T>`

The seam you implement per project or per CMS. It owns parsing in any format,
cross-file and cross-source joins, and the final item order.

```csharp
public interface IContentMapper<T>
{
    IReadOnlyList<ContentItem<T>> Map(ContentSourceContext context);
}
```

`ContentSourceContext` has three public fields:

| Field | Type |
| --- | --- |
| `VersionId` | `string` |
| `VersionFolder` | `string` |
| `Sources` | `IReadOnlyList<ContentSourceFolder>` |

Each `ContentSourceFolder` exposes `public IReadOnlyList<string> Files` — every
file under that source folder, recursive, ordinal-sorted, with `*.original.json`
excluded.

### `ContentItem<T>`

What a mapper emits. Three public fields:

| Field | Type | Notes |
| --- | --- | --- |
| `Id` | `string` | Must be unique and non-empty across the whole list |
| `ContentHash` | `ulong` | What the differ compares |
| `Data` | `T` | Your model |

### `ContentJsonFiles`

A helper for the common "content is plain JSON files" case:

```csharp
public static IEnumerable<JToken> ParseItems(IEnumerable<string> files);
```

It accepts either a bare array or a `{"data":[...]}` envelope. A JSON *object*
that is neither — a global-config file, say — is deliberately skipped, since there
is no array of elements to find. Read those directly instead; see
[singleton models](multiple-content-models.md#singleton-models).

### `ContentHashing`

```csharp
public static ulong Hash(string canonicalJson);
public static ulong Hash(JToken value, params string[] excludeTopLevelFields);
```

Use `excludeTopLevelFields` to drop volatile fields — export timestamps and the
like — so a re-export with no real change does not hash as changed.

## Diffing

```csharp
public static ContentDiff<T> Diff<T>(
    IReadOnlyList<ContentItem<T>> oldItems,
    IReadOnlyList<ContentItem<T>> newItems);
```

`ContentDiffer.Diff` compares by `Id` plus `ContentHash`, one item at a time, and
**throws on a duplicate or empty `Id`** anywhere in the list.

`ContentDiff<T>`:

| Member | Type |
| --- | --- |
| `Added` | `List<ContentItem<T>>` |
| `Changed` | `List<ContentItem<T>>` |
| `Unchanged` | `List<ContentItem<T>>` |
| `RemovedIds` | `List<string>` |
| `OrderChanged` | `bool` |
| `IsEmpty` | `bool` |

`IsEmpty` is what `ContentManager` reads to decide a swap needs no gate — see
[when a staged version commits](version-lifecycle.md#when-a-staged-version-commits).

## Asset caching

### `IAssetCache`

A format-agnostic load and refcount seam:

```csharp
public interface IAssetCache
{
    Task<bool> RetainAsync(string absolutePath, CancellationToken ct);
    void Release(string absolutePath);
    void EvictUnreferenced();
}
```

A single `RetainAsync` both loads the asset and claims one reference to it, so
there is no window where an asset sits cached at zero references waiting to be
claimed. `Release` gives the reference back, and `EvictUnreferenced` frees
whatever that made unreferenced.

Retaining does not hand you anything to render. That is a separate, type-specific
read method beyond the seam itself.

### `TextureCache`

The built-in `Texture2D` implementation.

```csharp
public int Count { get; }

public Task<bool> RetainAsync(string absolutePath, CancellationToken ct);
public Task<Texture2D> GetAsync(string absolutePath, CancellationToken ct);
public void Release(string absolutePath);
public void EvictUnreferenced();
public void Dispose();
```

`GetAsync` is the type-specific read method — how a view actually gets the
`Texture2D` to display, whether or not anything retained it yet. It loads and
caches on demand either way. **It is main-thread only.**

Concurrent callers for the same path coalesce onto one shared load rather than
decoding the same file twice. Each caller's own `ct` cancels only that caller's
wait on the result; it never aborts the shared load out from under whoever else is
coalesced onto it.

After `Dispose`, `TextureCache` is inert rather than throwing.

Supply your own `IAssetCache` for audio, video, or other asset kinds, with your own
equivalent read method.

### `AssetLease`

The whole set of references one content version holds, as a single `IDisposable`.

```csharp
public IReadOnlyCollection<string> RetainedPaths { get; }

public static Task<AssetLease> AcquireAsync(
    IAssetCache cache,
    IEnumerable<string> paths,
    CancellationToken ct);

public void Dispose();
```

There is no public constructor — `AcquireAsync` is the only way to get one. It
loads with bounded concurrency, skips assets that fail to load, and unwinds itself
if the acquire is cancelled or throws.

`ContentManager` hands a lease from staged to current and disposes the outgoing
one. That is the only place refcount bookkeeping happens, which is why you do not
call `Retain`/`Release` yourself in the normal flow.
