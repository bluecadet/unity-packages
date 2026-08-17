---
title: Versioning and releases
description: Why there is no repo-wide version number, what the release tags look like, and how to read a version before you upgrade.
---

## Packages version independently

There is no repo-wide version. Each of the seven packages has its own version, its
own changelog, and its own release cadence, and they are released separately. A
`com.bluecadet.hap` release says nothing about `com.bluecadet.spring`.

That is why this site has no version selector: a single site-wide version string
would be wrong for six packages out of seven. The current version of each package is
stated on its own page, and the full history is on the
[Releases](https://github.com/bluecadet/unity-packages/releases) page.

## Semantic versioning

Versions are [semver](https://semver.org/), bumped from the commits that landed:

| Change | Bump |
| --- | --- |
| Bug fix, performance work | Patch — `1.0.1` → `1.0.2` |
| New feature, backwards compatible | Minor — `1.0.1` → `1.1.0` |
| Breaking change | Major — `1.1.0` → `2.0.0` |

**Pre-1.0 packages are the exception.** While a package is on `0.x`, a breaking
change bumps the minor version, not the major — so `0.1.0` → `0.2.0` can break you.
`com.bluecadet.launchpad` is currently pre-1.0. Read its changelog before upgrading;
for the 1.x packages, a minor bump is safe.

## Release tags

Tags are `<full-package-name>@<version>`:

```
com.bluecadet.spring@1.0.1
com.bluecadet.hap@1.1.0
```

No `v` prefix, no shorthand name. This is the string a
[Git URL install](installing.md#git-url) needs after the `#`, and getting it wrong is
the usual cause of a 404 on install.

Each tag has a GitHub Release carrying that version's changelog section and the
signed `.tgz` for the package.

## What ships in a release

Every release is built the same way: the package is signed with Unity's UPM CLI, the
`.attestation.p7m` signature is verified to be present in the archive, the signed
tarball is attached to the GitHub Release, and OpenUPM publishes that same tarball
unchanged rather than re-packing from source.

So the artifact you get from OpenUPM, from the GitHub Release, and from a
[tarball install](installing.md#signed-tarball) is the same signed archive. A Git URL
install is the one route that bypasses this — it resolves the source tree at that
tag, which has no signature.

## Changelogs

Each package keeps a `CHANGELOG.md` beside its source, generated from the commits in
the release. Entries land in three sections:

| Section | From |
| --- | --- |
| Added | New features |
| Fixed | Bug fixes and performance work |
| Changed | Refactors, and anything else consumer-visible |

Docs, tests, CI, and chore commits are omitted. If something is missing from a
changelog, that is why.

## Upgrading

`openupm add com.bluecadet.<name>` re-run without a version takes the latest release;
`openupm add com.bluecadet.<name>@<version>` pins. Nothing upgrades on its own — an
installed version stays put until you move it.

Before a major bump, or a minor bump on a pre-1.0 package, read that package's
release notes. Between the two, the release notes are the more complete record: they
carry the full commit list, while the changelog carries the curated sections above.

## For maintainers

Cutting a release, adding a new package, and the conventional-commit conventions
behind all of the above are documented in
[CONTRIBUTING.md](https://github.com/bluecadet/unity-packages/blob/main/CONTRIBUTING.md).
