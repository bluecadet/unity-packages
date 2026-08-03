# Bluecadet Launchpad

A Launchpad controller pushes version-promotion events to consumers over
HTTP/SSE. This package connects your Unity app to that controller —
tracking connection state and surfacing promoted `versionId`s — and
coordinates applying those versions via staging, swap gates, and apply
events. For apps whose versions carry content, it also ships a
store/mapper/cache toolkit for downloading, diffing, and preloading that
content with as little app-specific code as possible.

## Installation

**Via openUPM (recommended)**

```sh
openupm add com.bluecadet.launchpad
```

Or add the scoped registry manually to `Packages/manifest.json`:

```json
{
  "scopedRegistries": [
    {
      "name": "OpenUPM",
      "url": "https://package.openupm.com",
      "scopes": ["com.bluecadet"]
    }
  ],
  "dependencies": {
    "com.bluecadet.launchpad": "0.1.0"
  }
}
```

**Via Git URL**

```json
{
  "dependencies": {
    "com.bluecadet.launchpad": "https://github.com/bluecadet/unity-packages.git?path=Packages/com.bluecadet.launchpad#com.bluecadet.launchpad@0.1.0"
  }
}
```

This package depends on `com.unity.nuget.newtonsoft-json`.

## Core concepts

### Controller interface and version lifecycle

- **LaunchpadClient** - protocol-only client for the controller's HTTP API.
  Tracks `ConnectionState` (`Connecting` / `Connected` / `Disconnected`),
  listens for `content:version:promoted` SSE events (with a 10s
  manifest-read fallback poll), and raises `OnVersionPromoted(versionId)` on
  the main thread. Has zero knowledge of content shape - it only ever
  surfaces version id strings - and sends acks back to the controller. It is
  the production implementation of **IVersionFeed**, the two-method seam
  (`OnVersionPromoted` + `AckAsync`) `ContentManager` actually depends on, so
  you can drive the lifecycle from a test or a non-HTTP source.
- **ContentManager&lt;T&gt;** - conducts the version lifecycle: loads and
  diffs a promoted version off the main thread; only once that's done does
  it come back to the main thread, because preloading whatever the version
  references, staging the result (`OnVersionStaged`), and committing it
  (`OnVersionApplied`) all touch Unity objects. Committing isn't purely
  gate-gated, though - it happens immediately on a cold boot (`_current ==
  null`, so there's nothing on screen yet to protect) or when the diff is
  empty (nothing is actually changing, so there's nothing worth deferring),
  and only otherwise waits for the `ISwapGate` to allow it. Acks as soon as
  a version is prepared, not when it's shown. Both events carry a
  `ContentVersion<T>` (`VersionId`, `VersionFolder`, `Items`, `Diff`), and
  `State` is `Idle` / `Preparing` / `Staged`. Exactly one prepare runs at a
  time: a newly promoted version cancels the one in flight, drops anything
  staged, and takes over. `ApplyStagedNow()` force-commits whatever is
  staged, bypassing the gate entirely - a manual override for callers that
  must guarantee staged content is current right now (e.g. before
  shutdown); calling it during normal operation overrides whatever the
  gate was deliberately deferring for.
- **SwapGates (ImmediateGate / IdleGate)** - policy for when a staged
  version may commit. `IdleGate` gates swaps behind an app-driven
  "safe to swap" flag (e.g. only while idle/attract) but force-commits after
  a configurable max defer so a version never goes stale behind a busy UI.
- **LaunchpadConfig** - a plain data class (`controllerUrl`, `consumerId`,
  `contentRoot`, `sourceFolders`, `maxSwapDeferSeconds`) with no knowledge of
  files or how it's loaded. Apps subclass it to add their own fields and are
  responsible for loading it themselves (e.g. via their own settings system).

### Content toolkit

For apps whose promoted versions carry content, `ContentManager<T>` delegates
the content-specific work to these pieces:

- **ContentStore&lt;T&gt;** - resolves a version folder on disk and its
  configured source directories, lists files, and hands them to an
  `IContentMapper<T>`. Fully shape-agnostic; all parsing, joins, and item
  ordering live in the mapper. Implements **IContentStore&lt;T&gt;**, the
  one-method seam (`LoadVersionAsync`) `ContentManager` depends on, so you
  can substitute your own for a non-file-based CMS. That method returns a
  `LoadedVersion<T>` (`VersionId`, `VersionFolder`, `Items`) - the plain
  result type any implementation of the seam has to hand back. It expects
  exactly the layout the Launchpad downloader writes:

  ```
  contentRoot/
    manifest.json          declares the current versionId
    versions/
      <versionId>/         folder name IS the version id
        <sourceFolder>/    one per configured sourceFolders entry
  ```

  A version folder may sit directly under `contentRoot` instead of under
  `versions/`, but nothing is matched by substring and no directory tree is
  crawled. A promoted `versionId` resolves to the folder of exactly that
  name or the load fails; a cold-boot load (`versionId: null`) takes the
  version `manifest.json` declares, falling back to the newest folder that
  has all configured source folders.
- **IContentMapper&lt;T&gt;** - the seam you implement per project/CMS: owns
  parsing (any format), cross-file/cross-source joins, and the final item
  order. `ContentJsonFiles.ParseItems` is a helper for the common
  "content is plain JSON files" case (bare array or `{"data":[...]}`
  envelope).
- **IAssetCache / TextureCache** - format-agnostic load/refcount seam. A
  single `RetainAsync(path, ct)` both loads the asset and claims one
  reference to it, so there is no window where an asset sits cached at zero
  references waiting to be claimed; `Release` gives the reference back and
  `EvictUnreferenced` frees whatever that made unreferenced. Retaining
  doesn't hand you anything to render - that's a separate, type-specific
  read method beyond the `IAssetCache` seam itself. On `TextureCache` it's
  `GetAsync(path, ct) -> Task<Texture2D>`: the way a view actually gets the
  `Texture2D` to display, whether or not anything retained it yet (it loads
  and caches on demand either way). Concurrent callers for the same path
  coalesce onto one shared load rather than decoding the same file twice,
  and each caller's own `ct` only cancels that caller's wait on the result
  - it never aborts the shared load out from under whoever else is
  coalesced onto it. `TextureCache` is the built-in `Texture2D`
  implementation (and is inert rather than throwing after `Dispose`);
  supply your own for audio, video, or other asset kinds, with your own
  equivalent read method.
- **AssetLease** - the whole set of references one content version holds,
  as a single `IDisposable`. `AssetLease.AcquireAsync` loads with bounded
  concurrency, skips assets that fail to load, and unwinds itself if the
  acquire is cancelled or throws. `ContentManager` hands a lease from
  staged to current and disposes the outgoing one, which is the only place
  refcount bookkeeping happens.

### Multiple content models

`IContentMapper<T>` isn't limited to one model. When a version carries
several distinct record types - exhibits, sponsors, floor maps - the
pattern is still one `ContentManager<T>`, with `T` a shared base type
(e.g. `abstract class Record`), and one mapper whose `Map` emits a single
mixed, flat list of `ContentItem<Record>` covering every model. Dispatch
per file, not per parse call: feeding every file into one
`ContentJsonFiles.ParseItems` call merges their elements into one token
stream with nothing left to say which model a given token came from, so
the mapper has to look at each file (by name or path) and route it to a
per-model helper before parsing it:

```csharp
abstract class Record { }
sealed class Exhibit : Record { public string title; public string imagePath; }
sealed class Sponsor : Record { public string name; }

class RecordMapper : IContentMapper<Record>
{
    public IReadOnlyList<ContentItem<Record>> Map(ContentSourceContext context)
    {
        var items = new List<ContentItem<Record>>();
        foreach (var file in context.Sources[0].Files)
        {
            if (file.EndsWith("exhibits.json", StringComparison.OrdinalIgnoreCase))
                items.AddRange(MapArray<Exhibit>(file, "exhibit"));
            else if (file.EndsWith("sponsors.json", StringComparison.OrdinalIgnoreCase))
                items.AddRange(MapArray<Sponsor>(file, "sponsor"));
        }
        return items;
    }

    static IEnumerable<ContentItem<Record>> MapArray<TModel>(string file, string idPrefix)
        where TModel : Record
    {
        foreach (JToken token in ContentJsonFiles.ParseItems(new[] { file }))
        {
            yield return new ContentItem<Record>
            {
                Id = $"{idPrefix}:{token["id"]}",
                ContentHash = ContentHashing.Hash(token),
                Data = token.ToObject<TModel>()
            };
        }
    }
}
```

- **Id namespacing** - `ContentDiffer` throws on a duplicate or empty Id
  anywhere in the list, and that list now spans every model. If the CMS
  hands out ids per collection (an exhibit and a sponsor can both be
  `"9"`), prefix them per collection (`"exhibit:9"`, `"sponsor:9"`) so
  they can't collide once merged, as `MapArray` does above.
- **Why one flat list of records, not one "bundle" item holding all the
  collections** - `ContentDiffer` compares by `Id` + `ContentHash`, one
  item at a time. Per-record items give that comparison something to
  work with: this exhibit changed, that sponsor was added, order held
  steady elsewhere - and unchanged records land in `Diff.Unchanged`
  instead of being re-processed. A single bundle item (`Id = "content"`,
  `Data` = a wrapper object holding every collection) collapses all of
  that into one verdict - "the one item changed" - on every publish that
  touches anything at all, which forces a full rebuild and throws away
  the diff machinery you're depending on. The grouped, typed shape the
  app actually wants is easy to get back at the consumer edge, in
  `OnVersionApplied`, *after* the diff has already run against the flat
  list:

  ```csharp
  manager.OnVersionApplied += version =>
  {
      var exhibits = version.Items.Select(i => i.Data).OfType<Exhibit>();
      var sponsors = version.Items.Select(i => i.Data).OfType<Sponsor>();
  };
  ```

  That gets you both: granular change info out of `version.Diff`, and
  ergonomic typed lists for views. Caveat: an app that always rebuilds
  everything on any change doesn't lose anything with a bundle today -
  but it bakes that ceiling in, and there's no way back to granular
  diffing later without changing the content shape.
- **Singleton models** - a global-config file is a JSON object, not an
  array, so `ContentJsonFiles.ParseItems` deliberately skips it (no
  `"data"` array to find elements in). Read it directly instead and emit
  exactly one item with a well-known, fixed Id:

  ```csharp
  JObject config = JObject.Parse(File.ReadAllText(configFile));
  items.Add(new ContentItem<Record>
  {
      Id = "config:global",
      ContentHash = ContentHashing.Hash(config, "exportedAt"),
      Data = config.ToObject<GlobalConfig>()
  });
  ```

  A singleton is just a collection of size one with a stable Id: a hash
  change makes the differ report it modified like any other record, and
  it rides the same atomic version swap as everything else, so global
  config can never be out of sync with the records it describes. Use
  `ContentHashing.Hash(JToken, params string[] excludeTopLevelFields)` to
  drop volatile fields (export timestamps and the like) from the hash so
  a re-export with no real change doesn't hash as changed.
- **The atomicity payoff of one manager** - every model arrives as part
  of the same version, so there's one diff, one swap-gate decision, one
  ack. A cross-model reference (an exhibit's sponsor id) can never
  observe a mixed publish, because there's no window where one model has
  swapped to the new version and another hasn't.
- **When to use one manager per model instead** - only when the content
  streams are genuinely independent, on independent publish schedules,
  and cross-model consistency doesn't matter. Each `ContentManager<T>`
  then needs its own `LaunchpadClient`/`IVersionFeed` (i.e. its own
  channel), because each one acks and gates its swap independently - and
  you give up the atomicity guarantee above in exchange for that
  independence.

## Usage

Construct the pieces once, typically from a composition-root
`MonoBehaviour`, and drive `TickMainThread()` every frame:

```csharp
using Bluecadet.Launchpad;

// Your own subclass of LaunchpadConfig, loaded however your app loads settings.
MyConfig cfg = LoadMyConfig();

var client = new LaunchpadClient(cfg.controllerUrl, cfg.consumerId);
var store = new ContentStore<Exhibit>(cfg.contentRoot, cfg.sourceFolders, new ExhibitMapper());
var textures = new TextureCache();
var gate = new IdleGate(TimeSpan.FromSeconds(cfg.maxSwapDeferSeconds));

var manager = new ContentManager<Exhibit>(
    client,
    store,
    textures,
    gate,
    exhibit => string.IsNullOrEmpty(exhibit.imagePath) ? Array.Empty<string>() : new[] { exhibit.imagePath });

manager.OnVersionApplied += version => { /* refresh views from manager.Current */ };

client.Start(cancellationToken);
manager.Start(cancellationToken);

// Every frame:
manager.TickMainThread();
gate.SetSwappable(isAppIdle);
```

`ContentManager` immediately kicks off a cold-boot load of whatever content
is already on disk (`versionId: null`) so the app is usable even if the
controller is unreachable at launch, then applies promoted versions
thereafter. Dispose `manager`, `client`, and `textures` together when your
composition root is destroyed.
