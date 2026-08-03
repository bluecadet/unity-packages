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
  diffs a promoted version off the main thread, preloads whatever it
  references, stages the result (`OnVersionStaged`), and commits it
  (`OnVersionApplied`) once an `ISwapGate` allows. Acks as soon as a version
  is prepared, not when it's shown. Both events carry a
  `ContentVersion<T>` (`VersionId`, `VersionFolder`, `Items`, `Diff`), and
  `State` is `Idle` / `Preparing` / `Staged`. Exactly one prepare runs at a
  time: a newly promoted version cancels the one in flight, drops anything
  staged, and takes over.
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
  one-method seam (`LoadVersionAsync`) `ContentManager` depends on. It
  expects exactly the layout the Launchpad downloader writes:

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
  `EvictUnreferenced` frees whatever that made unreferenced. `TextureCache`
  is the built-in `Texture2D` implementation (and is inert rather than
  throwing after `Dispose`); supply your own for audio, video, or other
  asset kinds.
- **AssetLease** - the whole set of references one content version holds,
  as a single `IDisposable`. `AssetLease.AcquireAsync` loads with bounded
  concurrency, skips assets that fail to load, and unwinds itself if the
  acquire is cancelled or throws. `ContentManager` hands a lease from
  staged to current and disposes the outgoing one, which is the only place
  refcount bookkeeping happens.

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
