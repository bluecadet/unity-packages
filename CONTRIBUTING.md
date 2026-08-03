# Contributing

## Commit Messages

Use [Conventional Commits](https://www.conventionalcommits.org/). The type and optional scope determine how your commit appears in the changelog.

```
feat(spring): add overdamped mode
fix(hap): resolve scrubbing deadlock on Windows
refactor(utils): simplify singleton lifecycle
```

| Type | Changelog section |
|---|---|
| `feat` | Added |
| `fix`, `perf` | Fixed |
| `refactor` | Changed |
| `test`, `chore`, `docs`, `ci`, `style` | Omitted |

Commits that don't match any type land in **Changed**. Scope is optional but helps clarify intent in a multi-package repo.

---

## Releasing a Package

Releases are automated with [release-please](https://github.com/googleapis/release-please). You don't bump versions, write changelog entries, or tag anything by hand — just land conventional commits on `main`.

### 1. Land conventional commits on `main`

release-please reads the commit types in the table above to figure out what changed and how to bump the version (`fix`/`perf` → patch, `feat` → minor, `!`/`BREAKING CHANGE` → major, or minor while on `0.x`).

### 2. release-please opens a release PR

The [Release Please](.github/workflows/release-please.yml) workflow runs on every push to `main` and keeps one open release PR per package that has unreleased commits (`release-please-config.json` / `.release-please-manifest.json` at the repo root track per-package versions). The PR:

- Bumps the package's `package.json` version
- Adds a new `## [X.Y.Z](compare-url) (date)` section to that package's `CHANGELOG.md`

Review and edit the PR like any other pull request — it stays open and updates itself as you push more commits, until you merge it.

### 3. Merging the release PR ships it

When a release PR merges, release-please:

1. Tags the merge commit `{full-package-name}@{version}` (e.g. `com.bluecadet.spring@0.2.0`) — no `v` prefix, no shorthand
2. Creates a GitHub Release for that tag, with the changelog section as the release body

That tag push triggers the [Sign and Release](.github/workflows/release.yml) workflow, which:

1. Validates the tag version matches `package.json`
2. Signs the package with the Unity UPM CLI
3. Verifies the `.attestation.p7m` signature is present in the archive
4. Attaches the signed `.tgz` to the GitHub Release release-please already created
5. Verifies OpenUPM actually publishes the tagged version (`openupm/openupm-action`)

OpenUPM picks up the release automatically via `trackingMode: githubRelease` and publishes the pre-signed tarball to the registry unchanged.

### Repo setup

The Release Please workflow authenticates with a `RELEASE_PLEASE_TOKEN` repo secret — a personal access token, not the default `GITHUB_TOKEN`. This is required because tags/commits pushed by a workflow run using the default `GITHUB_TOKEN` don't trigger other workflows, so the tag release-please pushes would never fire the Sign and Release workflow above. Set `RELEASE_PLEASE_TOKEN` to a PAT (or GitHub App token) with `contents: write`, `pull-requests: write`, and `issues: write` access to this repo — the last is needed because release-please applies tracking labels (e.g. `autorelease: pending`) to its release PRs.

---

## Adding a New Package

### 1. Create the package directory

```
Packages/com.bluecadet.<name>/
  package.json
  README.md
  CHANGELOG.md
  <Name>.asmdef
  Scripts/
  Tests/
```

`package.json` minimum:

```json
{
  "name": "com.bluecadet.<name>",
  "version": "0.1.0",
  "displayName": "Bluecadet <Name>",
  "description": "...",
  "unity": "6000.3",
  "author": {
    "name": "Bluecadet",
    "url": "https://bluecadet.com"
  }
}
```

### 2. Add a CHANGELOG.md stub

```markdown
# Changelog

All notable changes will be documented here.
```

release-please prepends new version sections to this file automatically.

### 3. Register the package with release-please

Add an entry for the package's directory to both root-level files:

- `release-please-config.json`: add a `"Packages/com.bluecadet.<name>": { "component": "com.bluecadet.<name>" }` entry under `"packages"`
- `.release-please-manifest.json`: add `"Packages/com.bluecadet.<name>": "0.1.0"` matching the version in `package.json`

### 4. Register with OpenUPM

Submit a PR to [openupm/openupm](https://github.com/openupm/openupm) adding a package listing under `data/packages/`. Set `trackingMode: githubRelease` so OpenUPM fetches the signed `.tgz` from the GitHub Release instead of re-packing from source (which would discard the signature).

This listing PR must be merged **before** the package's first release-please release PR merges. Otherwise the `verify-openupm` job in [Sign and Release](.github/workflows/release.yml) fails on that first release, since OpenUPM has nothing to publish yet — the release itself still succeeds, and signing and tarball attachment are unaffected.

### 5. Update the root README

Add a row to the packages table in `README.md`.
