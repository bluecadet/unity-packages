---
title: Supported formats
description: The five Hap variants this package decodes, and the two it detects but rejects.
---

Hap is a family of variants rather than a single codec. Each stores frames in a
block-compressed texture format the GPU reads directly.

| Variant | Compression | Alpha |
| --- | --- | --- |
| Hap | DXT1 | — |
| Hap Alpha | DXT5 | yes |
| Hap Q | YCoCg DXT5 | — |
| Hap Q Alpha | YCoCg DXT5 + RGTC1 | yes |
| Hap R | BC7 | — |

Hap Q Alpha decodes as real transparency rather than an approximation: the player
uploads two textures per frame — a YCoCg colour plane and an RGTC1 alpha plane —
and combines them in a decode shader.

## Variants that will not decode

HapA (alpha-only) and Hap HDR (BC6H) are recognised in the container but not
decoded. Opening one fails at `OpenAsync`/`Open` with
`HapOpenStatus.UnsupportedFormat` rather than producing a broken frame — see
[open results](hap-player.md#open-results) for the full status list.

## Chunked frames

Hap frames may be split into chunks so that decompression can be parallelised.
The number of threads used to decompress one frame's chunks is a process-wide
setting, `HapPlayer.DecodeThreadCount`; see
[process-wide tuning](performance.md#process-wide-tuning).

## Encoding source material

This package only reads Hap; it does not encode. Encode with whatever tool your
pipeline already uses — the variant choice is the meaningful decision:

- **Hap** is the cheapest per frame and the largest visual compromise.
- **Hap Q** is the usual default when quality matters; it roughly doubles the
  per-frame byte size of Hap, which is also the thing that dominates playback
  cost.
- **Hap R** (BC7) is the highest quality of the three opaque variants.
- Reach for the **Alpha** variants only when you actually need transparency —
  Hap Q Alpha costs two texture uploads per frame instead of one.

Per-frame byte size is what drives main-thread cost, so it is worth treating the
variant and the resolution as one combined budget decision. See
[playing many videos at once](performance.md).
