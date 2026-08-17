# Bluecadet Launchpad

Client for a Launchpad controller. Connects a Unity app to the controller's
HTTP/SSE version-promotion feed, tracks connection state, and coordinates applying
promoted versions through staging, swap gates, and apply events.

For apps whose versions carry content, it also ships a store/mapper/cache toolkit
for downloading, diffing, and preloading that content.

Requires Unity 6000.3+ and `com.unity.nuget.newtonsoft-json`.

> **This package is 0.1.0.** It is pre-1.0 and the API is not yet stable — expect
> breaking changes on minor version bumps. Pin an exact version.

## Installation

```sh
openupm add com.bluecadet.launchpad
```

The `com.bluecadet` scoped registry only needs adding once per project. See
[Installing a Package](../../README.md#installing-a-package) for the manual
`manifest.json` form and the Git URL alternative. Release tags are
`com.bluecadet.launchpad@<version>` — pick one from
[Releases](https://github.com/bluecadet/unity-packages/releases).

## Usage

Construct the pieces once from a composition root and drive `TickMainThread()`
every frame:

```csharp
var client = new LaunchpadClient(cfg.controllerUrl, cfg.consumerId);
var manager = new ContentManager<Exhibit>(client, store, textures, gate, PreloadPaths);

client.Start(cancellationToken);
manager.Start(cancellationToken);
```

`ContentManager` cold-boots from whatever content is already on disk, so the app is
usable even if the controller is unreachable at launch.

## Documentation

Full documentation is in [`Documentation~/`](Documentation~/index.md):

- [Version lifecycle](Documentation~/version-lifecycle.md)
- [Content toolkit](Documentation~/content-toolkit.md)
- [Multiple content models](Documentation~/multiple-content-models.md)

Release history is in [CHANGELOG.md](CHANGELOG.md).
