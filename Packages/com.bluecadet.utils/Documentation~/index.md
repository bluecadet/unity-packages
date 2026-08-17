---
title: Utils
description: Utility functions and helpers for Unity projects, in the Bluecadet.Utils namespace.
---

`com.bluecadet.utils` collects a small set of independent utilities: a command-line argument
parser, an environment/settings snapshot, a cascading settings-file loader with validation, and
two editor windows for exercising both. Package version 1.2.0, targeting Unity 6000.3, with a
dependency on `com.unity.nuget.newtonsoft-json` 3.2.1.

## Install

**Via OpenUPM (recommended)**

```sh
openupm add com.bluecadet.utils
```

Or add the scoped registry manually to `Packages/manifest.json` (once per project):

```json
{
  "scopedRegistries": [
    {
      "name": "OpenUPM",
      "url": "https://package.openupm.com",
      "scopes": ["com.bluecadet"]
    }
  ],
  "dependencies": {
    "com.bluecadet.utils": "<version>"
  }
}
```

**Via Git URL**

```json
{
  "dependencies": {
    "com.bluecadet.utils": "https://github.com/bluecadet/unity-packages.git?path=Packages/com.bluecadet.utils#com.bluecadet.utils@<version>"
  }
}
```

Pick a version from [Releases](https://github.com/bluecadet/unity-packages/releases) — every released tag is listed there. The `openupm add` form above always takes the latest.

## Requirements

- Unity 6000.3 or later
- `com.unity.nuget.newtonsoft-json` 3.2.1

## Utilities

| Page | Description |
| --- | --- |
| [CommandLineArgs](command-line-args.md) | Parses `--flag value`, `--key=value`, and bare `--flag` arguments. |
| [AppEnvironment](app-environment.md) | Immutable snapshot of data path, machine ID, and parsed args; entry point for `SettingsFile<T>`. |
| [Settings files](settings-file.md) | Cascading JSON settings loader with `--set` CLI overrides. |
| [Settings validation](settings-validation.md) | `ISettingsValidator` for reporting bad settings values in the editor. |
| [Editor windows](editor-windows.md) | Tools > Bluecadet windows for simulated args and typed settings editing. |

## Also in this package

These are public API but lightly documented here:

- `Singleton` — abstract `MonoBehaviour` base with `public static bool Quitting { get; }`, tracked
  via `OnApplicationQuit`.
- `Singleton<T>` — abstract `MonoBehaviour` base (`where T : MonoBehaviour`) with
  `public static T Get(bool createIfNotFound = false)`.
- `SingletonRegistry<T>` — static helper (`where T : MonoBehaviour`) with
  `public static T Get(bool createIfNotFound = false)`. Finds or creates the instance, cleans up
  duplicates, and logs a warning if called while `Singleton.Quitting` is true.
- `IdleTimeout` — a `MonoBehaviour` with a `public List<float> IdleTimeoutIntervals` field
  (`[Delayed]`) and `public event Action<int> OnIdleState`. Fires `OnIdleState` with a 1-based tier
  index as each interval elapses; `public void OnUserActivity()` resets the idle timer to 0.
