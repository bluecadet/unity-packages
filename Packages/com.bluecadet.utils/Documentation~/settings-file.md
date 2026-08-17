---
title: Settings files
description: A cascading JSON settings loader that merges a base file, machine overrides, local overrides, and CLI --set flags into a plain class.
---

`SettingsFile<T>` loads and merges a cascade of JSON files, plus CLI `--set` overrides, into a
plain class — no `[Serializable]` required. It's sealed and has no public constructor; obtain one
via [AppEnvironment.SettingsFile\<T\>()](app-environment.md).

## A settings class

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

## Cascade

All files are resolved under `AppEnvironment.DataPath`. Filenames derive from the `baseName`
passed to `SettingsFile<T>()` (default `"settings"`).

| Precedence | Tier | File / source |
| --- | --- | --- |
| Lowest | `SettingsTier.Base` | `settings.json` — shared base |
| | `SettingsTier.Machine` | `settings.<machineId>.json` — per-machine overrides |
| | `SettingsTier.Local` | `settings.local.json` — local overrides, typically git-ignored |
| Highest | `SettingsTier.Cli` | `--set key.path=value` — CLI overrides, repeatable |

## Merge semantics

Each file tier is parsed and merged with `JObject.Merge` using `MergeArrayHandling.Replace`, so
arrays replace rather than concatenate. A missing tier is silently skipped; a malformed tier logs
a warning and is skipped. If every tier is unusable, `Value` falls back to `new T()`.

## --set parsing

`--set` values are parsed as JSON literals where possible:

```
--set general.targetFrameRate=30   # int
--set general.debugMode=true       # bool
```

Values that aren't valid JSON fall back to a plain string. `--set` is repeatable; every occurrence
is applied, and later occurrences win for the same path.

## Other members

| Member | Description |
| --- | --- |
| `public event Action<T> OnReloaded` | Fires after `Reload()` re-merges the cascade. |
| `public T Value { get; }` | The current merged value. |
| `public IReadOnlyList<string> LoadedPaths { get; }` | The file tiers that actually loaded. |
| `public string PathFor(SettingsTier tier)` | The on-disk path for a tier, or null for `SettingsTier.Cli`, which has no file. |
| `public SettingsTier? TierFor(string dottedPath)` | The tier that produced the effective value at a dotted path, or null if no tier sets it. |
| `public void Reload()` | Re-reads and re-merges every tier; `OnReloaded` fires afterward. |

## See also

- [Settings validation](settings-validation.md) for reporting bad values in the editor.
- [Editor windows](editor-windows.md) for the typed Settings window that reads and writes this
  cascade.
