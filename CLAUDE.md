# CLAUDE.md — Apace (Solace reimplementation)

Working notes for Claude Code. Keep this file updated with anything a future
session needs to know to work in this repo effectively.

## What this is

Apace is a reimplementation of the Minecraft Earth server backend ("Solace").
Mixed C# / Java project:

| Piece | Tech | Where |
|---|---|---|
| Orchestrator (LauncherUI dashboard, ApiServer, BuildplateLauncher) | C# / .NET 10 | `src/` |
| Fabric mod "Fountain" (MC 1.20.4 Java, multiworld/buildplates) | Java / Gradle | `repos/Fountain-fabric` |
| Bedrock↔Java bridge "Vienna" + connector plugin | Java / Maven | `repos/Vienna` |
| Static game data | submodule | `staticdata` (github.com/Earth-Restored/Solace.StaticData) |

`repos/*` (Fountain-fabric, Vienna, Fountain-bridge, Fountain-connector-plugin-base)
are **independent git repositories, git-ignored** by the main repo — commit and
push them separately (they live under the KotPasztet/Earth-Restored GitHub orgs;
check `git remote -v` in each).

## Architecture (v2 — persistent multiworld server)

One **persistent Fabric server** hosts ALL buildplate instances as dynamically
created dimensions (multiworld). No new server JVM per instance (that was v1).

- `PersistentProcessManager` (C#) starts the Fabric server JVM and the persistent
  Vienna bridge JVM, both as subprocesses of the BuildplateLauncher.
- **Control channel**: TCP `127.0.0.1:25564`, newline-delimited JSON.
  Fountain mod `ControlServer.java` listens (port from `-Dfountain.control.port`,
  registered on `SERVER_STARTED`). Vienna `ControlChannelClient.java` connects.
  Requests: `createInstance`, `destroyInstance` (worldData = base64 zip of
  region+entities, v1-compatible format), `bindPlayer`, `unbindPlayer`.
- **Event bus** (`Solace.EventBus.*`, TCP 5532): C# ↔ ViennaConnectorPlugin
  (persistent bridge listens on queue `buildplate_persistent`; per-instance
  routing uses queue `buildplate_<instanceId>`).
- Instance lifecycle: C# `Instance.cs` sends `createInstance`/`destroyInstance`
  over the event bus → connector plugin forwards over the control channel →
  Fountain creates/destroys the dimension. `destroyInstance` publishes `saved`
  (with worldData) before replying so C# can persist the buildplate.
- Java subprocess stdout/stderr is piped into Serilog with
  `ComponentName="Java Server"` → shows up as its own tab in the web UI
  Live Logs, plus a status card on the Server Status page.

### Ports
5000 public HTTP API · 1808 launcher UI · 5532 event bus TCP ·
19132/udp shared Bedrock bridge port · 25565 Fabric server ·
25564 control channel (localhost only)

## Building

```bash
# Fountain mod → artifact copied into Apace/mods/fountain-0.0.1.jar
cd repos/Fountain-fabric && ./gradlew :fountain-core:build

# Vienna connector plugin → Apace/server_jars/buildplate-connector-plugin-*.jar
cd repos/Vienna/buildplate/connector-plugin && ../../mvnw package

# C# (publishes build layout used by Docker image)
pwsh ./publish.ps1 -profiles "framework-dependent-linux-x64"
```

`publish.ps1` copies `mods/*.jar` into the published
`staticdata/server_template_dir/mods/`, so **rebuild the Java JARs and copy them
into `mods/` + `server_jars/` before publishing the Docker image** — stale JARs
there have caused bugs more than once (see Gotchas).

## Running / deploying

- Local dev: `./run.sh` (or `run.ps1`) — wraps the installed instance in
  `~/apace` via docker-compose.
- Docker: `docker-compose.yml` (main) / `docker-compose.dev.yml` (dev image
  `ghcr.io/kotpasztet/apace:dev`). Images built by GitHub Actions
  (`.github/workflows/docker-image*.yml`) and deployed on a VPS with persistent
  bind mounts under `/opt/apace-persistent/`:
  - `server-template-dir` → `/app/staticdata/server_template_dir` (Fabric JAR,
    libraries, `mods/`)
  - `fabric-data` → `/app/launcher/persistent_fabric` (persistent server world)
  - `launcher-data`, `config.json`, `logs`, `resourcepacks`, `data`, …
- The container entrypoint (`/app/entrypoint.sh`, generated inline in the
  Dockerfile) syncs `/app/defaults/mods/*.jar` (baked into the image) into the
  volume-mounted `server_template_dir/mods/` on startup.

## Git conventions

- Work happens on **`dev`**; never merge/push to `main` unless explicitly told to.
- Commit messages in English, conventional style (`feat:`, `fix:`).
- `repos/*` subprojects have their own history — commit there too when their
  code changes, otherwise the main-repo Docker build can reference JARs that
  don't match any committed source.

## Gotchas / lessons learned

- **Stale JARs in the `server-template-dir` volume**: bind mounts shadow image
  content, so a fresh image does NOT update mods in the volume. The entrypoint
  syncs them (compares and overwrites) — if mods ever look old on the server,
  check `/opt/apace-persistent/server-template-dir/mods/` first, before
  suspecting the Java code.
- Symptom of a Fountain mod without the control channel: bridge logs
  `Failed to create instance ... "control channel request failed"`
  (nothing listens on 25564 → instant connection refused). Check the
  "Java Server" log tab for `Control server listening on localhost port 25564`.
- `Exception while cleaning up runtime directory: Could not find a part of the
  path '/tmp/vienna-buildplate-instance_...'` after a failed start is benign
  (double cleanup), not the root cause.
- Fountain mod JAR is a Fabric jar-in-jar: classes live inside
  `META-INF/jars/fountain-core-0.0.1.jar` — check nested JAR contents, not the
  outer one, when verifying what's deployed.
- GitHub push needs credentials (token); if push fails, ask the user.
