# Third-party code

This directory vendors two C/C++ libraries used by the native plugin. Both
are compiled as part of the zig build (see `Native~/build.zig`).

A third library, `hap` (the reference Vidvox Hap codec), used to be vendored
here and has been removed: frame decode is now a clean-room Zig
implementation (`src/core/hap_decode.zig`), so nothing references `hap.c`
anymore and `licenses/LICENSE-hap.txt` no longer applies to anything in this
repository.

`hap_decode.zig` is a clean-room implementation written from the published
Hap bitstream specification (`HapVideoDRAFT.md` in the Vidvox/hap repo), not
a port of `hap.c`; it carries none of that file's BSD-licensed code. The spec
document remains the reference for the wire format.

## minimp4

- Upstream: https://github.com/lieff/minimp4 (a fork of the original
  https://github.com/aspt/mp4, both credited in the vendored header)
- Files: `vendor/minimp4/minimp4.c`, `vendor/minimp4/minimp4.h`
- License: `vendor/licenses/LICENSE-minimp4.txt`
- Upstream version: unrecorded. minimp4 is a single-header library with no
  version macro; the vendored copy carries no commit reference.
- `minimp4.c` itself is just the two defines (`MINIMP4_IMPLEMENTATION`,
  `MINIMP4_ALLOW_64BIT`) plus `#include "minimp4.h"` needed to emit the
  implementation in its own translation unit.
- The vendored `minimp4.h` is **patched** — see "Local patches" below. Any
  refresh from upstream must re-apply those fixes (or confirm upstream has
  since fixed the same issue).

## snappy

- Upstream: https://github.com/google/snappy
- Files: `vendor/snappy/*.cc`, `vendor/snappy/*.h`
- License: `vendor/licenses/LICENSE-snappy.txt`,
  `vendor/licenses/AUTHORS-snappy.txt`
- Upstream version: **1.2.2**, per `SNAPPY_MAJOR`/`SNAPPY_MINOR`/
  `SNAPPY_PATCHLEVEL` in `vendor/snappy/snappy_config/snappy-stubs-public.h`.
- `vendor/snappy/snappy_config/` (`config.h`, `snappy-stubs-public.h`) is
  hand-written, not the CMake-generated output upstream normally produces. It
  disables the optional codec dependencies (`HAVE_LIBLZO2`, `HAVE_LIBZ`,
  `HAVE_LIBLZ4` all 0) since none are needed to decode Hap frames.
- `config.h` gates `SNAPPY_HAVE_SSSE3`/`SNAPPY_HAVE_BMI2` on the compiler's
  `__SSSE3__`/`__BMI2__` macros. Snappy has no runtime CPU dispatch, so
  `build.zig` only passes `-mssse3 -mbmi2` for x86_64 targets; aarch64 gets
  NEON for free via `__ARM_NEON`.

## Local patches

`minimp4.h` carries the hardening fixes below, all driven by fuzzing the
decoders with malformed/adversarial input (the fuzz harnesses live in
`src/core/*_fuzz.zig`, with the crash corpus in
`tests/fixtures/fuzz_regressions/`). None are upstream cherry-picks.

| File         | Description                                                          |
|--------------|----------------------------------------------------------------------|
| `minimp4.h`  | Harden `MP4D_open` against malformed files: guard null-track derefs, fix a 32-bit-wrap bound check in `BOX_stts`, route all parse-time allocations through one bounded/overflow-checked helper, and fix a leak in `MALLOC()`. |
| `minimp4.h`  | Close remaining null-track derefs (`stts`/`stsz`/`stz2`/`stsc`/`stco`/`co64`/`avcC`/`mdhd`) reachable via a lone top-level box, and fix O(n^2) rescans in `sample_to_chunk()`/`MP4D_frame_offset()` by resuming from a per-track cache instead of restarting each call. |
| `minimp4.h`  | Grow `stts` timestamp/duration arrays geometrically (double capacity) instead of reallocating to the exact new size on every entry, fixing a fuzzer-found multi-second hang from many small `stts` entries. |
| `minimp4.h`  | Widen `count * elemsize` malloc-size expressions to 64-bit before multiplying, so overflow can't wrap the size below `minimp4_bounded_malloc`'s check and under-allocate a buffer the parser then writes past. |
| `minimp4.h`  | Guard `MP4D_frame_offset` against a chunk/sample count being set without its matching array being allocated (can happen when a malformed file's second, oversized `stco`/`stsz` box hits an out-of-memory path after the first allocation was freed). |
| `minimp4.h`  | Bail out of `BOX_ctts` once its declared entry count runs past the box's actual payload, instead of reading zero-padding forever — a file could otherwise claim ~4 billion entries in a few bytes on disk. |
| `minimp4.h`  | Fix upstream's `MP4D_64BIT_SUPPORTED` / `MINIMP4_ALLOW_64BIT` macro mixup: the 64-bit box-header and `co64` chunk-offset paths were gated on the former, which nothing defines, so every file over 4 GB was rejected as "not an MP4" despite 64-bit support being enabled. Regression-tested by the synthesized sparse >4 GB fixture in `src/core/demuxer_test.zig`. |

## Local edits to `snappy_config/config.h`

The include guard is named `HAP_SNAPPY_CONFIG_H` (the file is hand-written,
so it has no upstream form to diverge from).
