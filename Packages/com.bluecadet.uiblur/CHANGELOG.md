# Changelog

All notable changes will be documented here.

## [1.0.2](https://github.com/bluecadet/unity-packages/compare/com.bluecadet.uiblur@1.0.1...com.bluecadet.uiblur@1.0.2) (2026-08-07)


### Fixed

* ignore cliff.toml.meta ([2333421](https://github.com/bluecadet/unity-packages/commit/2333421ec307dbb56790c2a50e96067324ebf6e0))


### Changed

* **uiblur:** extract ComputeBlurParams, add tests, remove dead code ([e4b09c8](https://github.com/bluecadet/unity-packages/commit/e4b09c81834bc7f23a8cdc6a01edab37368f1b6f))

## [1.0.1] - 2026-05-20

### Fixed

- Add CHANGELOG meta files, npmignore cliff.toml, inject _upm.changelog on release

## [1.0.0] - 2026-05-19

### Changed

- Initialize package
- Remove blur passes option and make blur scale public
- Add clear texture to uiblur scene view
- Use Texture2D.blackTexture for uiblur clear instead of allocating
- Increase blur range to 256
