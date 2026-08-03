# bluecadet_hap native plugin

The native side of `com.bluecadet.hap`: a Zig library that demuxes Hap-encoded
MOV files and decodes their compressed textures, exported to C# through a
small C ABI.

## Layout

| Path                     | What                                                              |
|--------------------------|-------------------------------------------------------------------|
| `src/bluecadet_hap.zig`  | The exported C ABI (`hap_open`, `hap_decode_texture`, ...)         |
| `src/bluecadet_hap.h`    | Hand-maintained header for that ABI, mirrored by the C# bindings   |
| `src/core/`              | Engine-agnostic demux/decode core (pure Zig + the vendored C libs) |
| `vendor/`                | minimp4 and snappy, plus licenses (see `vendor/README.md`)         |
| `tests/fixtures/`        | `.mov` fixtures, golden decoded textures, fuzz crash corpus        |

## Building

Requires Zig 0.16+.

```bash
zig build test                                   # unit + ABI tests
zig build all -p ../Plugins -Doptimize=ReleaseFast   # all 6 shipped targets
```

`zig build all` writes `Plugins/<os>-<arch>/bluecadet_hap.{bundle,dll,so}`
for macOS/Windows/Linux on arm64 and x86_64. Run it from this directory; the
test fixtures are looked up relative to it.

## Core provenance

`src/core/`, `vendor/minimp4/`, `vendor/snappy/` and `tests/fixtures/` were
copied from the sibling `hap-video` core at commit
`378c90927622d524c6dfb6bf37568e9a4ef786b7`, which is where that code is
developed and fuzzed. The long-term plan is to extract the shared pieces into
a standalone `hap-zig` package that both consumers depend on instead of
vendoring.

The copy has since been pruned and re-pointed at this package's consumer, so
it no longer diffs cleanly against that commit:

- The scheduler/playback layers (`decode_scheduler.zig`,
  `playback_controller.zig`, `frame_queue.zig`, `outer_thread_pool.zig`,
  `retire_ring.zig`) were never copied — this package runs its own C# decode
  scheduler, clock and frame ring.
- Everything the C ABI doesn't reach was dropped: the frame-level `decoder.zig`
  layer (the ABI decodes one texture at a time straight into the caller's
  buffer), the presenter-oriented `HapVariant`/`VideoTrackInfo` helpers, and
  `pool_lifecycle.zig`, whose two helpers are inlined into `thread_pool.zig`
  now that only one pool exists here.
- `thread_pool.zig` owns its `kOuterWorkers` constant instead of importing it
  from the outer pool, and adds `setActiveWorkerCount` / `activeWorkerCount`
  so `hap_set_thread_count` can retune chunk-decode parallelism at runtime.
- The fixture and vendor `README.md`s are adapted to this layout.
