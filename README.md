## Packages

| Package | Description |
|---|---|
| [com.bluecadet.hap](Packages/com.bluecadet.hap) | GPU-compressed HAP video playback |
| [com.bluecadet.launchpad](Packages/com.bluecadet.launchpad) | Client for the Launchpad controller: version tracking, staged sync, and gated hot-swap |
| [com.bluecadet.spring](Packages/com.bluecadet.spring) | Physics-based spring animations |
| [com.bluecadet.touchscreen](Packages/com.bluecadet.touchscreen) | Multi-touch input module for touchscreen installations |
| [com.bluecadet.uiblur](Packages/com.bluecadet.uiblur) | Kawase blur effect for UI elements (URP) |
| [com.bluecadet.uiblur-hdrp](Packages/com.bluecadet.uiblur-hdrp) | Kawase blur effect for UI elements (HDRP) |
| [com.bluecadet.utils](Packages/com.bluecadet.utils) | Utility functions and helpers |

## Installing a Package

### Via openUPM (recommended)

Install with the [openupm CLI](https://openupm.com/docs/getting-started.html#installing-openupm-cli):

```sh
openupm add com.bluecadet.spring
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
    "com.bluecadet.spring": "0.1.0"
  }
}
```

The scoped registry only needs to be added once per project regardless of how many `com.bluecadet` packages you install.

### Via Git URL

Add the following to `Packages/manifest.json`, replacing `[PACKAGE_NAME]` with the package folder name and `[RELEASE_TAG]` with the version tag:

```json
{
  "dependencies": {
    "com.bluecadet.spring": "https://github.com/bluecadet/unity-packages.git?path=Packages/[PACKAGE_NAME]#[RELEASE_TAG]"
  }
}
```

For example, to install `com.bluecadet.spring` at version `0.1.0`:

```json
{
  "dependencies": {
    "com.bluecadet.spring": "https://github.com/bluecadet/unity-packages.git?path=Packages/com.bluecadet.spring#com.bluecadet.spring@0.1.0"
  }
}
```

## Publishing Changes

See [CONTRIBUTING.md](CONTRIBUTING.md) for the full release process.
