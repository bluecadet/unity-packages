# Changelog

All notable changes will be documented here.

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
