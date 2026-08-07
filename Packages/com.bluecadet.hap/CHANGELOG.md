# Changelog

All notable changes will be documented here.

## [2.0.0](https://github.com/bluecadet/unity-packages/compare/com.bluecadet.hap@1.1.0...com.bluecadet.hap@2.0.0) (2026-08-07)


### ⚠ BREAKING CHANGES

* **hap:** await opening and closing a video
* **hap:** decode video frames into GPU textures
* **hap:** rework the native API around textures

### Added

* **hap:** add first-party demux and decode core ([d456974](https://github.com/bluecadet/unity-packages/commit/d4569748d00eddd718c07149c5bd562fe375767c))
* **hap:** await opening and closing a video ([834e5a2](https://github.com/bluecadet/unity-packages/commit/834e5a2a7a97c7079cd3bc42b48ea00b27c56d42))
* **hap:** decode video frames into GPU textures ([b662292](https://github.com/bluecadet/unity-packages/commit/b662292aa016bf5c2f2f211d1a315da3f8ad4cb5))
* **hap:** rework the native API around textures ([55bb032](https://github.com/bluecadet/unity-packages/commit/55bb032d894d55d9c80793b959f089568c2be4af))
* **hap:** show Hap Q Alpha transparency ([c52cdf7](https://github.com/bluecadet/unity-packages/commit/c52cdf7b4183dee0d23f3315f701ad71101cbd8f))
* **hap:** stagger and cap uploads across players ([5a8d88f](https://github.com/bluecadet/unity-packages/commit/5a8d88f7cb88adc624804ac54b7119af7544cf7b))


### Fixed

* **hap:** cut a 1ms stall from closing videos ([c90dbf4](https://github.com/bluecadet/unity-packages/commit/c90dbf47fa57861d20ae30a2ce771d9492429398))
* **hap:** cut allocs and cache traffic in decode ([50e1678](https://github.com/bluecadet/unity-packages/commit/50e167802e6354843f386f6b85fd0b3167c1d6ab))
* **hap:** decode straight into caller memory ([bd8d2dd](https://github.com/bluecadet/unity-packages/commit/bd8d2ddf256e44523a9b3ced6a127d379eb0b344))
* **hap:** honor a timecode set while a video opens ([bdc3b78](https://github.com/bluecadet/unity-packages/commit/bdc3b7879b86af53c9d462897ced280f3c791de7))
* **hap:** open videos 55-70% faster ([5442330](https://github.com/bluecadet/unity-packages/commit/5442330038d54c20523cf4f79208f6255923cd11))
* **hap:** play video in the editor outside play mode ([731889c](https://github.com/bluecadet/unity-packages/commit/731889ce777436cd832aceebfa229589751c28e7))
* **hap:** restore frame prefetching lost in rewrite ([b549192](https://github.com/bluecadet/unity-packages/commit/b549192c3c0383b55b8b5f9517ab40a2fd285d14))
* **hap:** stop re-blitting unchanged video frames ([a39f5c2](https://github.com/bluecadet/unity-packages/commit/a39f5c2b74cecae52d1c0d008baec0309c026cd7))


### Changed

* **hap:** type the decode scheduler requests ([0795da0](https://github.com/bluecadet/unity-packages/commit/0795da0890ed1ac34ab106b221d15aaaa114402c))

## [1.1.0] - 2026-05-28

### Added

- Move open/close onto background threads

### Changed

- Split HapPlayer into focused modules

### Fixed

- Ignore cliff.toml.meta

## [1.0.4] - 2026-05-28

### Changed

- Extract DecodeScheduler from DecodeLoop
- Introduce HapFormat domain type
- Add TryAcquire/HapFrameLease to ring buffer
- Extract HapOutputPipeline from HapPlayer

## [1.0.3] - 2026-05-28

### Fixed

- Extend output RT ring depth to match GPU pipeline depth
- Fix RT write index to always differ from display index
- Restore output RT count to 2, decouple from uploader ring depth

## [1.0.2] - 2026-05-27

### Fixed

- Cycle uploaders per frame-in-flight to fix D3D12 tearing

## [1.0.1] - 2026-05-20

### Fixed

- Add CHANGELOG meta files, npmignore cliff.toml, inject _upm.changelog on release

## [1.0.0] - 2026-05-19

### Changed

- Add initial hap code
- Add hap readme
- Add profiling to hap code
- Swap snappy port for google/snappy
- Add dll meta
- Improve hap scrubbing perf
- Support reverse playback and treat speed=0 as paused
- harden hap native code
- Add second render texture to hap for ping-pong writing/reading, fix tearing
- Hap performance fixes
- Use zig to build hap native code

### Fixed

- Fix demuxer failure on large files
- Fix hapq colorspace conversion, build for mac
- Fix hap textures flipped
- Fix hap performance on windows
- Fix hap platform meta
- Fix scrubbing freezes and high-speed playback deadlocks
- Drain past frames in TryPeek to fix scrubbing jitter
- Revert FIFO queue to latest-wins ring buffer
- Close TryRead TOCTOU race with Interlocked pin
- Snapshot direction inside lock; release pin after upload
- Raise Windows timer resolution for decode thread
