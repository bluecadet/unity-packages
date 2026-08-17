---
title: Installing
description: The four ways to add a Bluecadet package to a Unity project — OpenUPM, a Git URL, or a signed tarball — and what each one costs you.
---

Every package in this repo installs the same way, and they are independent of each
other: install one, or all seven, in any combination. There is no meta-package and
no shared runtime to install first.

Pick a route:

| Route | Use it when |
| --- | --- |
| [OpenUPM](#openupm-recommended) | Normal case. Resolves updates, handles dependencies, one-line install. |
| [Git URL](#git-url) | You want a specific tag (or an unreleased commit) without a registry. |
| [Signed tarball](#signed-tarball) | The build machine has no network access to the registry. |

Whichever route you take, check the [requirements](requirements.md) first — three of
the packages need something already installed in your project that their manifests
do not declare.

## OpenUPM (recommended)

With the [openupm CLI](https://openupm.com/docs/getting-started.html#installing-openupm-cli),
run this from the project root — the directory containing `Assets/` and `Packages/`:

```sh
openupm add com.bluecadet.spring
```

That adds the scoped registry to `Packages/manifest.json` if it isn't there yet, then
adds the package at its latest published version.

To do it by hand instead, add the registry and the dependency yourself:

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
    "com.bluecadet.spring": "1.0.1"
  }
}
```

The `scopedRegistries` block is per project, not per package. Add it once and every
`com.bluecadet.*` package resolves from it.

### Pinning and updating

`openupm add` without a version takes the latest. Append `@<version>` to pin:

```sh
openupm add com.bluecadet.spring@1.0.1
```

To move a pinned package, run `openupm add` again with the new version, or edit the
version string in `Packages/manifest.json` directly. Unity re-resolves on focus.

To remove one:

```sh
openupm remove com.bluecadet.spring
```

## Git URL

No registry needed, but you give up dependency resolution and update prompts — Unity
locks the package to the exact commit the tag points at, and nothing will tell you
when a newer version ships.

```json
{
  "dependencies": {
    "com.bluecadet.spring": "https://github.com/bluecadet/unity-packages.git?path=Packages/com.bluecadet.spring#com.bluecadet.spring@1.0.1"
  }
}
```

Three parts matter:

- `?path=Packages/<package-name>` — this is a monorepo, so the path segment is what
  selects the package.
- `#<tag>` — release tags are `<full-package-name>@<version>`. No `v` prefix, no
  shorthand: `com.bluecadet.spring@1.0.1`, not `spring@1.0.1` or `v1.0.1`. See
  [versioning](versioning.md).
- Git must be on the `PATH` of whatever machine resolves the package, including CI
  and build agents.

Every released tag is on the [Releases](https://github.com/bluecadet/unity-packages/releases)
page. Omitting `#<tag>` tracks the default branch, which is unpinned and will change
under you — don't do it for anything you plan to ship.

## Signed tarball

Each release attaches a `.tgz` signed with Unity's UPM CLI, with the
`.attestation.p7m` signature inside the archive. This is the same artifact OpenUPM
serves, so an air-gapped install is byte-identical to a registry install.

Download the `.tgz` from the [release](https://github.com/bluecadet/unity-packages/releases),
drop it somewhere in the project (a `LocalPackages/` directory beside `Packages/` is
the usual convention), and reference it with a `file:` path:

```json
{
  "dependencies": {
    "com.bluecadet.hap": "file:../LocalPackages/com.bluecadet.hap-1.1.0.tgz"
  }
}
```

`file:` paths in `manifest.json` resolve relative to the `Packages/` directory, which
is why the example starts with `../`.

Transitive dependencies are not bundled. A tarball install of a package that depends
on something from the Unity registry (`com.unity.collections`, `com.unity.burst`,
Newtonsoft JSON) still needs that dependency resolvable — pull it in ahead of time,
or the project won't compile.


## Verifying the install

Open **Window > Package Manager** and switch the scope to **In Project**. Packages
installed from OpenUPM list under **Packages - OpenUPM** with a version and a
changelog link; Git and tarball installs list their source instead of a version.

## Troubleshooting

**`Package [com.bluecadet.x] cannot be found`**

The scoped registry is missing, or its `scopes` array doesn't include
`com.bluecadet`. Check `Packages/manifest.json` against the block above. A registry
entry scoped to a single package (`com.bluecadet.spring`) works but has to be
repeated for each one — scope the whole `com.bluecadet` prefix instead.

**The package installs, then the project won't compile**

Almost always a missing undeclared dependency: URP for `com.bluecadet.uiblur`, HDRP
for `com.bluecadet.uiblur-hdrp`, the Input System for `com.bluecadet.touchscreen`.
None of the three declare it in `package.json`. See [requirements](requirements.md).

**A Git URL install 404s**

The tag is wrong. It is `com.bluecadet.spring@1.0.1` — full package name, `@`, bare
version. Confirm the exact string on the
[Releases](https://github.com/bluecadet/unity-packages/releases) page, and confirm
`?path=` names the same package.

**`DllNotFoundException` from `com.bluecadet.hap`**

The Hap decoder is a native plugin, shipped prebuilt for macOS, Windows, and Linux on
arm64 and x86_64. Anything outside those six targets has no binary to load. The other
six packages are pure C# and run wherever Unity does.

**Unity doesn't pick up a manifest edit**

Package resolution runs when the editor regains focus. Alt-tab away and back, or use
**Package Manager > Refresh**.
