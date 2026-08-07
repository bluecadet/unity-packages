# Changelog

All notable changes will be documented here.

## [1.0.2](https://github.com/bluecadet/unity-packages/compare/com.bluecadet.spring@1.0.1...com.bluecadet.spring@1.0.2) (2026-08-07)


### Fixed

* ignore cliff.toml.meta ([2333421](https://github.com/bluecadet/unity-packages/commit/2333421ec307dbb56790c2a50e96067324ebf6e0))

## [1.0.1] - 2026-05-20

### Fixed

- Add CHANGELOG meta files, npmignore cliff.toml, inject _upm.changelog on release

## [1.0.0] - 2026-05-19

### Changed

- Initialize package
- Refactor spring package: fluent builder API, PlayerLoop, Burst physics, weak-ref pool

### Fixed

- Correct MinStiffness assertion — overdamped spring never converges in test budget
