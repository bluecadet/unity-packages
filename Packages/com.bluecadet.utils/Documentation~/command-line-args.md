---
title: CommandLineArgs
description: A pure C# parser for command-line style arguments, with case-insensitive lookup and an editor-only simulated-args file.
---

`CommandLineArgs` is sealed and has no public constructor — build one with `FromProcess()`,
`Parse()`, or `ParseText()`.

## Syntax

The parser accepts three forms:

- `--flag value` — a named argument
- `--key=value` — a named argument, `=`-separated
- `--flag` — a bare flag with no value

Names are case-insensitive and leading dashes are normalized. When a name repeats, the last
occurrence wins. `All` is keyed the same normalized way as `Get`, `TryGet`, and `HasFlag`.

## Factories

| Member | Behavior |
| --- | --- |
| `public static CommandLineArgs FromProcess()` | In a build, parses `Environment.GetCommandLineArgs()`. In the editor, reads the simulated-args file instead. |
| `public static CommandLineArgs Parse(params string[] argv)` | Parses an explicit argv array directly, bypassing `FromProcess()` and `ParseText()`'s tokenizer. Useful in tests. |
| `public static CommandLineArgs ParseText(string text)` | Tokenizes a single string, honoring quoted tokens (e.g. `--name="Blue Cadet"`). |

```csharp
using Bluecadet.Utils;

CommandLineArgs args = CommandLineArgs.FromProcess();

if (args.HasFlag("verbose")) { /* ... */ }
string env = args.Get("env", fallback: "production");
if (args.TryGet("port", out string port)) { /* ... */ }
```

## Lookup

| Member | Description |
| --- | --- |
| `public bool HasFlag(string name)` | True if the name was passed, with or without a value. |
| `public string Get(string name, string fallback = null)` | The value for a name, or `fallback` if not present. |
| `public bool TryGet(string name, out string value)` | True and sets `value` if the name was passed. |
| `public IReadOnlyDictionary<string, string> All { get; }` | Every parsed argument, for enumeration rather than one-name-at-a-time lookup. |

## Simulated args in the editor

`FromProcess()` can't read real process arguments in the editor, so it reads a text file at
exactly `ProjectSettings/EditorSimulatedArgs.txt` (resolved relative to the project root) instead.
Write it exactly as you'd type arguments on a command line:

```
--env=staging --port 8080 --verbose
```

A missing file is treated as no arguments. Edit this file directly, or use the
[Simulated Args editor window](editor-windows.md).
