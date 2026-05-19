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

### 1. Bump the version

Edit `package.json` for the package you're releasing. Follow [Semantic Versioning](https://semver.org/).

### 2. Generate the changelog and determine the version

Run from inside the package directory. git-cliff infers the next version from your commits since the last release tag (`fix` → patch, `feat` → minor, `feat!` / `BREAKING CHANGE` → minor while on 0.x).

**First release** (no prior changelog entries to preserve):
```sh
cd Packages/com.bluecadet.spring
git cliff --bump -o CHANGELOG.md
```

**Subsequent releases** (preserves existing entries and any manual edits):
```sh
cd Packages/com.bluecadet.spring
git cliff --unreleased --bump --prepend CHANGELOG.md
```

To see the computed version before generating:
```sh
git cliff --bumped-version
# → com.bluecadet.spring@0.2.0
```

Review the output and edit if needed.

### 3. Commit and tag

Update `package.json` to match the version git-cliff computed, then:

```sh
git add Packages/com.bluecadet.spring
git commit -m "chore(spring): release 0.2.0"
git tag com.bluecadet.spring@0.2.0
git push origin main com.bluecadet.spring@0.2.0
```

The tag format is `{full-package-name}@{version}` — no `v` prefix, no shorthand.

If you need to override the computed version (e.g. releasing 1.0.0 intentionally), use `--tag` instead of `--bump`:
```sh
git cliff --unreleased --tag 1.0.0 --prepend CHANGELOG.md
```

### 4. CI takes it from here

Pushing the tag triggers the [Sign and Release](.github/workflows/release.yml) workflow, which:

1. Validates the tag version matches `package.json`
2. Signs the package with the Unity UPM CLI
3. Verifies the `.attestation.p7m` signature is present in the archive
4. Extracts release notes from the committed `CHANGELOG.md`
5. Creates a GitHub Release with the signed `.tgz` attached

OpenUPM picks up the release automatically via `trackingMode: githubRelease` and publishes the pre-signed tarball to the registry unchanged.

---

## Adding a New Package

### 1. Create the package directory

```
Packages/com.bluecadet.<name>/
  package.json
  README.md
  CHANGELOG.md
  <Name>.asmdef
  cliff.toml
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

### 2. Add a cliff.toml

Copy from an existing package and update the two package-specific lines:

```toml
[git]
tag_pattern = "com\\.bluecadet\\.<name>@(.*)"
include_paths = ["Packages/com.bluecadet.<name>/**"]
```

Everything else (body template, commit parsers) stays the same.

### 3. Add a CHANGELOG.md stub

```markdown
# Changelog

All notable changes will be documented here.
```

### 4. Register with OpenUPM

Submit a PR to [openupm/openupm](https://github.com/openupm/openupm) adding a package listing under `data/packages/`. Set `trackingMode: githubRelease` so OpenUPM fetches the signed `.tgz` from the GitHub Release instead of re-packing from source (which would discard the signature).

### 5. Update the root README

Add a row to the packages table in `README.md`.
