# Bluecadet Utils

Utility functions and helpers for Unity projects.

## Installation

**Via openUPM (recommended)**

```sh
openupm add com.bluecadet.utils
```

Or add the scoped registry manually to `Packages/manifest.json`:

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
    "com.bluecadet.utils": "0.1.6"
  }
}
```

**Via Git URL**

```json
{
  "dependencies": {
    "com.bluecadet.utils": "https://github.com/bluecadet/unity-packages.git?path=Packages/com.bluecadet.utils#utils/v0.1.6"
  }
}
```

## CommandLineArgs

Pure C# parser for command-line style arguments (`--flag value`, `--key=value`, bare `--flag`).
Names are case-insensitive and leading dashes are normalized; when a name repeats, the last
occurrence wins.

```csharp
using Bluecadet.Utils;

CommandLineArgs args = CommandLineArgs.FromProcess();

if (args.HasFlag("verbose")) { /* ... */ }
string env = args.Get("env", fallback: "production");
if (args.TryGet("port", out string port)) { /* ... */ }
```

`FromProcess()` parses `Environment.GetCommandLineArgs()` in a build. In the editor it instead
reads a simulated-args text file at `ProjectSettings/EditorSimulatedArgs.txt` (relative to the
project root), so you can exercise CLI-driven behavior without leaving the editor. Write the file
exactly as you would type arguments on the command line (e.g. `--env=staging --port 8080 --verbose`);
a missing file is treated as no arguments. Quoted tokens (`--name="Blue Cadet"`) are honored via
`CommandLineArgs.ParseText`.

## AppEnvironment

Immutable snapshot of the runtime environment: data directory, machine identity, and parsed
command-line arguments. Also the entry point for constructing `SettingsFile<T>` instances.

```csharp
using Bluecadet.Utils;

AppEnvironment env = AppEnvironment.Current;

string dataPath = env.DataPath;   // --assetsPath, else Application.streamingAssetsPath
string machineId = env.MachineId; // --machineId, else Environment.MachineName
string absolute = env.ResolvePath("some/relative/file.json");
```

In tests, build an isolated environment instead of touching `Current`:

```csharp
AppEnvironment env = AppEnvironment.ForTest("--machineId=CI", tempDir);
```

## SettingsFile

Loads and merges a cascade of JSON settings files, plus CLI `--set` overrides, into a plain
class (no `[Serializable]` required). Construct via `AppEnvironment.SettingsFile<T>()`.

```csharp
public class AppSettings
{
    public GeneralSettings general = new();

    public class GeneralSettings
    {
        public bool debugMode;
        public int targetFrameRate = 60;
    }
}

SettingsFile<AppSettings> settings = AppEnvironment.Current.SettingsFile<AppSettings>();
AppSettings value = settings.Value;
```

Cascade, lowest to highest precedence, all resolved under `AppEnvironment.DataPath`:

1. `settings.json` — shared base
2. `settings.<machineId>.json` — per-machine overrides
3. `settings.local.json` — local overrides (typically git-ignored)
4. `--set key.path=value` — CLI overrides, repeatable

Each file tier is parsed and merged with `JObject.Merge` using `MergeArrayHandling.Replace`
(arrays replace rather than concatenate). A missing tier is silently skipped; a malformed tier
logs a warning and is skipped. If every tier is unusable, `Value` falls back to `new T()`.

`--set` values are parsed as JSON literals where possible (e.g. `--set general.targetFrameRate=30`
sets an int, `--set general.debugMode=true` sets a bool), falling back to a plain string if the
value isn't valid JSON. `--set` is repeatable; every occurrence is applied, and later occurrences
win when they target the same path.

Other members:

- `Reload()` re-reads and re-merges every tier; `OnReloaded` fires afterward.
- `Explain("general.debugMode")` returns the effective value at a dotted path and which
  `SettingsTier` produced it.
- `LoadedPaths` lists the file tiers that actually loaded; `PathFor(tier)` returns the on-disk
  path for a given tier (or a descriptive pseudo-path for `SettingsTier.Cli`, which has no file).
