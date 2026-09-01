---
title: Settings files
description: A cascading YAML/JSON settings loader that merges a base file, machine overrides, local overrides, and CLI --set flags into a plain class.
---

`SettingsFile<T>` loads and merges a cascade of YAML or JSON files, plus CLI `--set` overrides,
into a plain class — no `[Serializable]` required. It's sealed and has no public constructor;
obtain one via [AppEnvironment.SettingsFile\<T\>()](app-environment.md).

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
| Lowest | `SettingsTier.Base` | `settings.yaml` — shared base |
| | `SettingsTier.Machine` | `settings.<machineId>.yaml` — per-machine overrides |
| | `SettingsTier.Local` | `settings.local.yaml` — local overrides, typically git-ignored |
| Highest | `SettingsTier.Cli` | `--set key.path=value` — CLI overrides, repeatable |

Each file tier may be YAML or JSON: the loader probes for `<stem>.yaml` first, then falls back to
`<stem>.json`, so existing JSON projects work unchanged and formats can be mixed per tier. If both
files exist for one tier, the YAML file wins and a warning is reported. A tier that has no file yet
takes the `Base` tier's format when one is written — YAML when there is no `Base` file at all.

## YAML notes

- Plain (unquoted) scalars are typed per the **YAML 1.2 Core schema**: `true`/`false` (any of the
  three standard capitalizations), integers (`42`, `0xFF`, `0o17`), floats (`3.25`, `1e3`, `.inf`,
  `.nan`), and nulls (`~`, `null`/`Null`/`NULL`, empty). Quoted, literal (`|`) and folded (`>`)
  scalars are always strings.
- The YAML 1.1 booleans `yes`/`no`/`on`/`off` are **plain strings**, not booleans.
- Only the `.yaml` extension is recognized — a `.yml` file is ignored.
- Merge keys (`<<:`) are not interpreted; `<<` comes through as a literal key. Plain anchors and
  aliases do resolve on read; an alias that points into itself does not.
- A tier's root must be a mapping and its keys must be unique; a non-mapping root or a duplicate key
  is a malformed tier, so it logs a warning and is skipped. Only the first document of a
  multi-document file is read. A file that is empty, holds nothing but comments, or holds nothing
  but a null literal (`~`, `null`/`Null`/`NULL`) or an empty flow mapping (`{}`) is a valid, empty
  tier. Saving into one appends the new entries, or writes them over the `~`/`{}`; the surrounding
  comments and blank lines stay put either way.
- Editor saves preserve comments and formatting outside the edited value, but replacing an object
  or array rewrites that whole subtree (comments inside it are lost). A comment on the edited key's
  own line survives, on the rewritten key line.
- The editor cannot save into flow-style collections (`{...}`, `[...]` with entries) or anchored
  values — an anchored empty root (`&a ~`) included; it reports an error and leaves that file
  untouched. Rewrite those spots in block style to edit them in the Settings window.
- An empty `{}` is the exception — at the root or nested, a save replaces it, so a nested `a: {}`
  does not come back after a set-then-remove: the set consumes the placeholder and the removal
  prunes the emptied parent. A sequence root (`[]`, or a top-level `- item` list) is never editable,
  since a tier's root has to be a mapping.
- A `Base` or `Machine` save fails without writing anything when the `Local` file is malformed in
  any way: flow-style or anchored, but equally a duplicate key, tab indentation, an unclosed flow
  collection, or bad indentation. The `Local` shadow strip is parsed in memory before anything
  reaches disk, so the target file is left as it was. The reported error names the path of the file
  it could not edit and states that nothing was written.
- That guarantee does not cover a `Local` file that is well-formed but unwritable (read-only, or a
  permissions problem): the strip succeeds in memory, the target file is written, and only then does
  the `Local` write fail, so the shadowing override survives. The error reports that write failing
  rather than claiming nothing was written.
- Multiline strings are written as double-quoted escaped strings (never literal blocks), and new
  entries are always emitted in block style, two spaces per level.
- Saves preserve line endings: a rewritten line keeps its own terminator, and an inserted or
  appended entry takes the ending of the line it follows, so a CRLF file stays CRLF. An
  unterminated last line has no ending to copy, so the file's first ending stands in there (`\n`
  for a file with none) — the one spot where a mixed-ending file can pick up the other ending.
  CR-only (classic Mac) line endings are not handled.
- A new key is inserted after the previous sibling's trailing indented comments — insert and
  removal share one rule for which lines belong to a subtree — so adding a key and removing it
  again is a byte-exact no-op, and a comment belonging to a neighbouring block is never adopted or
  destroyed. Inserting into a file that does not end in a newline adds one, and a later removal
  does not take it back.
- A YAML tier the editor strips down to nothing but comments is kept, since it still reads as an
  empty tier; only a file left blank is deleted.

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
| `public string PathFor(SettingsTier tier)` | The on-disk path for a tier — its `.yaml` file, else its `.json` file, else the path a save would create — or null for `SettingsTier.Cli`, which has no file. |
| `public SettingsTier? TierFor(string dottedPath)` | The tier that produced the effective value at a dotted path, or null if no tier sets it. |
| `public void Reload()` | Re-reads and re-merges every tier; `OnReloaded` fires afterward. |

## See also

- [Settings validation](settings-validation.md) for reporting bad values in the editor.
- [Editor windows](editor-windows.md) for the typed Settings window that reads and writes this
  cascade.
