# Migrating from Solace

Already running a Solace server and want to move to Apace? The migration moves
your players, their progress and their buildplates into an Apace data
directory.

The one-line idea: **stop both servers, run the migration once, start Apace —
players log in with their old username and password and find their stuff.**

## Quick migration (recommended)

One command detects your Solace install, installs Apace if it is missing, stops
both servers, always shows a dry-run plan first and migrates after a single
confirmation:

**Linux / macOS / Termux / VPS:**

```bash
curl -sSL https://raw.githubusercontent.com/KotPasztet/Apace/main/scripts/migrate-from-solace.sh | bash
```

**Windows (PowerShell):**

```powershell
iwr https://raw.githubusercontent.com/KotPasztet/Apace/main/scripts/migrate-from-solace.ps1 | iex
```

What the wrapper does:

1. Finds Solace automatically (`~/solace/solace-server`, `~/solace`, `~/Solace`,
   `$SOLACE_DIR` — or `%USERPROFILE%\solace\solace-server` on Windows).
2. Makes sure Python 3 is available (offers to install it on Linux/Termux).
3. Installs Apace if it is not installed yet (Docker if possible, otherwise the
   bare-metal / Termux installer), and stops the freshly started stack.
4. Stops Solace (systemd unit `solace.service` where present) and Apace.
5. Always runs the converter's `--dry-run` first and shows you the plan.
6. After your confirmation, migrates (the converter backs up the Apace target
   first) and prints the next steps.

Options (pass after `bash -s --` on Linux, or download the script and run it
with flags on Windows):

| Flag | Meaning |
|---|---|
| `--solace-dir <path>` / `-SolaceDir <path>` | Solace directory (skips auto-detect) |
| `--target <dir>` / `-Target <dir>` | Apace persistent data dir (default: `/opt/apace-persistent` on Docker, `~/apace` bare, `C:\apace-persistent` on Windows Docker) |
| `--docker` / `--no-docker` / `-Docker` / `-NoDocker` | Force the install mode |
| `--dry-run` / `-DryRun` | Show the plan and stop — write nothing |
| `--no-backup` / `-NoBackup` | Skip the pre-migration backup |
| `--yes` / `-y` / `-Yes` | No prompts |

Example — a VPS where Solace lives in a custom place:

```bash
curl -sSL https://raw.githubusercontent.com/KotPasztet/Apace/main/scripts/migrate-from-solace.sh | bash -s -- \
    --solace-dir /home/me/solace/solace-server --target /opt/apace-persistent
```

```powershell
iwr https://raw.githubusercontent.com/KotPasztet/Apace/main/scripts/migrate-from-solace.ps1 -OutFile migrate-from-solace.ps1
.\migrate-from-solace.ps1 -SolaceDir "$env:USERPROFILE\solace\solace-server" -Target C:\apace-persistent
```

The wrapper never modifies or deletes anything in the Solace directory (it is
only ever read), and it never deletes the Solace install — retiring it is a
manual step for after you have verified Apace.

## What migrates

| Data | Where it ends up in Apace |
|---|---|
| Accounts (username, password salt/hash, first/last name, created date) | `data/live.db` → `Accounts` |
| Profile (health, experience, level, rubies) | `data/earth.db` → `objects` |
| Boosts, hotbar, inventory, journal, redeemed tappables | `data/earth.db` → `objects` |
| Tokens (level-up, journal unlocks, daily login) | `data/earth.db` → `objects` |
| Daily-login history (claimed dates) | derived into each player's `tokenClaims` object |
| Crafting + smelting slots (active jobs included) | `data/earth.db` → `objects` |
| Activity log (last 40 entries) | `data/earth.db` → `objects` |
| Player buildplates (names, sizes, metadata) | `data/earth.db` → `objects` |
| Shared + encounter buildplates | `data/earth.db` → `objects` |
| `object_store/` (world data blobs) | `data/object_store/` |
| Resourcepacks (`java/`, `vanilla.zip`, `genoa_cache/`) | `resourcepacks/` |
| `server_template_dir/eula.txt` | `server-template-dir/eula.txt` (only if Apace has none) |
| Panel users, roles, logins, claims | `launcher-data/app.db` (best effort, see below) |
| Panel → game account links | `LinkedGameAccounts` table (re-linked to the new player ids) |
| Settings: `ApiPort`, `IPv4`, `OnlyAllowLocalLogin`, `MapTilerApiKey`, `TileDataSource`, `SkipFileChecks` | `config.json` (only if Apace has none yet) |

Solace stores its database payloads with PascalCase JSON keys while Apace uses
camelCase. The script re-serializes every object through explicit per-type key
maps, so the migrated data is byte-compatible with what Apace itself would
have written.

## What does NOT migrate

- **Login sessions / secrets** — every player must log in again after the move.
- **Tiles** — Apace re-renders them (they are derived data).
- **Template buildplates** — Apace re-imports them from its own staticdata.
- **Panel buildplate previews** — Apace regenerates previews.
- **Connection strings and ports** that Apace derives itself
  (`EarthDatabaseConnectionString`, `LiveDatabaseConnectionString`,
  `BuildplateBasePort`) are never copied into `config.json`.
- If the Solace panel database (`launcher/Data/app.db`) has drifted from the
  schema Apace expects, panel users are skipped with a clear message —
  recreate them in Apace. (Everything else still migrates.)

## Manual migration (the converter directly)

The quick migration above is only an orchestrator; the conversion itself is
`scripts/migrate-from-solace.py` — a single Python 3 script using only the
standard library, so it has no install step and can run on any machine that
reaches the data directories. Use it directly when you need exact control
(for example when the wrappers cannot detect your layout):

### Prerequisites

1. **Both servers must be stopped.** The script reads Solace's SQLite files
   directly; migrating a running server can produce torn data. Stop the
   Solace services, and make sure the Apace container is down (`docker compose
   -f docker-compose.dev.yml down`) if you are migrating into its data dir.
2. Python 3.8+ on the machine you run the script from (any Linux/macOS box
   that can reach the data directories; on Windows use `py -3`).
3. Enough disk space for the backup the script creates by default.

### Dry run first

Always start with `--dry-run` — it validates the Solace layout, cross-checks
every account id, and prints the full plan (per-table row counts, objects to
write, files to copy, warnings) without writing anything:

```bash
python3 scripts/migrate-from-solace.py --dry-run
```

Defaults: the script looks for Solace at `~/solace/solace-server`
(`%USERPROFILE%\solace\solace-server` on Windows) and targets
`/opt/apace-persistent`. Both can be overridden:

```bash
python3 scripts/migrate-from-solace.py \
    --solace-dir /home/me/solace/solace-server \
    --target /opt/apace-persistent
```

The script asks for confirmation before touching anything; `--yes` skips the
prompt (use it in automation only after you have read a dry run).

### What the script does

1. Validates the Solace layout (`data/earth.db`, `staticdata/`,
   `data/object_store/`, panel `app.db`) and verifies all required earth.db
   tables exist — it aborts with a clear message if something is missing.
2. Maps every Solace account (a GUID) to Apace's 16-hex player id
   (`sha256(username)` prefix) and cross-checks the stored id against it —
   mismatches are warned about; duplicate ids are skipped with a warning.
   Accounts without a username are skipped.
3. Makes a timestamped `tar.gz` backup of the target `data/` +
   `config.json` into a sibling `apace-backup-<timestamp>/` directory
   (disable with `--no-backup`).
4. Copies `object_store/`, the resourcepacks and `eula.txt` into the target.
5. Creates/updates `data/live.db` (accounts) and `data/earth.db`
   (`objects` table, `version = 1`). Existing live.db accounts are not
   duplicated — conflicting ids or usernames are skipped with a warning;
   earth.db objects are merged with `INSERT OR REPLACE`.
6. Best-effort copies the panel database and re-links panel users to their
   game accounts.
7. Writes `config.json` only if the target has none; an existing one is
   reported but left untouched.
8. `chown -R 1654:1654` on the target (skipped on Windows).

## Verification checklist

After the migration and before letting players in:

- [ ] The dry run reported the expected account count and no unexpected warnings.
- [ ] The final report lists the accounts/objects/files you expected.
- [ ] `config.json` contains the API port you want (1808 default) — edit it if
      Solace used a different port than Apace's published Docker port.
- [ ] Start Apace and open the panel: users are listed and can log in.
- [ ] Log into the game with an old Solace account: profile, inventory, hotbar
      and journal are intact.
- [ ] Enter a migrated buildplate — the world appears on first entry.
- [ ] Daily login: previously claimed days are remembered (streak restarts at
      the current day — Solace did not track streak history).

## Rollback

Stop Apace, then restore the backup created before the migration:

```bash
cd /opt            # parent of apace-persistent/
ls apace-backup-*/                       # pick the timestamp you want
tar -xzf apace-backup-<timestamp>/apace-data-<timestamp>.tar.gz -C apace-persistent/
```

This restores `data/` and `config.json` to their pre-migration state. Apace
does not modify the Solace directory — the source is only ever read — so the
old server can always be brought back up as-is.

## Windows note

Solace-on-Windows installs live under `%USERPROFILE%\solace\solace-server`; the
PowerShell one-liner at the top of this guide detects it, stops the Solace
panel processes and asks before killing anything. Run the converter directly
with `py -3 scripts\migrate-from-solace.py --dry-run` from the Apace checkout
and pass `--target` pointing at your Apace data directory. The final `chown`
step is skipped on Windows; if the target is a Linux Docker volume, run one
more `chown -R 1654:1654` from the VPS (or `docker compose up` and let the
container fix ownership) afterwards.
