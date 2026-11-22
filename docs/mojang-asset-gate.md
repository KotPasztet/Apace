# Mojang asset gate

The Apace repo ships **only the manifest** of the additional Mojang assets the
server needs (character creator piece binaries, dressing room thumbnails, the
default character textures) — never the assets themselves. The asset gate is the
panel flow that, on first start, asks the operator for a **one-time consent**,
downloads the described files into the persistent data volume and then unblocks
server startup.

## How it works

1. Any start path (the full **Start** flow via `FileChecker.CheckAsync`, or a
   page-level component start via `ServerManager.EnsureComponentsOnline`) calls
   `MojangAssetGate.EnsureReadyAsync` first.
2. The gate loads `mojang-assets.manifest.json` (facts only: source URL, sha256,
   size, `verified` flag) and compares it with the persistent assets dir:
   - everything present → startup proceeds (and the post-processor runs);
   - consent already recorded but files missing → **silent auto-redownload**;
     if that fails, the prompt is armed and offers **Retry**;
   - no consent yet → the prompt is armed and startup parks on the gate's
     `TaskCompletionSource` (same pattern as the EULA prompt);
   - manifest itself unavailable → legacy persona fallback (see
     [Missing manifest policy](#missing-manifest-policy) below).
3. The prompt appears on the **Server Status** page: *"Additional Mojang assets
   required"*, describing what is downloaded, from where and into which
   directory, with an **Accept & Download** button. Accepting writes the
   one-time consent flag and downloads with per-file progress. Redeploys keep
   the volume — the prompt never comes back unless files go missing.
4. After every verified asset is in place, `MojangAssetPostProcessor.RunAll`
   runs (idempotently) — see below.

The gate is implemented in `src/Apace.LauncherUI/Services/MojangAssetGate.cs`;
the panel UI part lives in `Components/Pages/ServerStatus.razor`.

## Locations

| Item | Path |
|------|------|
| Assets dir (docker) | `/app/data/mojang-assets/` (`<DataDir>/mojang-assets`) |
| Sub-layout | `playfab/persona/`, `playfab/skinpacks/`, `images/`, `skins/` |
| Consent flag | `<assets dir>/consent.json` |
| Manifest (committed) | `staticdata-extras/playfab/mojang-assets.manifest.json` |

The manifest is read from (first existing wins, logged):
`../staticdata/playfab/` (the deployment layout — `publish.ps1` overlays
`staticdata-extras` onto `staticdata`), then `staticdata-extras/playfab/`
walked up from the working directory **and** from `AppContext.BaseDirectory`
(up to five levels, which covers dev `bin/Debug/net10.0` outputs), then the
panel's `../staticdata` (`Program.StaticDataDir`).

### Missing manifest policy

The manifest is a separate deliverable — a checkout or image can legitimately
carry none. In that case the gate does **not** silently pass; instead it falls
back to the legacy layout:

- If the legacy staticdata persona directory (`staticdata/playfab/persona/` or
  the committed `staticdata-extras/playfab/persona/` twins) still contains
  persona piece binaries, the start proceeds with a loud **warning** — the CDN
  controllers serve from staticdata and the gate cannot verify completeness.
- With **neither** manifest **nor** legacy persona assets the start is
  **blocked** (fail-closed, mirrors the ApiServer fail-fast) with a clear log
  message: generate the manifest or disable the gate in Server Options.
- The ApiServer direct start applies the same policy: no manifest + no legacy
  persona assets → `Log.Fatal` + exit 1 even with `--require-mojang-assets`.

## Consent format (`consent.json`)

```json
{ "AcceptedAt": "2026-01-01T00:00:00Z", "ManifestVersion": "1" }
```

`AcceptedAt` is a `DateTimeOffset` (round-trip format), `ManifestVersion` the
manifest `version` it was accepted for. The presence of the file is the consent.

## Manifest schema (`mojang-assets.manifest.json`)

```json
{
  "version": 1,
  "generatedAt": "2026-01-01T00:00:00Z",
  "sources": { "catalog": "...", "assetCdn": "...", "skinTextures": "..." },
  "items": [
    {
      "kind": "persona",                       // persona | thumbnail | skin-texture
      "file": "playfab/persona/<id>.zip",      // relative to the assets dir
      "url": "https://...",                    // recorded source (may be offline)
      "sha256": "hex or absent",
      "size": 12345,
      "verified": true,                        // reachable + hash confirmed at generation time
      "name": "Steve"                          // optional (skin textures)
    }
  ]
}
```

Only **`verified: true`** entries are downloaded and only they block startup.
Unverified entries (no reachable source at manifest generation time) are
informational — surfaced as "unavailable", never enforced. Regenerate with
`python3 scripts/generate_mojang_asset_manifest.py` (agent task; never commit
downloaded assets).

## Serving from the ApiServer

The ApiServer is started with `--mojang-assets-dir <path>` (always) and
`--require-mojang-assets` (when the settings toggle is on). The CDN controllers
serve the data-volume copy **first** with a fallback to the legacy locations:

- `PersonaCdnController` (`playfab/persona/{file}`): `<assets>/playfab/persona/`
  → `staticdata/playfab/persona/`;
- `SkinPackCdnController` (`playfab/skinpacks/{file}`): `<assets>/playfab/skinpacks/`
  → `staticdata/playfab/skinpacks/`;
- `MojangImagesCdnController` (`playfab/images/{file}`): `<assets>/images/` —
  used only when `UseStaticFiles` finds nothing in `wwwroot/playfab/images/`
  (static files middleware runs before MVC).

### Direct (panel-less) start

With `--require-mojang-assets` the ApiServer re-verifies the manifest at boot
and fail-fasts (`Log.Fatal`, exit code 1) when anything verified is missing.
Without the flag the check degrades to a warning.

## Settings toggle

**Configure → Require additional Mojang assets** (`RequireMojangAssets` in
`Settings.cs`, default **on**). When off, the gate lets every start through and
the ApiServer runs without the requirement flag; CDN serving still prefers the
data dir, falling back to staticdata.

### Interplay with `SkipFileChecks`

`SkipFileChecks` short-circuits the whole `FileChecker.CheckAsync` (as before,
this includes the gate). Two consequences:

- With `SkipFileChecks` on and the assets missing, the panel-side gate is
  skipped — but the ApiServer **still fail-fasts** on boot when
  `--require-mojang-assets` was passed. Page-level component starts
  (`EnsureComponentsOnline`) keep honouring the gate regardless of
  `SkipFileChecks`, which arms the consent prompt as the recovery path.
- `SkipFileChecks` does **not** clear the consent or the settings toggle.

## Downloads

Each file downloads to `<file>.part` first, is size- and sha256-checked
(when the manifest records them) and only then moved into place. Transient
failures (dead host, reset connection, truncated body) are retried up to
**3 attempts** per file (2 s / 4 s backoff); the per-request connect timeout
is 20 s and the overall request timeout 10 minutes. Files that still fail are
listed in the alert's error list with a **Retry** button — the gate stays
closed until every verified asset is in place.

## Post-processor hook (pipeline integration point)

`MojangAssetPostProcessor` runs after the gate considers the assets complete —
right after a successful download and idempotently on every start that finds
them complete. Steps must be registered at panel startup:

```csharp
MojangAssetPostProcessor.Register(async mojangAssetsDir =>
{
    // build/patch derived assets under mojangAssetsDir ...
});
```

Signature: `delegate Task PostProcessStep(string mojangAssetsDir)` — the
resolved absolute assets dir (`/app/data/mojang-assets` in docker). A failing
step is logged and does not block server startup. Steps must be idempotent
(re-running on a fully processed dir must be a no-op).
