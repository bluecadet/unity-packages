---
title: AppEnvironment
description: An immutable snapshot of data path, machine identity, and parsed command-line arguments.
---

`AppEnvironment` is sealed and holds the data an app needs at startup: where its data lives, what
machine it's running on, and the arguments it was launched with. It's also the entry point for
constructing [SettingsFile\<T\>](settings-file.md) instances.

## Current

```csharp
using Bluecadet.Utils;

AppEnvironment env = AppEnvironment.Current;
```

| Member | Source |
| --- | --- |
| `public string DataPath { get; }` | `--assetsPath`, else `Application.streamingAssetsPath`. |
| `public string MachineId { get; }` | `--machineId`, else `Environment.MachineName`. |
| `public CommandLineArgs Args { get; }` | The parsed [CommandLineArgs](command-line-args.md) this environment was built from. |

## ResolvePath

```csharp
string absolute = env.ResolvePath("some/relative/file.json");
```

`public string ResolvePath(string pathOrRelative)` resolves a path relative to `DataPath`.

## Constructing in tests

Build an isolated instance instead of touching `Current`:

```csharp
AppEnvironment env = new AppEnvironment(tempDir, "CI", CommandLineArgs.ParseText("--verbose"));
```

`public AppEnvironment(string dataPath, string machineId, CommandLineArgs args)` throws
`ArgumentNullException` if `args` is null.

## SettingsFile

```csharp
SettingsFile<AppSettings> settings = env.SettingsFile<AppSettings>();
```

`public SettingsFile<T> SettingsFile<T>(string baseName = "settings") where T : class, new()` is
an instance method (not an extension method) that builds a [settings cascade](settings-file.md)
rooted at `DataPath`. Pass `baseName` to load a settings file other than `settings.json`.
