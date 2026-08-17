---
title: Version lifecycle
description: How a promoted versionId travels from the controller through staging and a swap gate to the content your views read.
---

One version is promoted by the controller. The client surfaces its id, the manager
loads and diffs it off the main thread, stages the result, and a gate decides when
it may replace what is on screen.

## `LaunchpadClient`

A protocol-only client for the controller's HTTP API. It has zero knowledge of
content shape — it only ever surfaces version id strings — and it sends acks back
to the controller.

```csharp
public LaunchpadClient(string controllerUrl, string consumerId)

public ConnectionState State { get; }
public DateTime LastEventUtc { get; }
public event Action<string> OnVersionPromoted;

public void Start(CancellationToken externalToken);
public Task<bool> AckAsync(string versionId, CancellationToken ct);
public void Dispose();
```

`ConnectionState` is `Connecting`, `Connected`, or `Disconnected`.

It listens for `content:version:promoted` SSE events, with a 10-second
manifest-read fallback poll behind them, and raises `OnVersionPromoted` on the
main thread.

### `IVersionFeed`

`LaunchpadClient` is the production implementation of `IVersionFeed`, the
two-member seam `ContentManager` actually depends on:

```csharp
event Action<string> OnVersionPromoted;
Task<bool> AckAsync(string versionId, CancellationToken ct);
```

Implement it to drive the lifecycle from a test, or from a non-HTTP source.

> [!NOTE]
> `OnVersionPromoted` belongs to the **feed**, not to `ContentManager<T>`.
> `ContentManager<T>` exposes `OnVersionStaged` and `OnVersionApplied` only.

## `ContentManager<T>`

Conducts the lifecycle.

```csharp
public ContentManager(
    IVersionFeed feed,
    IContentStore<T> store,
    IAssetCache assets,
    ISwapGate gate,
    Func<T, IEnumerable<string>> preloadPathsSelector)

public IReadOnlyList<ContentItem<T>> Current { get; }
public string CurrentVersionId { get; }
public string StagedVersionId { get; }
public ContentManagerState State { get; }

public event Action<ContentVersion<T>> OnVersionStaged;
public event Action<ContentVersion<T>> OnVersionApplied;

public void Start(CancellationToken ct);
public void TickMainThread();
public void ApplyStagedNow();
public void Dispose();
```

`ContentManagerState` is `Idle`, `Preparing`, or `Staged`.

Note the first parameter's type is `IVersionFeed`, not `LaunchpadClient`.

### What runs where

Loading and diffing a promoted version happen **off** the main thread. Only once
that is done does the manager come back to the main thread, because preloading
whatever the version references, staging the result, and committing it all touch
Unity objects.

### When a staged version commits

Committing is not purely gate-gated. It happens immediately when:

- it is a **cold boot** (there is no current version, so nothing on screen needs
  protecting), or
- the **diff is empty** (nothing is actually changing, so there is nothing worth
  deferring).

Otherwise it waits for the `ISwapGate` to allow it.

The manager acks as soon as a version is *prepared*, not when it is shown.

### One prepare at a time

Exactly one prepare runs at a time. A newly promoted version cancels the one in
flight, drops anything staged, and takes over.

### `ApplyStagedNow()`

Force-commits whatever is staged, bypassing the gate entirely. It is a manual
override for callers that must guarantee staged content is current right now — for
example before shutdown.

> [!CAUTION]
> Calling `ApplyStagedNow()` during normal operation overrides exactly what the
> gate was deliberately deferring for. Use it at deliberate boundaries, not as a
> general-purpose apply.

### `ContentVersion<T>`

Both events carry one of these. There is no public constructor; the manager
creates them.

| Member | Type |
| --- | --- |
| `VersionId` | `string` |
| `VersionFolder` | `string` |
| `Items` | `IReadOnlyList<ContentItem<T>>` |
| `Diff` | `ContentDiff<T>` |

See [the content toolkit](content-toolkit.md#diffing) for `ContentDiff<T>`.

## Swap gates

An `ISwapGate` is policy for when a staged version may commit.

```csharp
public interface ISwapGate
{
    bool CanSwapNow { get; }
    void NotifyStagedPending();
    void ClearPending();
}
```

### `ImmediateGate`

Always allows the swap. `CanSwapNow` is `true`; the two notification methods do
nothing. Use it when there is nothing on screen worth protecting.

### `IdleGate`

```csharp
public IdleGate(TimeSpan maxDefer)

public bool CanSwapNow { get; }
public void SetSwappable(bool canSwap);
public void NotifyStagedPending();
public void ClearPending();
```

Gates swaps behind an app-driven "safe to swap" flag — call `SetSwappable(true)`
while the app is idle or in attract, `false` while a visitor is mid-interaction.
It force-commits after `maxDefer` regardless, so a version never goes stale behind
a busy UI.

## `LaunchpadConfig`

A plain serializable data class with no knowledge of files or of how it is loaded.
Apps subclass it to add their own fields and are responsible for loading it
themselves, for example through their own settings system.

| Field | Type | Default |
| --- | --- | --- |
| `controllerUrl` | `string` | `"http://127.0.0.1:8710"` |
| `consumerId` | `string` | `""` |
| `contentRoot` | `string` | `string.Empty` |
| `sourceFolders` | `string[]` | empty |
| `maxSwapDeferSeconds` | `float` | `300` |

`SettingsFile<T>` from [`com.bluecadet.utils`](/utils/settings-file/) is one way
to load a subclass of this from a JSON cascade, but nothing in this package
requires it.
