## Packages

| Package | Description |
|---|---|
| [com.bluecadet.hap](Packages/com.bluecadet.hap) | GPU-compressed HAP video playback |
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
    "com.bluecadet.spring": "https://github.com/bluecadet/unity-packages.git?path=Packages/com.bluecadet.spring#spring/v0.1.0"
  }
}
```

## Publishing Changes

1. Update the version number in the `package.json` file of the package you are modifying. Follow [Semantic Versioning](https://semver.org/) guidelines.

2. Commit your changes with a descriptive message.

3. Create a new tag for the version you are publishing. Use the format `package-name/vX.Y.Z`, where `X.Y.Z` is the version number. Exclude `com.bluecadet.` from the package name in the tag. For example, for `com.bluecadet.spring` version `1.2.3`, the tag should be `spring/v1.2.3`.
    ```sh
    git tag -a spring/v1.2.3
    ```

4. Push the commit and the tag to the remote repository:
   ```sh
   git push --tags
   ```
