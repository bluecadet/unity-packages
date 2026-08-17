---
title: Editor windows
description: Tools > Bluecadet opens two editor-only windows for simulating CLI args and editing the settings cascade.
---

**Tools > Bluecadet** opens two editor-only, dockable windows: `Tools/Bluecadet/Settings` and
`Tools/Bluecadet/Simulated Args`. Everything under `Editor/` is internal — these windows are a UI
surface, not a public API.

## Simulated Args

A text area editing `ProjectSettings/EditorSimulatedArgs.txt`, autosaved on every edit — the file
[CommandLineArgs.FromProcess()](command-line-args.md) reads while running in the editor. A
read-only preview shows how the current text parses.

## Settings

A typed editor for a settings base name (default `settings`). Tag your settings class with
`[SettingsClass]` (or `[SettingsClass("other")]` for a different base name):

```csharp
public SettingsClassAttribute(string baseName = "settings")
public string BaseName { get; }
```

and the window hydrates it from the [merged cascade](settings-file.md) and draws every field with
Unity's own property drawers.

### Field colors

| Color | Meaning |
| --- | --- |
| Red | An `ISettingsValidator` complains about this field. Wins over every other color. |
| Yellow | Unsaved change, including fields no file tier persists yet. |
| Blue | Effective value comes from the `Local` tier. |
| Green | Effective value comes from the `Machine` tier. |
| Gray | Effective value comes from a `--set` CLI override, which wins over every file and is therefore read-only in the window. |

Every validation message is listed above the save buttons; saving anyway is allowed after a
confirmation.

### Save / revert

- **Save to Base / Machine / Local** writes only the fields you changed into that tier's file,
  leaving its other keys alone. Saving to `Base` or `Machine` also drops any `Local` override that
  would shadow the new value. Saving to `Local` skips values that already match `Base`+`Machine`,
  so the file never accumulates redundant overrides. Files that end up empty are deleted.
- **Revert** re-reads the cascade and drops unsaved edits.
- Footer foldouts show the merged JSON read-only and every tier file's path, with buttons to
  reveal it in the file browser or delete it.

### Fallback view

Without a `[SettingsClass]`-tagged class for the current base name, the window falls back to a
read-only view listing each dotted path, its merged value, and the tier
(`Base`/`Machine`/`Local`/`Cli`) that produced it.

## Known limitation

JSON keys are assumed to match C# field names, so `[JsonProperty]` renames are not supported, and
arrays are treated as single values rather than per-element ones.
