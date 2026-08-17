---
title: Settings validation
description: ISettingsValidator reports missing or out-of-range settings values in the editor.
---

Implement `ISettingsValidator` on a settings class, or on any object nested inside one, to report
values that are missing or out of range. Paths are relative to the validating object, so a nested
class validates its own fields without knowing where it sits in the settings tree.

## Example

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

## Types

| Member | Description |
| --- | --- |
| `void ISettingsValidator.Validate(SettingsValidationErrors errors)` | Called on the settings object and on any nested object that implements it. |
| `public IReadOnlyList<SettingsValidationError> Errors { get; }` | On `SettingsValidationErrors`. The read-only list the editor renders from. |
| `public bool HasErrors { get; }` | On `SettingsValidationErrors`. |
| `public void Add(string relativePath, string message)` | On `SettingsValidationErrors`. Appends a `SettingsValidationError`. |
| `public SettingsValidationError(string path, string message)` | Readonly struct constructor. |
| `public string Path { get; }` / `public string Message { get; }` | On `SettingsValidationError`. |
| `public override string ToString()` | Renders `"path: message"`, or just the message when the path is empty. |

Errors from an object inside a list or array are reported against the list field itself, since the
editor treats arrays as single values.

> [!NOTE]
> Validation is editor-only. The [Settings window](editor-windows.md) runs it on load and after
> every edit, but `SettingsFile<T>` never calls it — a build always boots with whatever the files
> say, valid or not.

## Known limitation

The walk that finds validators does not descend into structs (Unity's math types expose computed
properties that hand back more of themselves), so a validator nested inside a struct is never
called. A struct that implements `ISettingsValidator` itself is still called.
