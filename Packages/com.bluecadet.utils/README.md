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
    "com.bluecadet.utils": "0.1.6"
  }
}
```

**Via Git URL**

```json
{
  "dependencies": {
    "com.bluecadet.utils": "https://github.com/bluecadet/unity-packages.git?path=Packages/com.bluecadet.utils#utils/v0.1.6"
  }
}
```
