# LaunchpadDemo

Reference consumer project showing the intended way to wire
`com.bluecadet.utils` and `com.bluecadet.launchpad` together in an app.
Open it in Unity 6000.3, load `Assets/Scenes/Demo.unity`, and press Play —
it cold-boots from the seed content in `StreamingAssets/content` with no
controller running.

## What "ideal" looks like here

**One composition root.** `AppBootstrap` is the only file that knows the
whole object graph. It builds environment → settings → Launchpad pieces in
that order, ticks `ContentManager.TickMainThread()` in `Update()`, and
disposes everything in `OnDestroy()`. Nothing else touches construction.

**Config through the settings cascade.** `AppConfig` subclasses
`LaunchpadConfig` (which is deliberately file-agnostic) and adds app fields.
It is loaded with `AppEnvironment.Current.SettingsFile<AppConfig>()`, which
merges:

1. `StreamingAssets/settings.json` — base, committed
2. `settings.<machineId>.json` — per-machine override
3. `settings.local.json` — local override (git-ignore this in a real project;
   included here to show the tier working — it shortens `idleAfterSeconds`)
4. `--set key=value` CLI overrides

Inspect the merged result and per-field provenance in
**Project Settings → Project → Bluecadet**.

**Relocatable installs.** Both the settings files and `contentRoot` resolve
against `AppEnvironment.DataPath`, which is `StreamingAssets` by default but
honors `--assetsPath <dir>`. A kiosk install can point everything at a
writable data folder with a single launch flag; the code never changes.

**Multiple content models, one manager.** This version carries three record
types — `Slide`, `Sponsor`, and the singleton `ShowConfig` — all as a shared
base type, `Record`. `ContentManager<Record>` diffs and swaps them as one
flat list, one version, one ack. `DemoContentMapper` is the only place that
knows what the CMS exports: it dispatches per source file (not per parse
call — merging every file into one `ContentJsonFiles.ParseItems` stream
would leave nothing to say which model a token came from), routing
`slides.json` and `sponsors.json` to `ContentJsonFiles.ParseItems`, resolving
`Slide` image paths to absolute, and hashing every record with
`ContentHashing.Hash` so republished-but-identical content doesn't diff as
changed. Ids are namespaced per collection (`"slide:<id>"`, `"sponsor:<id>"`)
so an id reused across collections can't collide once merged into one list.
It throws on malformed content — including a missing or duplicated `config`
singleton — so a bad version is rejected whole. `AppBootstrap` regroups the
flat list back into typed views (`_slides`, `_sponsors`, `_showConfig`) in
`OnVersionApplied`, after the diff has already run.

**The `config` singleton.** `config/config.json` is a bare JSON object, not
a `{"data":[...]}` envelope or array — that's what makes it the singleton:
`ContentJsonFiles.ParseItems` silently skips object-shaped files, so
`DemoContentMapper` reads it directly with `JObject.Parse` and emits exactly
one `ContentItem<Record>` with the fixed id `"config:global"`, hashed via
`ContentHashing.Hash(JObject, "exportedAt")` to exclude the re-export
timestamp from change detection. A missing or duplicated singleton throws,
aborting the version load — the same failure mode as any other malformed
content.

**Idle-gated hot swap.** Utils' `IdleTimeout` drives Launchpad's `IdleGate`:
staged versions only apply while nobody is interacting, with
`maxSwapDeferSeconds` as the staleness backstop. Any input closes the gate
and resets the idle clock.

## Package references

This project lives inside the unity-packages repo, so `Packages/manifest.json`
uses relative `file:` references:

```json
"com.bluecadet.launchpad": "file:../../../Packages/com.bluecadet.launchpad",
"com.bluecadet.utils": "file:../../../Packages/com.bluecadet.utils"
```

A real project outside this repo should install released versions via the
openUPM scoped registry instead (see each package's README). The demo's
`LaunchpadDemo.asmdef` references the `Bluecadet.Launchpad` and
`Bluecadet.Utils` assemblies by name; Newtonsoft comes along transitively as
a precompiled assembly.

## Trying a content promotion

Without a controller the app just cold-boots the version named in
`StreamingAssets/content/manifest.json`. To simulate a new version by hand:
copy `versions/v001` to `versions/v002`, edit its `slides.json`,
`sponsors.json`, and/or `config.json`, point `manifest.json` at `v002`, and
restart. With a real Launchpad controller
running at `controllerUrl`, promotions arrive over SSE, stage in the
background (watch the HUD), and swap in once you stop touching the app for
`idleAfterSeconds` — or immediately via the HUD's "Apply staged now" button.
