---
title: Playing many videos at once
description: Measured per-player cost, why the average frame time understates the load, and how to stagger playback to flatten it.
---

The bottleneck for playing many videos at once is main-thread texture upload —
not disk I/O, and not decode. Budget against upload.

Each concurrent 4K Hap Q player costs roughly **0.77 ms of main-thread time per
frame**, almost entirely the `Texture2D.Apply` memcpy of one decoded frame into
GPU-visible memory. Cost is dominated by frame byte size, so 1080p — a quarter of
the pixels — costs substantially less. Extrapolating, a little over a quarter as
much, since a small fixed per-upload overhead does not shrink with resolution.

## Average frame time understates the load

If your content's frame rate divides evenly into the display's refresh rate but
does not match it — 30 fps content at 60 Hz being the common case — each player
only uploads on half the ticks. By default every player picks the *same* half,
because they all start at time 0. That packs N uploads onto every other tick and
none onto the ticks between, so the ticks that do work run at roughly double the
per-player cost the average suggests.

Measured on 4K Hap Q:

| Players | Reported mean (of a 60 Hz budget) | Reality |
| --- | --- | --- |
| 16 | ~49% | Busy ticks already consume the entire budget |
| 24 | ~69% | Half of all frames overran the budget |

> [!IMPORTANT]
> Size your player count against worst-case (p99) frame time, not the mean.

## Staggering playback to spread uploads

Phase-offsetting each player's start time spreads uploads across the content's
frame period instead of bunching them on one tick. Seek each player before
starting playback:

```csharp
for (int i = 0; i < players.Count; i++)
{
    HapPlayer player = players[i];
    await player.OpenAsync(path);
    player.Time = (i / (float)players.Count) * (1f / player.FrameRate);
    player.Play();
}
```

Measured on 4K Hap Q at 24 players, staggered against in-phase: frames overrunning
the 60 Hz budget dropped from ~50% to under 1%, and p99 main-thread work roughly
halved.

The trade is that total main-thread work across all players **rises 10–26%**.
Staggering costs more in aggregate than it saves; it buys a flatter, more
predictable per-frame load, not a lower one. Dropped frames and delivered frame
counts were unaffected either way.

### When not to stagger

Staggering introduces up to one content frame of phase skew between players —
33 ms at 30 fps. That is invisible when each player drives an independent screen.
It is a visible seam if a single image spans multiple displays, or if content is
deliberately frame-locked across players. Apply it only where players are allowed
to drift out of phase with each other.

## Process-wide tuning

Both of these are statics on `HapPlayer`, not per-player settings. The last
assignment wins and applies to every open player.

### `DecodeThreadCount`

```csharp
public static int DecodeThreadCount { get; set; }
```

How many threads decompress one chunked frame's chunks in parallel. It applies to
every video currently playing, from its next chunked frame onward. It reads back
`0` until assigned, meaning "use the plugin's default" — one thread per hardware
thread, minus the ones the engine needs. Assigning a value below `1` is rejected
with a logged error.

### `UploadBudgetBytesPerFrame`

```csharp
public static long UploadBudgetBytesPerFrame { get; set; }
```

Caps how many bytes of decoded video the shared main loop hands to the GPU in one
tick, across every open player. Defaults to `0`, meaning no cap.

A tick that would exceed the cap does not drop a frame outright. The players it
defers keep showing what they already uploaded, their clocks keep running, and
each gets another chance on its next turn. The loop also starts its uploads from a
different player each frame, so the deferral rotates rather than always penalising
the same players.

Worth setting once enough players are open that a single tick's uploads would
overrun the frame budget. It trades some dropped frames for a flat per-frame
upload cost across all of them.

## Limitations of these numbers

- Measured on macOS/Metal (Apple M4 Pro). **Not verified on Windows/D3D12.**
- Measured with a warm OS page cache.
- Every player in testing opened the *same* file, so file reads shared one
  page-cache entry. Deployments where each player opens a distinct file may hit a
  disk bottleneck this testing did not exercise.
