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
    "com.bluecadet.utils": "1.2.0"
  }
}
```

**Via Git URL**

```json
{
  "dependencies": {
    "com.bluecadet.utils": "https://github.com/bluecadet/unity-packages.git?path=Packages/com.bluecadet.utils#utils/v1.2.0"
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

`Parse(params string[] argv)` parses an explicit argv array directly, without going through
`FromProcess()` or `ParseText`'s tokenizer — handy in tests. `All` exposes every parsed argument as
a read-only `name → value` dictionary (keyed the same normalized way as `Get`/`TryGet`/`HasFlag`),
for callers that want to enumerate rather than look up one name at a time.

## AppEnvironment

Immutable snapshot of the runtime environment: data directory, machine identity, and parsed
command-line arguments. Also the entry point for constructing `SettingsFile<T>` instances.

```csharp
using Bluecadet.Utils;

AppEnvironment env = AppEnvironment.Current;

string dataPath = env.DataPath;   // --assetsPath, else Application.streamingAssetsPath
string machineId = env.MachineId; // --machineId, else Environment.MachineName
string absolute = env.ResolvePath("some/relative/file.json");
CommandLineArgs args = env.Args;  // the parsed args this environment was built from
```

In tests, build an isolated environment instead of touching `Current`:

```csharp
AppEnvironment env = new AppEnvironment(tempDir, "CI", CommandLineArgs.ParseText("--verbose"));
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
- `TierFor("general.debugMode")` returns the `SettingsTier` that produced the effective value at a
  dotted path, or null if no tier sets it.
- `LoadedPaths` lists the file tiers that actually loaded; `PathFor(tier)` returns the on-disk
  path for a file tier, or null for `SettingsTier.Cli`, which has no file.

## Settings validation

Implement `ISettingsValidator` on a settings class, or on any object nested inside one, to report
values that are missing or out of range. Paths are relative to the validating object, so a nested
class validates its own fields without knowing where it sits:

```csharp
public class AppSettings : ISettingsValidator
{
    public string controllerUrl = "http://127.0.0.1:8710";

    public void Validate(SettingsValidationErrors errors)
    {
        if (!Uri.TryCreate(controllerUrl, UriKind.Absolute, out _))
            errors.Add(nameof(controllerUrl), "Must be an absolute http(s) URL.");
    }
}
```

`errors.Add(path, message)` builds a `SettingsValidationError` — a `Path`/`Message` pair whose
`ToString()` renders as `"path: message"` (or just the message when the path is empty) — and
appends it to `SettingsValidationErrors.Errors`, the read-only list the editor renders from.

Validation is editor-only: the **Settings** window runs it on load and after every edit, and
`SettingsFile<T>` never calls it, so a build always boots with whatever the files say. Errors from
an object inside a list or array are reported against the list field itself, since the editor
treats arrays as single values.

Known limitation: the walk that finds validators does not descend into structs (Unity's math types
expose computed properties that hand back more of themselves), so a validator nested inside a
struct is never called — a struct that implements `ISettingsValidator` itself still is.

## Editor windows

**Tools > Bluecadet** opens two editor-only, dockable windows:

- **Simulated Args** - a text area editing `ProjectSettings/EditorSimulatedArgs.txt` (autosaved on
  every edit), the file `CommandLineArgs.FromProcess()` reads while running in the editor. A
  read-only preview shows how the current text parses, so typos are easy to spot.
- **Settings** - a typed editor for a settings base name (default `settings`). Tag your settings
  class with `[SettingsClass]` (or `[SettingsClass("other")]` for a different base name) and the
  window hydrates it from the merged cascade and draws every field with Unity's own property
  drawers. Field backgrounds show where the effective value comes from: yellow for an unsaved
  change, blue for the `Local` tier, green for the `Machine` tier, and gray for a `--set` CLI
  override, which wins over every file and is therefore read-only in the window. Fields that no
  file tier persists yet start out marked as unsaved, so newly added settings are easy to spot.
  Fields an `ISettingsValidator` complains about turn red (red wins over every other color) and
  every message is listed above the save buttons; saving anyway is allowed after a confirmation.

  **Save to Base / Machine / Local** writes only the fields you changed into that tier's file,
  leaving its other keys alone; saving to `Base` or `Machine` also drops any `Local` override that
  would shadow the new value, and saving to `Local` skips values that already match `Base`+`Machine`
  so the file never accumulates redundant overrides. Files that end up empty are deleted.
  **Revert** re-reads the cascade and drops unsaved edits. Footer foldouts show the merged JSON
  read-only and every tier file's path, with buttons to reveal it in the file browser or delete it.

  Without a `[SettingsClass]`-tagged class for the current base name, the window falls back to a
  read-only view listing each dotted path, its merged value, and the tier (`Base`/`Machine`/`Local`/
  `Cli`) that produced it.

  Known limitation: JSON keys are assumed to match C# field names, so `[JsonProperty]` renames
  aren't supported, and arrays are treated as single values rather than per-element ones.
