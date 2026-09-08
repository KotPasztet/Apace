#!/usr/bin/env python3
"""Migrate a Solace (v0.0.7) installation to Apace.

Reads a stopped Solace server directory (earth.db, launcher panel app.db,
staticdata, object_store) and merges its data into an Apace persistent data
directory (earth.db `objects` table, live.db Accounts, resourcepacks,
server-template-dir, panel app.db, config.json).

Python 3.8+ standard library only.

Usage:
    python3 scripts/migrate-from-solace.py --dry-run
    python3 scripts/migrate-from-solace.py --solace-dir ~/solace/solace-server \
        --target /opt/apace-persistent --yes

Notes:
  * Solace serializes its DB payloads with System.Text.Json DEFAULT options
    (PascalCase), while Apace serializes with JsonNamingPolicy.CamelCase.
    Every migrated object is therefore re-serialized through explicit
    per-type key maps (verified against src/Solace.DB/Models in Apace,
    which is the source of truth).
  * Data keys (item uuids, buildplate ids, token ids) are never remapped.
  * The "type" JSON polymorphic discriminators (tokens, activity log
    entries) are kept verbatim.
"""

import argparse
import hashlib
import json
import os
import shutil
import sqlite3
import subprocess
import sys
import tarfile
import uuid
from datetime import datetime, timezone
from pathlib import Path

# ── Constants ─────────────────────────────────────────────────────────────────

# earth.db tables a Solace v0.0.7 install must contain (Tiles is optional and
# skipped, as are Secrets and TemplateBuildplates).
REQUIRED_SOLACE_TABLES = [
    "Accounts",
    "Profiles",
    "Boosts",
    "CraftingSlots",
    "Hotbars",
    "Inventories",
    "Journals",
    "RedeemedTappables",
    "Tokens",
    "SmeltingSlots",
    "ActivityLogs",
    "PlayerBuildplates",
    "SharedBuildplates",
    "EncounterBuildplates",
    "Secrets",
]

# Solace Account.Guid -> Apace 16-hex player id (LoginController.GenerateUserId).
DEFAULT_PROFILE_PICTURE = "images/default_pfp.png"

# Settings keys that may be carried over into a fresh Apace config.json.
# Connection strings / ports that Apace derives itself are deliberately
# excluded (EarthDatabaseConnectionString, LiveDatabaseConnectionString,
# BuildplateBasePort, ...).
CONFIG_KEYS = ["ApiPort", "IPv4", "OnlyAllowLocalLogin", "MapTilerApiKey",
               "TileDataSource", "SkipFileChecks"]
CONFIG_FORBIDDEN_KEYS = ["EarthDatabaseConnectionString",
                         "LiveDatabaseConnectionString", "BuildplateBasePort"]

# Apace config.json is written by Solace.LauncherUI.Settings with DEFAULT
# (case-sensitive, PascalCase) System.Text.Json options.
CONFIG_JSON_KW = {"indent": 2}  # Settings uses WriteIndented = true

# ── Key maps: Solace PascalCase -> Apace camelCase ───────────────────────────
# Every map below was cross-checked against Apace's models in
# src/Solace.DB/Models (Player/, Global/, Common/, Workshop/). Apace is the
# source of truth; where Solace has fields Apace lacks (e.g. DailyLoginToken
# Claimed/ClaimedOn) they are dropped via DROP_KEYS.

REWARDS = {
    "Rubies": "rubies",
    "ExperiencePoints": "experiencePoints",
    "Level": "level",
    "Items": "items",
    "Buildplates": "buildplates",
    "Challenges": "challenges",
}
RUBIES = {"Purchased": "purchased", "Earned": "earned"}
HOTBAR_ITEM = {"Uuid": "uuid", "Count": "count", "InstanceId": "instanceId"}
SHARED_HOTBAR_ITEM = {"Uuid": "uuid", "Count": "count",
                      "InstanceId": "instanceId", "Wear": "wear"}
NON_STACKABLE_INSTANCE = {"InstanceId": "instanceId", "Wear": "wear"}
JOURNAL_ENTRY = {"FirstSeen": "firstSeen", "LastSeen": "lastSeen",
                 "AmountCollected": "amountCollected"}
ACTIVE_BOOST = {"InstanceId": "instanceId", "ItemId": "itemId",
                "StartTime": "startTime", "Duration": "duration"}
CRAFTING_JOB = {
    "SessionId": "sessionId",
    "RecipeId": "recipeId",
    "StartTime": "startTime",
    "Input": "input",       # InputItem[][], recursed via INPUT_ITEM
    "TotalRounds": "totalRounds",
    "CollectedRounds": "collectedRounds",
    "FinishedEarly": "finishedEarly",
}
INPUT_ITEM = {"Id": "id", "Count": "count", "Instances": "instances"}
SMELTING_JOB = {
    "SessionId": "sessionId",
    "RecipeId": "recipeId",
    "StartTime": "startTime",
    "Input": "input",       # single InputItem
    "AddedFuel": "addedFuel",  # Fuel: {Item, BurnDuration, HeatPerSecond}
    "TotalRounds": "totalRounds",
    "CollectedRounds": "collectedRounds",
    "FinishedEarly": "finishedEarly",
}
FUEL = {"Item": "item", "BurnDuration": "burnDuration", "HeatPerSecond": "heatPerSecond"}
BURNING = {"Fuel": "fuel", "RemainingHeat": "remainingHeat"}
TOKEN = {
    "type": "type",  # discriminator, kept verbatim
    "Date": "date",
    "Rewards": "rewards",
    "Level": "level",
    "ItemId": "itemId",
    "ChallengeId": "challengeId",
    "ChallengeReferenceId": "challengeReferenceId",
}
TOKEN_DROP = {"Claimed", "ClaimedOn"}  # Solace-only, no Apace equivalent
ACTIVITY_ENTRY_KEYS = {
    "type": "type",  # discriminator, kept verbatim
    "Timestamp": "timestamp",
    "Level": "level",
    "Rewards": "rewards",
    "ItemId": "itemId",
}

# ── Migration driver ──────────────────────────────────────────────────────────


class Migration:
    def __init__(self, args):
        self.args = args
        self.warnings = []
        self.errors = []
        self.plan = {
            "accounts": 0, "accounts_skipped": 0, "live_db_inserts": 0,
            "live_db_conflicts": 0, "objects": 0,
            "objects_by_type": {}, "tables": {}, "files": [],
            "panel": {}, "config": {}, "backed_up": False,
        }
        self.migrated = []  # (account_id_guid, username, pid16, row)

    # ── helpers ───────────────────────────────────────────────────────────

    def warn(self, msg):
        self.warnings.append(msg)
        print(f"  [warn] {msg}")

    @staticmethod
    def open_ro(db_path):
        uri = f"file:{Path(db_path).as_posix()}?mode=ro"
        conn = sqlite3.connect(uri, uri=True)
        conn.execute("PRAGMA busy_timeout=5000")
        conn.row_factory = sqlite3.Row
        return conn

    @staticmethod
    def table_counts(db, tables):
        counts = {}
        existing = {
            r[0] for r in db.execute(
                "SELECT name FROM sqlite_master WHERE type='table'")
        }
        for t in tables:
            counts[t] = db.execute(f'SELECT COUNT(*) FROM "{t}"').fetchone()[0] \
                if t in existing else None
        return counts

    @staticmethod
    def columns(conn, table):
        try:
            return [r[1] for r in conn.execute(f'PRAGMA table_info("{table}")')]
        except sqlite3.Error:
            return []

    @staticmethod
    def camel(name):
        """Mirrors System.Text.Json JsonNamingPolicy.CamelCase."""
        if not name or not name[0].isupper():
            return name
        if len(name) == 1:
            return name.lower()
        return name[0].lower() + name[1:]

    @classmethod
    def remap(cls, obj, key_map, drop=()):
        """Re-key one JSON object via an explicit map.

        Unmapped keys get the .NET camelCase treatment and are KEPT (data
        preservation); entries in `drop` are removed (Solace-only fields).
        """
        out = {}
        for k, v in obj.items():
            if k in drop:
                continue
            nk = key_map.get(k) if key_map else None
            if nk is None:
                nk = cls.camel(k)
            out[nk] = cls.shape(v)
        return out

    @classmethod
    def shape(cls, v):
        """Copies nested payloads verbatim (dict KEYS are data — item uuids,
        buildplate ids — and must never be remapped). Nested model objects
        are re-keyed by their per-type callers."""
        if isinstance(v, list):
            return [cls.shape(x) for x in v]
        if isinstance(v, dict):
            return {k: cls.shape(x) for k, x in v.items()}
        return v

    def load_json(self, raw, what):
        if raw is None:
            return None
        try:
            return json.loads(raw) if isinstance(raw, (str, bytes)) else raw
        except (ValueError, TypeError):
            self.warn(f"could not parse JSON for {what}, skipping value")
            return None

    # ── a) locate Solace ──────────────────────────────────────────────────

    def resolve_solace_dir(self):
        if self.args.solace_dir:
            return Path(self.args.solace_dir).expanduser()
        candidates = [Path("~/solace/solace-server").expanduser()]
        if os.name == "nt":
            profile = Path(os.environ.get("USERPROFILE", "~")).expanduser()
            candidates = [profile / "solace" / "solace-server",
                          profile / "Solace" / "solace-server"]
        else:
            candidates += [Path("~/Solace").expanduser(),
                           Path("~/solace").expanduser()]
        for c in candidates:
            if (c / "data" / "earth.db").is_file():
                return c
        return candidates[0]  # will fail validation with a clear message

    # ── b) validate layout ────────────────────────────────────────────────

    def validate(self):
        s = self.solace
        problems = []
        if not s.is_dir():
            problems.append(f"Solace directory not found: {s}")
            self.errors += problems
            return False
        self.earth_db_path = s / "data" / "earth.db"
        if not self.earth_db_path.is_file():
            problems.append(f"missing {self.earth_db_path}")
        if not (s / "staticdata").is_dir():
            problems.append(f"missing {s / 'staticdata'}")
        # object_store: data/object_store on stock installs; tolerate a
        # top-level object_store as well.
        for cand in (s / "data" / "object_store", s / "object_store"):
            if cand.is_dir():
                self.object_store_src = cand
                break
        else:
            self.object_store_src = None
            problems.append(f"missing {s / 'data' / 'object_store'}")
        if problems:
            self.errors += problems
            return False

        db = self.open_ro(self.earth_db_path)
        try:
            existing = {
                r[0] for r in db.execute(
                    "SELECT name FROM sqlite_master WHERE type='table'")
            }
            missing = [t for t in REQUIRED_SOLACE_TABLES if t not in existing]
            if missing:
                self.errors.append(
                    "earth.db is missing required tables: " + ", ".join(missing))
                return False
            self.plan["tables"] = self.table_counts(
                db, REQUIRED_SOLACE_TABLES + ["Tiles", "TemplateBuildplates"])
        finally:
            db.close()

        # settings: settings.json (preferred) or config.json, in the install
        # dir, its launcher subdir, or ~/solace.
        names = ["settings.json", "config.json"]
        dirs = [s, s / "launcher", Path("~/solace").expanduser()]
        self.settings_path = next(
            (d / n for d in dirs for n in names if (d / n).is_file()), None)
        return True

    # ── c) id map ─────────────────────────────────────────────────────────

    def build_account_map(self):
        db = self.open_ro(self.earth_db_path)
        try:
            rows = db.execute(
                "SELECT * FROM Accounts ORDER BY CreatedDate, Id").fetchall()
        finally:
            db.close()
        seen = {}
        for row in rows:
            username = row["Username"]
            if username is None or not str(username).strip():
                self.warn(f"account {row['Id']}: NULL/empty username, skipping")
                self.plan["accounts_skipped"] += 1
                continue
            username = str(username)
            pid16 = hashlib.sha256(username.encode("utf-8")).hexdigest()[:16]
            raw_id = row["Id"]
            try:
                # Solace stores a .NET Guid TEXT; Guid.ToByteArray() is the
                # little-endian (bytes_le) layout, and pid16 is the first
                # half of the same SHA-256 digest.
                if uuid.UUID(str(raw_id)).bytes_le[:8] != bytes.fromhex(pid16):
                    self.warn(
                        f"account {raw_id} ({username}): stored id does not "
                        f"match sha256('{username}') prefix; trusting sha256")
            except (ValueError, TypeError, AttributeError):
                self.warn(
                    f"account {raw_id} ({username}): id is not a GUID; "
                    f"trusting sha256")
            if pid16 in seen:
                self.warn(
                    f"account {raw_id} ({username}): player id {pid16} already "
                    f"produced by '{seen[pid16]}', skipping this account")
                self.plan["accounts_skipped"] += 1
                continue
            seen[pid16] = username
            self.migrated.append((str(raw_id), username, pid16, row))
        self.plan["accounts"] = len(self.migrated)

    # ── d) files ──────────────────────────────────────────────────────────

    def collect_file_plan(self):
        files = []
        if self.object_store_src:
            n = sum(1 for p in self.object_store_src.rglob("*") if p.is_file())
            files.append(("dir", self.object_store_src,
                          self.target / "data" / "object_store", n))
        rp = self.solace / "staticdata" / "resourcepacks"
        if rp.is_dir():
            java = rp / "java"
            if java.is_dir():
                n = sum(1 for p in java.rglob("*") if p.is_file())
                files.append(("dir", java, self.target / "resourcepacks" / "java", n))
            vanilla = rp / "vanilla.zip"
            if vanilla.is_file():
                files.append(("file", vanilla,
                              self.target / "resourcepacks" / "vanilla.zip", 1))
            elif not java.is_dir():
                self.warn("staticdata/resourcepacks has neither java/ nor vanilla.zip")
            genoa = rp / "genoa_cache"
            if genoa.is_dir():
                n = sum(1 for p in genoa.rglob("*") if p.is_file())
                files.append(("dir", genoa,
                              self.target / "resourcepacks" / "genoa_cache", n))
        else:
            self.warn(f"missing {rp}")
        eula = self.solace / "staticdata" / "server_template_dir" / "eula.txt"
        eula_dst = self.target / "server-template-dir" / "eula.txt"
        if eula.is_file():
            files.append(("if-absent", eula, eula_dst, 1))
        else:
            self.warn(f"missing {eula} (Apace will keep its own)")
        self.plan["files"] = files

    # ── f) object builders ────────────────────────────────────────────────

    def build_player_objects(self):
        """Returns [(type, id, value)] for every migrated account."""
        objects = []
        db = self.open_ro(self.earth_db_path)
        try:
            for guid, username, pid, _row in self.migrated:
                made = self.build_one_account(db, guid, username, pid)
                for otype, value in made:
                    objects.append((otype, pid, value))
                    self.plan["objects_by_type"][otype] = \
                        self.plan["objects_by_type"].get(otype, 0) + 1

            # g) shared + encounter buildplates (player-independent objects)
            shared = self.build_shared_buildplates(db)
            if shared is not None:
                objects.append(("sharedBuildplates", "", shared))
                n = len(shared["sharedBuildplates"])
                self.plan["objects_by_type"]["sharedBuildplates"] = n
            encounter = self.build_encounter_buildplates(db)
            if encounter is not None:
                objects.append(("encounterBuildplates", "", encounter))
                n = len(encounter["encounterBuildplates"])
                self.plan["objects_by_type"]["encounterBuildplates"] = n
        finally:
            db.close()
        self.plan["objects"] = len(objects)
        self.objects = objects
        return objects

    def build_one_account(self, db, guid, username, pid):
        made = []

        # Profiles -> ("profile", pid)
        row = db.execute("SELECT * FROM Profiles WHERE Id = ?", (guid,)).fetchone()
        if row:
            rubies = self.load_json(row["Rubies"], f"{username} profile rubies") or {}
            made.append(("profile", self.remap({
                "Health": row["Health"],
                "Experience": row["Experience"],
                "Level": row["Level"],
                "Rubies": self.remap(rubies, RUBIES),
            }, {
                "Health": "health", "Experience": "experience",
                "Level": "level", "Rubies": "rubies",
            })))

        # Boosts -> ("boosts", pid); Apace adds mini-figs (empty defaults)
        row = db.execute("SELECT ActiveBoosts FROM Boosts WHERE Id = ?", (guid,)).fetchone()
        if row:
            boosts = self.load_json(row["ActiveBoosts"], f"{username} boosts")
            if boosts is None:
                boosts = []
            made.append(("boosts", {
                "activeBoosts": [self.remap(b, ACTIVE_BOOST) if b else None
                                 for b in boosts],
                "activeMiniFigs": [],
                "miniFigRecords": {},
            }))

        # Hotbars -> ("hotbar", pid)
        row = db.execute("SELECT Items FROM Hotbars WHERE Id = ?", (guid,)).fetchone()
        if row:
            items = self.load_json(row["Items"], f"{username} hotbar") or []
            made.append(("hotbar", {
                "items": [self.remap(i, HOTBAR_ITEM) if i else None for i in items],
            }))

        # Inventories -> ("inventory", pid)
        row = db.execute(
            "SELECT StackableItemsData, NonStackableItemsData FROM Inventories "
            "WHERE Id = ?", (guid,)).fetchone()
        if row:
            stackable = self.load_json(row["StackableItemsData"],
                                       f"{username} stackable items") or {}
            non_stackable = self.load_json(row["NonStackableItemsData"],
                                           f"{username} non-stackable items") or {}
            made.append(("inventory", {
                "stackableItems": {k: self.shape(v) for k, v in stackable.items()},
                "nonStackableItems": {
                    item_uuid: {iid: self.remap(inst, NON_STACKABLE_INSTANCE)
                                for iid, inst in instances.items()}
                    for item_uuid, instances in non_stackable.items()
                },
            }))

        # Journals -> ("journal", pid)
        row = db.execute("SELECT Items FROM Journals WHERE Id = ?", (guid,)).fetchone()
        if row:
            items = self.load_json(row["Items"], f"{username} journal") or {}
            made.append(("journal", {
                "items": {uuid_: self.remap(e, JOURNAL_ENTRY)
                          for uuid_, e in items.items()},
            }))

        # RedeemedTappables -> ("redeemedTappables", pid)
        row = db.execute("SELECT Tappables FROM RedeemedTappables WHERE Id = ?",
                         (guid,)).fetchone()
        if row:
            tappables = self.load_json(row["Tappables"],
                                       f"{username} redeemed tappables") or {}
            made.append(("redeemedTappables", {
                "tappables": {k: self.shape(v) for k, v in tappables.items()},
            }))

        # Tokens -> ("tokens", pid) and derived ("tokenClaims", pid)
        daily_claims = self.migrate_tokens(db, guid, username, made)

        # CraftingSlots -> ("crafting", pid)
        row = db.execute("SELECT Slots FROM CraftingSlots WHERE Id = ?", (guid,)).fetchone()
        if row:
            slots = self.load_json(row["Slots"], f"{username} crafting slots") or []
            made.append(("crafting", {"slots": [self.remap_slot(s) for s in slots]}))

        # SmeltingSlots -> ("smelting", pid)
        row = db.execute("SELECT Slots FROM SmeltingSlots WHERE Id = ?", (guid,)).fetchone()
        if row:
            slots = self.load_json(row["Slots"], f"{username} smelting slots") or []
            made.append(("smelting", {"slots": [self.remap_smelting_slot(s) for s in slots]}))

        # ActivityLogs -> ("activityLog", pid), last 40 entries
        row = db.execute("SELECT Entries FROM ActivityLogs WHERE Id = ?", (guid,)).fetchone()
        if row:
            entries = self.load_json(row["Entries"], f"{username} activity log") or []
            if len(entries) > 40:
                entries = entries[-40:]
            made.append(("activityLog", {
                "entries": [self.remap_activity_entry(e) for e in entries],
            }))

        # PlayerBuildplates -> ("buildplates", pid)
        rows = db.execute(
            "SELECT * FROM PlayerBuildplates WHERE AccountId = ?", (guid,)).fetchall()
        if rows:
            buildplates = {}
            for r in rows:
                buildplates[str(r["Id"])] = self.remap({
                    "TemplateId": r["TemplateId"] and str(r["TemplateId"]),
                    "Name": r["Name"],
                    "Size": r["Size"],
                    "Offset": r["Offset"],
                    "Scale": r["Scale"],
                    "Night": bool(r["Night"]),
                    "LastModified": r["LastModified"],
                    "ServerDataObjectId": r["ServerDataObjectId"],
                    "PreviewObjectId": r["PreviewObjectId"],
                }, {
                    "TemplateId": "templateId", "Name": "name", "Size": "size",
                    "Offset": "offset", "Scale": "scale", "Night": "night",
                    "LastModified": "lastModified",
                    "ServerDataObjectId": "serverDataObjectId",
                    "PreviewObjectId": "previewObjectId",
                })
            made.append(("buildplates", {"buildplates": buildplates}))

        if daily_claims is not None:
            made.append(("tokenClaims", daily_claims))
        return made

    def migrate_tokens(self, db, guid, username, made):
        """Copies tokens (dropping Solace-only fields) and derives TokenClaims.

        Apace keeps daily-login state in the tokenClaims object (see
        Solace.DB/Models/Player/TokenClaims.cs + TokenUtils), while Solace
        keeps it inside the DAILY_LOGIN tokens (Date/Claimed/ClaimedOn).
        """
        row = db.execute("SELECT Tokens FROM Tokens WHERE Id = ?", (guid,)).fetchone()
        if not row:
            return None
        tokens = self.load_json(row["Tokens"], f"{username} tokens") or {}
        made.append(("tokens", {"tokens": {
            tid: self.remap_token(tok)
            for tid, tok in tokens.items()
        }}))

        claims = {
            "lastDailyLoginDate": None,
            "dailyLoginStreak": 0,
            "redeemedDailyLoginDates": [],
            "redeemedChallengeRewardKeys": [],
            "oobeAdventureCrystalGranted": False,
            "oobeAdventureCrystalRedeemed": False,
        }
        claimed_dates = []
        for tok in tokens.values():
            if not isinstance(tok, dict):
                continue
            if tok.get("type") == "DAILY_LOGIN":
                if tok.get("Claimed"):
                    date = tok.get("Date")
                    if date:
                        claimed_dates.append(str(date))
            elif tok.get("type") == "OOBE_ADVENTURE_CRYSTAL":
                claims["oobeAdventureCrystalGranted"] = True
        if claimed_dates:
            claimed_dates.sort()
            claims["redeemedDailyLoginDates"] = claimed_dates
            claims["lastDailyLoginDate"] = claimed_dates[-1]
        return claims

    @classmethod
    def remap_token(cls, tok):
        if not isinstance(tok, dict):
            return tok
        mapped = cls.remap(tok, TOKEN, drop=TOKEN_DROP)
        return cls.remap_rewards(mapped)

    @classmethod
    def remap_activity_entry(cls, entry):
        if not isinstance(entry, dict):
            return entry
        return cls.remap_rewards(cls.remap(entry, ACTIVITY_ENTRY_KEYS))

    @classmethod
    def remap_rewards(cls, mapped):
        """Remaps a nested Rewards payload (Solace stores it PascalCase)."""
        if isinstance(mapped.get("rewards"), dict):
            mapped["rewards"] = cls.remap(mapped["rewards"], REWARDS)
        return mapped

    @classmethod
    def remap_slot(cls, slot):
        if slot is None:
            return None
        out = {"Locked": slot.get("Locked", False)}
        job = slot.get("ActiveJob")
        out["ActiveJob"] = None if job is None else cls.remap_crafting_job(job)
        return cls.remap(out, {"Locked": "locked", "ActiveJob": "activeJob"})

    @classmethod
    def remap_crafting_job(cls, job):
        mapped = cls.remap(job, CRAFTING_JOB)
        # Input is InputItem[][] -> map every InputItem
        if isinstance(mapped.get("input"), list):
            mapped["input"] = [
                [None if it is None else cls.remap(it, INPUT_ITEM) for it in round_]
                if isinstance(round_, list) else round_
                for round_ in mapped["input"]
            ]
        return mapped

    @classmethod
    def remap_smelting_slot(cls, slot):
        if slot is None:
            return None
        out = {"Locked": slot.get("Locked", False)}
        job = slot.get("ActiveJob")
        if job is None:
            out["ActiveJob"] = None
        else:
            mapped = cls.remap(job, SMELTING_JOB)
            if isinstance(mapped.get("input"), dict):
                mapped["input"] = cls.remap_input_item(job.get("Input"))
            added = job.get("AddedFuel")
            mapped["addedFuel"] = None if added is None else cls.remap_fuel(added)
            out["ActiveJob"] = mapped
        burning = slot.get("Burning")
        if burning is None:
            out["Burning"] = None
        else:
            fuel = burning.get("Fuel")
            out["Burning"] = cls.remap({
                "Fuel": None if fuel is None else cls.remap_fuel(fuel),
                "RemainingHeat": burning.get("RemainingHeat"),
            }, BURNING)
        return cls.remap(out, {"Locked": "locked", "ActiveJob": "activeJob",
                               "Burning": "burning"})

    @classmethod
    def remap_input_item(cls, item):
        if item is None:
            return None
        mapped = cls.remap(item, INPUT_ITEM)
        instances = item.get("Instances")
        if isinstance(instances, list):
            mapped["instances"] = [
                None if i is None else cls.remap(i, NON_STACKABLE_INSTANCE)
                for i in instances
            ]
        return mapped

    @classmethod
    def remap_fuel(cls, fuel):
        if fuel is None:
            return None
        mapped = cls.remap(fuel, FUEL)
        item = fuel.get("Item")
        mapped["item"] = cls.remap_input_item(item)
        return mapped

    def build_shared_buildplates(self, db):
        rows = db.execute("SELECT * FROM SharedBuildplates").fetchall()
        if not rows:
            return None
        account_to_pid = {guid: pid for guid, _u, pid, _r in self.migrated}
        shared = {}
        for r in rows:
            guid = str(r["AccountId"])
            pid = account_to_pid.get(guid)
            if pid is None:
                self.warn(
                    f"shared buildplate {r['Id']}: owning account {guid} was "
                    f"skipped, dropping it")
                continue
            hotbar = self.load_json(r["Hotbar"], f"shared buildplate {r['Id']} hotbar")
            shared[str(r["Id"])] = self.remap({
                "PlayerId": pid,
                "Size": r["Size"],
                "Offset": r["Offset"],
                "Scale": r["Scale"],
                "Night": bool(r["Night"]),
                "Created": r["Created"],
                "BuildplateLastModifed": r["BuildplateLastModifed"],  # sic
                "LastViewed": r["LastViewed"],
                "NumberOfTimesViewed": r["NumberOfTimesViewed"],
                "Hotbar": [
                    None if h is None else self.remap(h, SHARED_HOTBAR_ITEM)
                    for h in (hotbar or [])
                ],
                "ServerDataObjectId": r["ServerDataObjectId"],
            }, {
                "PlayerId": "playerId", "Size": "size", "Offset": "offset",
                "Scale": "scale", "Night": "night", "Created": "created",
                # Apace's SharedBuildplate.BuildplateLastModifed keeps the
                # upstream typo (missing 'e') -> camelCase keeps it too.
                "BuildplateLastModifed": "buildplateLastModifed",
                "LastViewed": "lastViewed",
                "NumberOfTimesViewed": "numberOfTimesViewed",
                "Hotbar": "hotbar",
                "ServerDataObjectId": "serverDataObjectId",
            })
        return {"sharedBuildplates": shared}

    def build_encounter_buildplates(self, db):
        rows = db.execute("SELECT * FROM EncounterBuildplates").fetchall()
        if not rows:
            return None
        encounter = {}
        for r in rows:
            encounter[str(r["Id"])] = self.remap({
                "Size": r["Size"],
                "Offset": r["Offset"],
                "Scale": r["Scale"],
                "ServerDataObjectId": r["ServerDataObjectId"],
            }, {
                "Size": "size", "Offset": "offset", "Scale": "scale",
                "ServerDataObjectId": "serverDataObjectId",
            })
        return {"encounterBuildplates": encounter}

    # ── e/g) live.db + panel ──────────────────────────────────────────────

    def plan_live_db(self):
        target_db = self.target / "data" / "live.db"
        existing = {}
        if target_db.is_file():
            conn = sqlite3.connect(target_db)
            conn.row_factory = sqlite3.Row
            try:
                if self.columns(conn, "Accounts"):
                    for r in conn.execute("SELECT Id, Username FROM Accounts"):
                        existing[str(r["Id"])] = r["Username"]
            finally:
                conn.close()
        self.live_existing = existing
        inserts, conflicts = 0, 0
        for _guid, _username, _pid, row in self.migrated:
            new_username = str(row["Username"])
            if str(row["Id"]) in existing or new_username in {
                    str(u) for u in existing.values() if u is not None}:
                conflicts += 1
            else:
                inserts += 1
        self.plan["live_db_inserts"] = inserts
        self.plan["live_db_conflicts"] = conflicts

    PANEL_TABLES = ["AspNetUsers", "AspNetRoles", "AspNetRoleClaims",
                    "AspNetUserRoles", "AspNetUserClaims", "AspNetUserLogins",
                    "AspNetUserTokens", "AspNetUserPasskeys"]
    PANEL_DROP_COLUMNS = {"AspNetUsers": {"LinkedInGameAccounts"}}

    def plan_panel(self):
        src = self.solace / "launcher" / "Data" / "app.db"
        dst = self.target / "launcher-data" / "app.db"
        result = {"source": str(src), "exists": src.is_file(),
                  "target_exists": dst.is_file(), "tables": {},
                  "linked_accounts": 0, "skipped": None}
        self.plan["panel"] = result
        if not src.is_file():
            result["skipped"] = "no Solace panel app.db found"
            return
        if not dst.is_file():
            result["skipped"] = "no Apace panel app.db yet (panel users skipped " \
                                "-- recreate in Apace)"
            return
        s = sqlite3.connect(src)
        t = sqlite3.connect(dst)
        s.row_factory = t.row_factory = sqlite3.Row
        try:
            for table in self.PANEL_TABLES:
                s_cols = set(self.columns(s, table))
                t_cols = set(self.columns(t, table))
                if not t_cols:
                    result["tables"][table] = {"status": "missing in target",
                                               "rows": 0}
                    continue
                drop = self.PANEL_DROP_COLUMNS.get(table, set())
                missing = t_cols - (s_cols - drop)
                if missing:
                    result["tables"][table] = {
                        "status": f"schema drift: target-only columns "
                                  f"{sorted(missing)}", "rows": 0}
                    continue
                # Conflicts with rows that already exist in the target are
                # resolved at write time via INSERT OR IGNORE (some Identity
                # tables — UserRoles, UserLogins, UserTokens — have composite
                # keys and no Id column).
                copy_cols = sorted((t_cols & s_cols) - drop)
                total = s.execute(f'SELECT COUNT(*) FROM "{table}"').fetchone()[0]
                result["tables"][table] = {"status": "ok", "rows": total,
                                           "columns": copy_cols}
            # ApplicationUser.LinkedInGameAccounts (JSON Guid list) ->
            # LinkedGameAccounts(PanelUserId, PlayerId=16-hex)
            if result["tables"].get("AspNetUsers", {}).get("status") == "ok":
                pid_by_guid = {guid: pid for guid, _u, pid, _r in self.migrated}
                existing_links = {
                    (str(r[0]), str(r[1])) for r in t.execute(
                        "SELECT PanelUserId, PlayerId FROM LinkedGameAccounts")
                } if self.columns(t, "LinkedGameAccounts") else set()
                count = 0
                for r in s.execute("SELECT Id, LinkedInGameAccounts FROM AspNetUsers"):
                    try:
                        guids = json.loads(r["LinkedInGameAccounts"] or "[]")
                    except ValueError:
                        continue
                    for g in guids:
                        pid = pid_by_guid.get(str(g))
                        if pid and (str(r["Id"]), pid) not in existing_links:
                            count += 1
                result["linked_accounts"] = count
        finally:
            s.close()
            t.close()

    # ── h) config.json ────────────────────────────────────────────────────

    def plan_config(self):
        src_settings = {}
        if self.settings_path:
            try:
                src_settings = json.loads(self.settings_path.read_text())
            except ValueError:
                self.warn(f"could not parse {self.settings_path}")
        desired = {}
        for key in CONFIG_KEYS:
            if key in src_settings and src_settings[key] is not None:
                desired[key] = src_settings[key]
        desired.setdefault("ApiPort", 1808)
        forbidden = {k: src_settings[k] for k in CONFIG_FORBIDDEN_KEYS
                     if k in src_settings}
        self.plan["config"] = {
            "source": str(self.settings_path) if self.settings_path else None,
            "desired": desired,
            "forbidden_dropped": forbidden,
            "target": self.target / "config.json",
            "target_exists": (self.target / "config.json").is_file(),
        }

    # ── execution ─────────────────────────────────────────────────────────

    def make_backup(self):
        data_dir = self.target / "data"
        config = self.target / "config.json"
        if not data_dir.is_dir() and not config.is_file():
            print("  backup: nothing to back up yet")
            return
        stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        backup_dir = self.target.parent / f"apace-backup-{stamp}"
        backup_dir.mkdir(parents=True, exist_ok=True)
        archive = backup_dir / f"apace-data-{stamp}.tar.gz"
        with tarfile.open(archive, "w:gz") as tar:
            if data_dir.is_dir():
                tar.add(data_dir, arcname="data")
            if config.is_file():
                tar.add(config, arcname="config.json")
        print(f"  backup written: {archive}")
        self.plan["backed_up"] = True

    @staticmethod
    def copy_file(src, dst):
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(src, dst)

    def copy_files(self):
        for kind, src, dst, _n in self.plan["files"]:
            if kind == "dir":
                shutil.copytree(src, dst, dirs_exist_ok=True)
            elif kind == "file":
                self.copy_file(src, dst)
            elif kind == "if-absent":
                if dst.is_file():
                    print(f"  {dst.name}: already present, keeping Apace copy")
                else:
                    self.copy_file(src, dst)

    def write_live_db(self):
        self.target_data = self.target / "data"
        self.target_data.mkdir(parents=True, exist_ok=True)
        path = self.target_data / "live.db"
        conn = sqlite3.connect(path)
        try:
            conn.execute("PRAGMA journal_mode=WAL")
            conn.execute("PRAGMA busy_timeout=5000")
            conn.execute("""
                CREATE TABLE IF NOT EXISTS Accounts (
                    Id TEXT PRIMARY KEY,
                    CreatedDate INTEGER,
                    Username TEXT,
                    FirstName TEXT,
                    LastName TEXT,
                    ProfilePictureUrl TEXT DEFAULT 'images/default_pfp.png',
                    PasswordSalt BLOB,
                    PasswordHash BLOB
                )""")
            for _guid, _username, pid, row in self.migrated:
                new_username = str(row["Username"])
                dup = conn.execute(
                    "SELECT 1 FROM Accounts WHERE Id = ? OR Username = ?",
                    (pid, new_username)).fetchone()
                if dup:
                    print(f"  live.db: {new_username} already exists, skipped")
                    continue
                conn.execute(
                    "INSERT INTO Accounts (Id, CreatedDate, Username, FirstName, "
                    "LastName, ProfilePictureUrl, PasswordSalt, PasswordHash) "
                    "VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                    (pid, row["CreatedDate"] or 0, new_username,
                     row["FirstName"], row["LastName"],
                     row["ProfilePictureUrl"] or DEFAULT_PROFILE_PICTURE,
                     row["PasswordSalt"], row["PasswordHash"]))
            conn.commit()
            # checkpoint AFTER commit: it cannot run inside a transaction
            conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
        finally:
            conn.close()

    def write_objects(self):
        path = self.target_data / "earth.db"
        conn = sqlite3.connect(path)
        try:
            conn.execute("PRAGMA journal_mode=WAL")
            conn.execute("PRAGMA busy_timeout=5000")
            conn.execute(
                "CREATE TABLE IF NOT EXISTS objects ("
                "type TEXT NOT NULL, id TEXT NOT NULL, value TEXT NOT NULL, "
                "version INTEGER NOT NULL, PRIMARY KEY (type, id))")
            for otype, oid, value in self.objects:
                conn.execute(
                    "INSERT OR REPLACE INTO objects (type, id, value, version) "
                    "VALUES (?, ?, ?, 1)",
                    (otype, oid, json.dumps(value, separators=(",", ":"))))
            conn.commit()
            conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
        finally:
            conn.close()

    def write_panel(self):
        result = self.plan["panel"]
        if result.get("skipped"):
            return
        src = Path(result["source"])
        dst = self.target / "launcher-data" / "app.db"
        dst.parent.mkdir(parents=True, exist_ok=True)
        s = sqlite3.connect(src)
        t = sqlite3.connect(dst)
        s.row_factory = t.row_factory = sqlite3.Row
        try:
            t.execute("PRAGMA busy_timeout=5000")
            inserted = {}
            for table, info in result["tables"].items():
                if info.get("status") != "ok" or not info.get("rows"):
                    continue
                cols = info["columns"]
                col_list = ", ".join(f'"{c}"' for c in cols)
                marks = ", ".join("?" for _ in cols)
                count = 0
                for r in s.execute(f'SELECT {col_list} FROM "{table}"'):
                    cur = t.execute(
                        f'INSERT OR IGNORE INTO "{table}" ({col_list}) '
                        f'VALUES ({marks})', tuple(r[c] for c in cols))
                    count += cur.rowcount if cur.rowcount > 0 else 0
                if count:
                    print(f"  panel {table}: {count} row(s) inserted")
                inserted[table] = count
            if result.get("linked_accounts") and self.columns(t, "LinkedGameAccounts"):
                pid_by_guid = {guid: pid for guid, _u, pid, _r in self.migrated}
                existing_links = {
                    (str(r[0]), str(r[1])) for r in t.execute(
                        "SELECT PanelUserId, PlayerId FROM LinkedGameAccounts")}
                linked = 0
                for r in s.execute("SELECT Id, LinkedInGameAccounts FROM AspNetUsers"):
                    try:
                        guids = json.loads(r["LinkedInGameAccounts"] or "[]")
                    except ValueError:
                        continue
                    for g in guids:
                        pid = pid_by_guid.get(str(g))
                        if not pid or (str(r["Id"]), pid) in existing_links:
                            continue
                        t.execute(
                            "INSERT INTO LinkedGameAccounts (PanelUserId, PlayerId) "
                            "VALUES (?, ?)", (str(r["Id"]), pid))
                        existing_links.add((str(r["Id"]), pid))
                        linked += 1
                if linked:
                    print(f"  panel LinkedGameAccounts: {linked} link(s) created")
            t.commit()
            t.execute("PRAGMA wal_checkpoint(TRUNCATE)")
        finally:
            s.close()
            t.close()

    def write_config(self):
        info = self.plan["config"]
        target = Path(info["target"])
        if target.is_file():
            print(f"  config.json exists at {target}, left untouched")
            return
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(json.dumps(info["desired"], **CONFIG_JSON_KW) + "\n")

    def chown_target(self):
        if os.name == "nt" or not hasattr(os, "geteuid"):
            return
        try:
            subprocess.run(["chown", "-R", "1654:1654", str(self.target)],
                           check=False, timeout=120)
        except (OSError, subprocess.SubprocessError):
            pass  # best-effort (uid may not exist on the host running this)

    # ── orchestration ─────────────────────────────────────────────────────

    def build_plan(self):
        self.target = Path(self.args.target).expanduser()
        if not self.validate():
            return False
        self.build_account_map()
        self.collect_file_plan()
        self.build_player_objects()
        self.plan_live_db()
        self.plan_panel()
        self.plan_config()
        return True

    def print_plan(self):
        p = self.plan
        print("=" * 72)
        print("MIGRATION PLAN (dry run — nothing was written)" if self.args.dry_run
              else "MIGRATION PLAN")
        print("=" * 72)
        print(f"Solace dir : {self.solace}")
        print(f"Settings   : {self.settings_path or 'not found (defaults used)'}")
        print(f"Target dir : {self.target}")
        print(f"Backup     : {'disabled (--no-backup)' if self.args.no_backup else 'enabled'}")
        print()
        print("Solace earth.db table row counts:")
        for table, count in sorted(p["tables"].items()):
            mark = "" if count else "  (empty)" if count == 0 else "  (missing!)"
            print(f"  {table:<22} {count if count is not None else '-'}{mark}")
        print()
        print(f"Accounts: {p['accounts']} migrated, {p['accounts_skipped']} skipped")
        print(f"live.db Accounts to insert: {p['live_db_inserts']} "
              f"({p['live_db_conflicts']} conflicts skipped)")
        print()
        print("Apace objects to write (earth.db `objects`, version=1):")
        for otype in sorted(p["objects_by_type"]):
            print(f"  {otype:<22} {p['objects_by_type'][otype]}")
        print(f"  {'TOTAL':<22} {p['objects']}")
        print()
        print("Files to copy:")
        for kind, src, dst, n in p["files"]:
            label = f"{n} files" if kind == "dir" else kind
            print(f"  {src} -> {dst} [{label}]")
        print()
        panel = p["panel"]
        if not panel.get("exists"):
            print(f"Panel app.db: {panel.get('skipped')}")
        elif panel.get("skipped"):
            print(f"Panel app.db: {panel.get('skipped')}")
        else:
            print("Panel app.db:")
            for table, info in panel["tables"].items():
                extra = f" -> {info['status']}" if info["status"] != "ok" else ""
                print(f"  {table:<22} {info.get('rows', 0)} rows{extra}")
            print(f"  LinkedGameAccounts to create: {panel['linked_accounts']}")
        print()
        cfg = p["config"]
        if cfg["target_exists"]:
            print(f"config.json EXISTS at {cfg['target']} — would be left "
                  f"untouched; would have mapped: {json.dumps(cfg['desired'])}")
        else:
            print(f"config.json would be created at {cfg['target']}: "
                  f"{json.dumps(cfg['desired'])} "
                  f"(source: {cfg['source'] or 'defaults'})")
        if cfg["forbidden_dropped"]:
            print(f"  never copied (Apace derives these): "
                  f"{', '.join(sorted(cfg['forbidden_dropped']))}")
        print()
        print(f"Warnings: {len(self.warnings)}")
        for w in self.warnings:
            print(f"  - {w}")
        skipped_note = ("Skipped on purpose: Secrets (re-login required), Tiles, "
                        "TemplateBuildplates (Apace re-imports them from its "
                        "staticdata), DbBuildplatePreview.")
        print()
        print(skipped_note)
        print("=" * 72)

    def run(self):
        self.solace = self.resolve_solace_dir()
        if not self.build_plan():
            for err in self.errors:
                print(f"[error] {err}", file=sys.stderr)
            return 1
        self.print_plan()
        if self.args.dry_run:
            return 0
        if not self.args.yes:
            answer = input("\nProceed with migration? Type 'yes' to continue: ")
            if answer.strip().lower() != "yes":
                print("aborted by user")
                return 2
        print()
        if not self.args.no_backup:
            print("Backing up target data/ + config.json ...")
            self.make_backup()
        print("Copying files ...")
        self.copy_files()
        print("Writing live.db accounts ...")
        self.write_live_db()
        print("Writing earth.db objects ...")
        self.write_objects()
        print("Migrating panel users ...")
        self.write_panel()
        print("Writing config.json ...")
        self.write_config()
        print("Fixing ownership (1654:1654) ...")
        self.chown_target()
        p = self.plan
        print()
        print("Migration complete.")
        print(f"  accounts migrated: {p['accounts']} "
              f"(skipped: {p['accounts_skipped']})")
        print(f"  objects written:   {p['objects']}")
        for kind, _src, _dst, n in p["files"]:
            if kind == "dir":
                print(f"  files copied:      {n} in {_dst}")
        print()
        print("Operational notes:")
        print("  - All players must LOG IN AGAIN (Secrets/sessions are not migrated).")
        print("  - Buildplate worlds appear on first entry; the first load re-imports world data.")
        print("  - The shop/catalog imports on the next ApiServer start.")
        print("  - Panel users keep their passwords; linked game accounts were re-linked.")
        if not self.args.no_backup:
            print("  - Rollback: stop Apace, restore the backup tar.gz created above.")
        return 0


def parse_args(argv=None):
    parser = argparse.ArgumentParser(
        description="Migrate a Solace v0.0.7 installation to Apace.",
        formatter_class=argparse.ArgumentDefaultsHelpFormatter)
    parser.add_argument("--solace-dir", default=None,
                        help="Solace server directory (contains data/earth.db)")
    parser.add_argument("--target", default="/opt/apace-persistent",
                        help="Apace persistent data directory")
    parser.add_argument("--dry-run", action="store_true",
                        help="print the full plan and per-table counts, write nothing")
    parser.add_argument("--no-backup", action="store_true",
                        help="skip the timestamped tar.gz backup of the target "
                             "data/ + config.json (backup is ON by default)")
    parser.add_argument("--yes", action="store_true",
                        help="do not ask for confirmation")
    return parser.parse_args(argv)


def main(argv=None):
    return Migration(parse_args(argv)).run()


if __name__ == "__main__":
    sys.exit(main())
