# Changelog

All notable changes will be documented here.

## [2.0.0](https://github.com/bluecadet/unity-packages/compare/com.bluecadet.utils@1.2.0...com.bluecadet.utils@2.0.0) (2026-08-07)


### ⚠ BREAKING CHANGES

* **utils:** Tools/Bluecadet/Setup Script Templates is gone. Set a Root Namespace on your assembly definition instead; Unity generates new scripts with the correct namespace automatically.
* **utils:** the SettingsManager monobehavior is removed. Settings and Simulated Args are dockable windows under Tools > Bluecadet. The settings window renders typed fields for the [SettingsClass]-tagged config with per-tier change tracking (dirty/local/machine/CLI tints) and sparse saves, replacing the raw JSON textarea.
* **utils:** CommandLineArgs (MonoBehaviour), SettingsManager, SettingsManagerBase, and AppSettings are removed. Configuration is now plain C#: CommandLineArgs (immutable parsed args), AppEnvironment (data path, machine id, args), and SettingsFile<T> (merged cascade of <name>.json, <name>.<machineId>.json, <name>.local.json, and repeatable --set CLI overrides).

### Added

* **utils:** add Bluecadet project settings pane ([7f04ef6](https://github.com/bluecadet/unity-packages/commit/7f04ef66fda011a5c96bf4fcdef78265bd8bf97e))
* **utils:** add sparse per-tier settings writes ([5c6a528](https://github.com/bluecadet/unity-packages/commit/5c6a528f562e3e4fa7c0b4165c886cea0f578c59))
* **utils:** make settings and args plain C# ([54ee3ff](https://github.com/bluecadet/unity-packages/commit/54ee3ffb635d78eb8bd41f900c06c713f416a8f8))
* **utils:** move settings UI to Tools &gt; Bluecadet windows ([0301509](https://github.com/bluecadet/unity-packages/commit/0301509f1bbe87048b6ce8a88f7169ed790b82b3))
* **utils:** remove script template setup menu ([cd56c97](https://github.com/bluecadet/unity-packages/commit/cd56c9756477d53c5c20c9a63027bda5658caba0))
* **utils:** validate settings in editor window ([0ba0111](https://github.com/bluecadet/unity-packages/commit/0ba01113154794549d9d9d26f154dbca26122fc1))


### Changed

* **utils:** extract SettingsFile tier cascade helpers ([32b70aa](https://github.com/bluecadet/unity-packages/commit/32b70aab9625558322e1c280c0b5a1f3ab7ec314))

## [Unreleased]

### Removed

- Remove Setup Script Templates menu item; set a Root Namespace on your assembly definition instead

## [1.2.0] - 2026-05-28

### Added

- Add per-machine settings cascade layer (settings.[machineId].json)

### Fixed

- Use stable fallback set when editorDirtyPaths reflection fails
- Dispose and reuse SerializedObject instead of recreating every frame
- Guard file-creation assertion behind UNITY_EDITOR
- Blend instance and local tints on foldouts with mixed child overrides

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
