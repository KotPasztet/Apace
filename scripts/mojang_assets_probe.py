#!/usr/bin/env python3
"""Mojang assets probe: xforge liveness + Wayback Machine coverage diagnostics.

WHY THIS EXISTS
The dev sandbox cannot reach archive.org / web.archive.org at all (egress
timeout), and xforge CDN responses from the sandbox IP are suspected of being
rate-limited: a thumbnail URL returned 200 at 13:33 and 404 at 13:47+ after
~500 downloads were made during manifest generation. This script is meant to
run from GitHub Actions runners (an external network vantage point) to answer:

  PART A  Are the xforge persona/thumbnail URLs still live from CI?
  PART B  Which of the 231 persona URLs have Wayback Machine captures (CDX API)?
  PART C  Are the archived bytes identical to (a) the manifest sha256 and
          (b) the original zips committed in 043d7bb?
  PART D  Print + write a JSON summary report.

This is diagnostics, NOT a gate: it always exits 0. The report is uploaded as
a workflow artifact and rendered into the GitHub step summary by
.github/workflows/mojang-assets-probe.yml.

Stdlib only (json, hashlib, subprocess, urllib, time, os, sys, datetime).
"""

import hashlib
import json
import os
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone

# --------------------------------------------------------------------------
# Constants
# --------------------------------------------------------------------------

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MANIFEST_PATH = os.path.join(
    REPO_ROOT, "staticdata-extras", "playfab", "mojang-assets.manifest.json"
)
REPORT_PATH = os.path.join(REPO_ROOT, "mojang-assets-probe-report.json")

PERSONA_COMMIT = "043d7bb"
PERSONA_GIT_PREFIX = "staticdata-extras/playfab/persona/"

USER_AGENT = "ApaceDataGen/1.0"
MAX_READ_BYTES = 20 * 1024 * 1024  # 20 MiB cap on any response body

# Baseline probe: same path shape as persona zips but a nil UUID, so a 200
# here would mean the CDN answers generically for the namespace (not content).
RANDOM_BASELINE_URL = (
    "https://xforgeassets001.xboxlive.com/"
    "pf-namespace-b63a0803d3653643/"
    "00000000-0000-0000-0000-000000000000/primary.zip"
)

CDX_ENDPOINT = "https://web.archive.org/cdx/search/cdx"
CDX_LIMIT = 10
CDX_TIMEOUT = 45
CDX_RETRIES = 3          # up to 3 retries on transient errors
CDX_RETRY_BACKOFF = 5.0  # seconds between CDX retries
CDX_POLITENESS = 0.7     # seconds between CDX calls

LIVENESS_TIMEOUT = 30
LIVENESS_RETRIES = 2
LIVENESS_RETRY_BACKOFF = 3.0

DOWNLOAD_TIMEOUT = 90
DOWNLOAD_RETRIES = 2
DOWNLOAD_RETRY_BACKOFF = 5.0

# HTTP codes treated as transient (worth retrying) beyond >=500.
TRANSIENT_CODES = (429, 403)  # rate limiting / temp blocks from wayback + CDNs


# --------------------------------------------------------------------------
# HTTP helper
# --------------------------------------------------------------------------

def http_get(url, timeout, retries, backoff):
    """GET url. Returns (status, body, error).

    - status: int HTTP code, or None if the request never got a response.
    - body:   response bytes (capped at MAX_READ_BYTES; error page body if
              the server answered with an HTTPError).
    - error:  None on definitive success, or a short error string.
              A definitive non-2xx (e.g. 404) is returned immediately with
              its status code; transient failures (URLError, timeout, 5xx,
              429, 403) are retried up to `retries` times with `backoff`
              seconds between attempts.
    """
    last_err = None
    for attempt in range(1 + retries):
        if attempt > 0:
            time.sleep(backoff)
        req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                return resp.status, resp.read(MAX_READ_BYTES), None
        except urllib.error.HTTPError as e:
            try:
                body = e.read(MAX_READ_BYTES)
            except Exception:
                body = b""
            if e.code >= 500 or e.code in TRANSIENT_CODES:
                last_err = "HTTP %d" % e.code
                continue
            return e.code, body, "HTTP %d" % e.code
        except (urllib.error.URLError, TimeoutError, OSError) as e:
            last_err = "%s: %s" % (type(e).__name__, e)
            continue
    return None, b"", last_err


# --------------------------------------------------------------------------
# Part A: xforge liveness from the CI vantage point
# --------------------------------------------------------------------------

def probe_xforge_liveness(items):
    thumbs = [i for i in items if i.get("kind") == "thumbnail" and i.get("verified")]
    personas = [i for i in items if i.get("kind") == "persona"]

    targets = []
    if thumbs:
        targets.append(("thumbnail", thumbs[0]["url"]))
    for n, item in enumerate(personas[:3], start=1):
        targets.append(("persona_%d" % n, item["url"]))
    targets.append(("random_baseline", RANDOM_BASELINE_URL))

    results = {}
    for name, url in targets:
        status, body, err = http_get(
            url, LIVENESS_TIMEOUT, LIVENESS_RETRIES, LIVENESS_RETRY_BACKOFF
        )
        results[name] = {
            "url": url,
            "status": status,
            "bytes": len(body),
        }
        if err:
            results[name]["error"] = err
        print("[A] %-16s status=%s bytes=%d %s" % (
            name, status, len(body), ("- " + err) if err else ""), flush=True)
        time.sleep(1.0)  # small gap between liveness probes
    return results


# --------------------------------------------------------------------------
# Part B: Wayback CDX coverage for persona URLs
# --------------------------------------------------------------------------

def _cdx_query(url, extra=""):
    """One CDX API call. Returns (rows, error); rows = [{timestamp,statuscode}]."""
    quoted = urllib.parse.quote(url, safe="")
    endpoint = "%s?url=%s&output=json&limit=%d%s" % (
        CDX_ENDPOINT, quoted, CDX_LIMIT, extra
    )
    status, body, err = http_get(endpoint, CDX_TIMEOUT, CDX_RETRIES, CDX_RETRY_BACKOFF)
    if status is None or status >= 400:
        return None, err or ("HTTP %s" % status)
    text = body.decode("utf-8", errors="replace").strip()
    if not text:
        return [], None
    try:
        data = json.loads(text)
    except json.JSONDecodeError:
        return None, "cdx_non_json_response"
    if not isinstance(data, list) or not data:
        return [], None
    header = data[0]
    try:
        idx_ts = header.index("timestamp")
        idx_sc = header.index("statuscode")
    except ValueError:
        return None, "cdx_unexpected_schema"
    rows = []
    for row in data[1:]:
        if isinstance(row, list) and len(row) > max(idx_ts, idx_sc):
            rows.append({"timestamp": row[idx_ts], "statuscode": row[idx_sc]})
    return rows, None


def find_latest_200_snapshot(url):
    """Returns (snapshot_ts_or_None, error_or_None).

    Picks the LATEST capture with statuscode 200 among the returned rows.
    The default query is ascending+limit=10; if the response was truncated
    (exactly 10 rows) and none of them is a 200, retry once with a
    server-side statuscode:200 filter so old 200 captures are not missed.
    None error + None snapshot = definitively no usable 200 capture.
    """
    rows, err = _cdx_query(url)
    if err:
        return None, err
    ok = [r for r in rows if r["statuscode"] == "200"]
    if ok:
        return max(ok, key=lambda r: r["timestamp"])["timestamp"], None
    if len(rows) == CDX_LIMIT:  # page truncated -> ask CDX to filter server-side
        rows2, err2 = _cdx_query(url, extra="&filter=statuscode:200")
        if err2:
            return None, err2
        ok2 = [r for r in rows2 if r["statuscode"] == "200"]
        if ok2:
            return max(ok2, key=lambda r: r["timestamp"])["timestamp"], None
    return None, None


# --------------------------------------------------------------------------
# Part C helpers: git blob comparison against commit 043d7bb
# --------------------------------------------------------------------------

def persona_git_path(file_field):
    """'playfab/persona/<uuid>.zip' -> 'staticdata-extras/playfab/persona/<uuid>.zip'."""
    item_id = os.path.splitext(os.path.basename(file_field))[0]
    return "%s%s.zip" % (PERSONA_GIT_PREFIX, item_id)


def git_blob_exists(path):
    r = subprocess.run(
        ["git", "cat-file", "-e", "%s:%s" % (PERSONA_COMMIT, path)],
        capture_output=True,
    )
    return r.returncode == 0


def git_blob_sha256(path):
    r = subprocess.run(
        ["git", "show", "%s:%s" % (PERSONA_COMMIT, path)], capture_output=True
    )
    if r.returncode != 0:
        return None
    return hashlib.sha256(r.stdout).hexdigest()


# --------------------------------------------------------------------------
# Parts B+C: per-item scan and byte verification
# --------------------------------------------------------------------------

def scan_wayback(personas):
    counts = {
        "checked": len(personas),
        "archived": 0,
        "match": 0,
        "mismatch": 0,
        "not_archived": 0,
        "cdx_error": 0,
        "download_error": 0,
    }
    details = []
    total = len(personas)

    for n, item in enumerate(personas, start=1):
        url = item["url"]
        detail = {
            "file": item.get("file"),
            "url": url,
            "manifest_sha256": item.get("sha256"),
            "manifest_size": item.get("size"),
        }

        snapshot_ts, err = find_latest_200_snapshot(url)
        time.sleep(CDX_POLITENESS)  # politeness: AFTER every CDX call

        if err:
            detail["classification"] = "cdx_error"
            detail["cdx_error"] = err
        elif snapshot_ts is None:
            detail["classification"] = "not_archived"
        else:
            counts["archived"] += 1
            detail["snapshot_timestamp"] = snapshot_ts
            raw_url = "https://web.archive.org/web/%sid_/%s" % (snapshot_ts, url)
            status, body, derr = http_get(
                raw_url, DOWNLOAD_TIMEOUT, DOWNLOAD_RETRIES, DOWNLOAD_RETRY_BACKOFF
            )
            if status != 200:
                detail["classification"] = "download_error"
                detail["download_status"] = status
                detail["download_error"] = derr or ""
            else:
                wb_sha = hashlib.sha256(body).hexdigest()
                detail["wayback_bytes"] = len(body)
                detail["wayback_sha256"] = wb_sha
                manifest_match = wb_sha == item.get("sha256")
                detail["manifest_match"] = manifest_match

                gpath = persona_git_path(item.get("file") or "")
                if git_blob_exists(gpath):
                    gsha = git_blob_sha256(gpath)
                    detail["git_path"] = gpath
                    detail["git_sha256"] = gsha
                    detail["git_match"] = (gsha == wb_sha) if gsha else None

                # match  = wayback bytes identical to manifest sha256
                # mismatch = differs (both hashes are recorded above)
                detail["classification"] = "match" if manifest_match else "mismatch"

        counts[detail["classification"]] += 1
        details.append(detail)

        if n % 10 == 0 or n == total:
            print(
                "[B/C] %d/%d done — archived=%d match=%d mismatch=%d "
                "not_archived=%d cdx_error=%d download_error=%d"
                % (
                    n, total, counts["archived"], counts["match"],
                    counts["mismatch"], counts["not_archived"],
                    counts["cdx_error"], counts["download_error"],
                ),
                flush=True,
            )

    return counts, details


# --------------------------------------------------------------------------
# Part D: summary + report file
# --------------------------------------------------------------------------

def build_report(liveness, counts, details, started_at):
    matches = [d for d in details if d.get("classification") == "match"]
    # "match" already means wayback == manifest; additionally require that
    # every matched item with a 043d7bb blob is also byte-identical to it.
    all_byte_identical = bool(matches) and all(
        d.get("git_match") in (True, None) for d in matches
    )
    checked = counts["checked"] or 1
    recoverable_pct = round(counts["archived"] * 100.0 / checked)
    persona_live = any(
        (liveness.get(k) or {}).get("status") == 200
        for k in ("persona_1", "persona_2", "persona_3")
    )

    git_commit = os.environ.get("GITHUB_SHA")
    if not git_commit:
        r = subprocess.run(["git", "rev-parse", "HEAD"], capture_output=True)
        git_commit = r.stdout.decode().strip() if r.returncode == 0 else "unknown"

    return {
        "generated_at": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "started_at": started_at,
        "git_commit": git_commit,
        "xforge_liveness": liveness,
        "wayback": dict(counts, items=details),
        "verdicts": {
            "xforge_persona_live": "yes" if persona_live else "no",
            "wayback_recoverable_pct": "%d%%" % recoverable_pct,
            "wayback_recoverable_pct_value": recoverable_pct,
            "all_matches_byte_identical": "true" if all_byte_identical else "false",
        },
    }


def print_summary(rep):
    v = rep["verdicts"]
    wb = rep["wayback"]
    print("", flush=True)
    print("==== PART D: summary ====", flush=True)
    print("xforge liveness:", flush=True)
    for name, e in rep["xforge_liveness"].items():
        print("  %-16s status=%s bytes=%s %s" % (
            name, e.get("status"), e.get("bytes"),
            ("- " + e["error"]) if e.get("error") else ""), flush=True)
    print(
        "wayback: checked=%(checked)d archived=%(archived)d match=%(match)d "
        "mismatch=%(mismatch)d not_archived=%(not_archived)d "
        "cdx_error=%(cdx_error)d download_error=%(download_error)d" % wb,
        flush=True,
    )
    print("verdicts:", flush=True)
    for key in ("xforge_persona_live", "wayback_recoverable_pct",
                "all_matches_byte_identical"):
        print("  %s: %s" % (key, v[key]), flush=True)
    print("report written to: %s" % REPORT_PATH, flush=True)


def main():
    started_at = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    with open(MANIFEST_PATH, encoding="utf-8") as fh:
        manifest = json.load(fh)
    items = manifest.get("items", [])
    personas = [i for i in items if i.get("kind") == "persona"]
    print(
        "[manifest] version=%s items=%d persona=%d"
        % (manifest.get("version"), len(items), len(personas)),
        flush=True,
    )

    print("==== PART A: xforge liveness ====", flush=True)
    liveness = probe_xforge_liveness(items)

    print("==== PART B+C: Wayback CDX scan + byte verification ====", flush=True)
    counts, details = scan_wayback(personas)

    report = build_report(liveness, counts, details, started_at)
    print_summary(report)

    with open(REPORT_PATH, "w", encoding="utf-8") as fh:
        json.dump(report, fh, indent=2)

    # Diagnostics only — always exit 0.
    return 0


if __name__ == "__main__":
    import sys

    sys.exit(main())
