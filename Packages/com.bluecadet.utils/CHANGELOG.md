# Changelog

All notable changes will be documented here.

## [1.1.1] - 2026-05-28

### Fixed

- Make CommandLineArgs execute in edit mode
- Allow Singleton.Get() in edit mode after play mode exit

## [1.1.0] - 2026-05-28

### Added

- Add CommandLineArgs singleton utility
- Remove JTokenExtensions and JsonValidationException
- Add target frame rate and vsync settings to SettingsManager
- Resolve settings directory from --assetsPath CLI flag
- Remove built-in key bindings from SettingsManager
- Move Setup Script Templates to Tools menu
### Fixed

- Don't LogException when base settings file doesn't exist

## [1.0.1] - 2026-05-20

### Fixed

- Add CHANGELOG meta files, npmignore cliff.toml, inject _upm.changelog on release

## [1.0.0] - 2026-05-19

### Changed

- Create utils package
- Add json validation utils
- Add support for local settings in settingsmanager
- Add gui tinting for settings manager
- Update load from files button cta
- Refactor settings manager to use generics
- Better settings manager dirty field tracking
- Disable settings diff tracking in builds
- Add shortcut to set up script templates
- Add singletonRegistry for composition pattern where inheritence isn't viable
- Add idle timeout utility
- Use system event in idle timeout

### Fixed

- Fix base settings saving logic
- Fix generic type bugs on settings manager
- Fix singleton behavior with no domain reload
