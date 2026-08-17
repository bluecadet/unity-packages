---
title: Launchpad client
description: Connects a Unity app to a Launchpad controller's version-promotion feed, and coordinates applying promoted versions through staging and swap gates.
---

A Launchpad controller pushes version-promotion events to consumers over
HTTP/SSE. This package connects a Unity app to that controller — tracking
connection state and surfacing promoted `versionId`s — and coordinates applying
those versions through staging, swap gates, and apply events.

For apps whose versions carry content, it also ships a store/mapper/cache toolkit
for downloading, diffing, and preloading that content with as little app-specific
code as possible.

Namespace: `Bluecadet.Launchpad`.

> [!WARNING]
> This package is **0.1.0**. It is in production use, but it is pre-1.0 and the
> API is not yet stable — expect breaking changes on minor version bumps until it
> reaches 1.0. Pin an exact version.

## Installation

OpenUPM is the recommended route:

```sh
openupm add com.bluecadet.launchpad
```

Or add the scoped registry to `Packages/manifest.json` yourself. The registry
entry is only needed once per project, no matter how many `com.bluecadet`
packages you install:

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
    "com.bluecadet.launchpad": "<version>"
  }
}
```

To pin a git tag instead, release tags are `com.bluecadet.launchpad@<version>`:

```json
{
  "dependencies": {
    "com.bluecadet.launchpad": "https://github.com/bluecadet/unity-packages.git?path=Packages/com.bluecadet.launchpad#com.bluecadet.launchpad@<version>"
  }
}
```

Pick a version from [Releases](https://github.com/bluecadet/unity-packages/releases) — every released tag is listed there. The `openupm add` form above always takes the latest.

## Requirements

- Unity 6000.3+
- `com.unity.nuget.newtonsoft-json` (declared as a package dependency)

## Wiring it up

Construct the pieces once, typically from a composition-root `MonoBehaviour`, and
drive `TickMainThread()` every frame:

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
    exhibit => string.IsNullOrEmpty(exhibit.imagePath)
        ? Array.Empty<string>()
        : new[] { exhibit.imagePath });

manager.OnVersionApplied += version => { /* refresh views from manager.Current */ };

client.Start(cancellationToken);
manager.Start(cancellationToken);

// Every frame:
manager.TickMainThread();
gate.SetSwappable(isAppIdle);
```

`ContentManager` immediately kicks off a cold-boot load of whatever content is
already on disk, so the app is usable even if the controller is unreachable at
launch, then applies promoted versions thereafter.

Dispose `manager`, `client`, and `textures` together when your composition root is
destroyed.

## Where to go next

- [Version lifecycle](version-lifecycle.md) — `LaunchpadClient`, `IVersionFeed`,
  `ContentManager<T>`, the swap gates, and `LaunchpadConfig`.
- [Content toolkit](content-toolkit.md) — the store, the mapper seam, the on-disk
  layout it expects, diffing, and asset caching.
- [Multiple content models](multiple-content-models.md) — how to carry several
  record types in one version without giving up granular diffing.
