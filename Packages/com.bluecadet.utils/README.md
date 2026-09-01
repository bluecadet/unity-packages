# Bluecadet Utils

Utilities for the parts of an installation build that sit outside the scene:
command-line arguments, a machine-aware YAML/JSON settings cascade with CLI overrides,
settings validation, and the editor windows that drive both.

Requires Unity 6000.3+ and `com.unity.nuget.newtonsoft-json`.

## Installation

```sh
openupm add com.bluecadet.utils
```

The `com.bluecadet` scoped registry only needs adding once per project. See
[Installing a Package](../../README.md#installing-a-package) for the manual
`manifest.json` form and the Git URL alternative. Release tags are
`com.bluecadet.utils@<version>` — pick one from
[Releases](https://github.com/bluecadet/unity-packages/releases).

## Usage

```csharp
using Bluecadet.Utils;

AppEnvironment env = AppEnvironment.Current;
string dataPath = env.DataPath;                  // --assetsPath, else streamingAssetsPath

SettingsFile<AppSettings> settings = env.SettingsFile<AppSettings>();
AppSettings value = settings.Value;              // merged cascade + --set overrides
```

Settings merge `settings.yaml`, `settings.<machineId>.yaml`, `settings.local.yaml`,
and repeatable `--set key.path=value` CLI overrides, in that order of precedence.
Each tier may also be a `.json` file (the `.yaml` form wins if both exist), and
editor saves into YAML tiers preserve their comments.

The package bundles YamlDotNet 18.1.0 (`Plugins/YamlDotNet.dll`, Auto Reference
off). If your project already carries its own `YamlDotNet.dll`, Unity reports a
duplicate-assembly error — remove the project-local copy to resolve it.

**Tools > Bluecadet** opens the Simulated Args and Settings editor windows.

## Documentation

Full documentation is in [`Documentation~/`](Documentation~/index.md):

- [CommandLineArgs](Documentation~/command-line-args.md)
- [AppEnvironment](Documentation~/app-environment.md)
- [Settings files](Documentation~/settings-file.md)
- [Settings validation](Documentation~/settings-validation.md)
- [Editor windows](Documentation~/editor-windows.md)

Release history is in [CHANGELOG.md](CHANGELOG.md).
