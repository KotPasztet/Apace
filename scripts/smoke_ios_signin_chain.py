#!/usr/bin/env python3
"""Smoke test of the full iOS (XAL/SISU) sign-in chain against a live Apace server.

Walks the exact sequence the patched iOS client runs: device/authenticate
(proof-of-possession body) -> sisu.xboxlive.com/authenticate -> the login page ->
ppsecure/register|login -> login.live.com/oauth20_token.srf (code -> Xbox ticket)
-> sisu.xboxlive.com/authorize -> xsts/authorize with the PlayFab relying party
(PlayfabXboxToken) -> 11509.playfabapi.com/Client/LoginWithXbox -> the game signin
(api/v1.1/player/profile/signin) -> the bootstrap GETs with the Genoa token.
Every step must answer 200 — the first non-200 is the "Uh oh" candidate (enable
APACE_LOG_AUTH_BODIES=1 on the server to capture the same walk from a real device).

Registers a debug account (username/password below) when it does not exist yet.

Usage:
    python3 scripts/smoke_ios_signin_chain.py [server]   # default http://e.choroszcz.org:1808
"""
import json
import sys
import urllib.request
import urllib.parse

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://e.choroszcz.org:1808"
UA = "MCE-iOS-debug/0.33.0 (scripts/smoke_ios_signin_chain.py)"

DEBUG_USER = "apaceiosdebug"
DEBUG_PASS = "apaceiosdebug1"


def req(method, path, body=None, form=False, headers=None, full=False):
    url = path if path.startswith("http") else BASE + path
    data = None
    hdrs = {"User-Agent": UA}
    if body is not None:
        if form:
            data = urllib.parse.urlencode(body).encode()
            hdrs["Content-Type"] = "application/x-www-form-urlencoded"
        else:
            data = json.dumps(body).encode()
            hdrs["Content-Type"] = "application/json"
    if headers:
        hdrs.update(headers)
    r = urllib.request.Request(url, data=data, headers=hdrs, method=method)
    try:
        resp = urllib.request.urlopen(r, timeout=20)
        raw = resp.read().decode("utf-8", "replace")
        return resp.status, raw, dict(resp.headers), resp.geturl()
    except urllib.error.HTTPError as e:
        raw = e.read().decode("utf-8", "replace")
        return e.code, raw, dict(e.headers), url
    except Exception as e:
        return None, f"EXC: {e}", {}, url


def step(n, name, method, path, body=None, form=False, headers=None):
    st, raw, hdrs, url = req(method, path, body, form, headers)
    print(f"--- step {n}: {name} -> {st}")
    print(f"    url: {url[:140]}")
    if st != 200:
        print(f"    body[:400]: {raw[:400]}")
    return st, raw, hdrs


print("== 1. device/authenticate (iOS PoP shape) ==")
st, raw, _ = step(1, "device/authenticate", "POST", "/device/authenticate", {
    "Properties": {
        "Id": "deadbeef-cafe-babe-food-000000000001",
        "DeviceType": "iOS",
        "SerialNumber": "D3ADB33F",
        "Version": "17.4",
        "ProofKey": {"kty": "RSA", "n": "0", "e": "AQAB", "use": "sig", "alg": "RS256"},
    },
    "RelyingParty": "http://xboxlive.com",
    "TokenType": "JWT",
})
device_token = json.loads(raw)["Token"] if st == 200 else None
print(f"    device token: {str(device_token)[:60]}...")

print("\n== 2. sisu.xboxlive.com/authenticate ==")
st, raw, _ = step(2, "sisu authenticate", "POST", "/sisu.xboxlive.com/authenticate", {
    "AppId": "0000000040281e53",
    "RedirectUri": "ms-xal-0000000040281e53",
    "DeviceToken": device_token,
    "Sandbox": "RETAIL",
    "TokenType": "JWT",
    "Offers": [],
    "Query": {},
})
redirect = json.loads(raw).get("MsaOauthRedirect") if st == 200 else None
print(f"    MsaOauthRedirect: {str(redirect)[:160]}")

print("\n== 3. login page (oauth20_authorize.srf) ==")
if redirect:
    st, raw, _, _ = req("GET", redirect)
    print(f"--- step 3: login page -> {st}, len={len(raw)}")
    if st != 200:
        print(raw[:300])

print("\n== 4. register (ppsecure/register) ==")
st, raw, _ = step(4, "register", "POST", "/login.live.com/ppsecure/register",
                  {"username": DEBUG_USER, "password": DEBUG_PASS, "firstName": "Ap", "lastName": "Db"}, form=True)
login_resp = json.loads(raw) if st == 200 and raw.strip().startswith("{") else {}
if not login_resp:
    # exists already (or any other reason) -> login instead
    st, raw, _ = step(4, "login (fallback)", "POST", "/login.live.com/ppsecure/login",
                      {"username": DEBUG_USER, "password": DEBUG_PASS}, form=True)
    login_resp = json.loads(raw) if st == 200 and raw.strip().startswith("{") else {}
code = login_resp.get("token")
print(f"    user token/code: {str(code)[:60]}...")

print("\n== 5. oauth20_token.srf (code -> Xbox ticket) ==")
st, raw, _ = step(5, "oauth20_token.srf", "POST", "/login.live.com/oauth20_token.srf",
                  {"grant_type": "authorization_code", "code": code}, form=True)
ticket = json.loads(raw).get("access_token") if st == 200 and raw.strip().startswith("{") else None
print(f"    xbox ticket: {str(ticket)[:60]}...")

print("\n== 6. sisu.xboxlive.com/authorize ==")
st, raw, _ = step(6, "sisu authorize", "POST", "/sisu.xboxlive.com/authorize", {
    "AccessToken": "t=" + (ticket or ""),
    "AppId": "0000000040281e53",
    "DeviceToken": device_token,
    "Sandbox": "RETAIL",
    "SiteName": "user.auth.xboxlive.com",
    "SessionId": "debugsession",
    "UseModernGamertag": True,
})
auth_resp = json.loads(raw) if st == 200 and raw.strip().startswith("{") else {}
uhs = None
xsts = None
if auth_resp:
    try:
        uhs = auth_resp["UserToken"]["DisplayClaims"]["xui"][0]["uhs"]
        xsts = auth_resp["AuthorizationToken"]["Token"]
        tid = auth_resp["TitleToken"]["DisplayClaims"]["xti"]["tid"]
        print(f"    uhs={uhs} tid={tid}")
        print(f"    XSTS: {str(xsts)[:60]}...")
    except Exception as e:
        print(f"    parse error: {e}; keys={list(auth_resp.keys())}")

print("\n== 6b. xsts/authorize (PlayFab relying party) ==")
xsts_pf = None
if auth_resp:
    title_tok = auth_resp.get("TitleToken", {}).get("Token")
    user_tok = auth_resp.get("UserToken", {}).get("Token")
    st, raw, _ = step(6, "xsts authorize (playfab RP)", "POST", "/xsts.auth.xboxlive.com/xsts/authorize", {
        "Properties": {
            "SandboxId": "RETAIL",
            "DeviceToken": device_token,
            "TitleToken": title_tok,
            "UserTokens": [user_tok],
        },
        "RelyingParty": "https://b980a380.minecraft.playfabapi.com/",
        "TokenType": "JWT",
    })
    if st == 200 and raw.strip().startswith("{"):
        xsts_pf = json.loads(raw).get("Token")
        print(f"    playfab XSTS: {str(xsts_pf)[:60]}...")

print("\n== 7. sisu client page ==")
step(7, "sisu client", "GET", "/sisu.xboxlive.com/client?redirect=ms-xal-0000000040281e53&state=xyz")

print("\n== 8. PlayFab LoginWithXbox (title 11509) ==")
if xsts_pf:
    st8, raw8, _ = step(8, "LoginWithXbox 11509 (xsts)", "POST", "/11509.playfabapi.com/Client/LoginWithXbox", {
        "TitleId": "11509",
        "CreateAccount": True,
        "XboxToken": f"XBL3.0 x={uhs};{xsts_pf}",
    })
    if st8 == 200 and raw8.strip().startswith("{"):
        ok = json.loads(raw8).get("data", {})
        json.dump({"session_ticket": ok.get("SessionTicket"), "playfab_id": ok.get("PlayFabId"),
                   "entity_token": ok.get("EntityToken", {}).get("EntityToken")}, open("/tmp/session.json", "w"))
        json.dump({"uhs": uhs, "xapi": xsts}, open("/tmp/xbl.json", "w"))
        print("    saved /tmp/session.json + /tmp/xbl.json")
xbox_auth = f"XBL3.0 x={uhs};{xsts}" if uhs and xsts else None
step(8, "LoginWithXbox 11509 (sisu auth tok)", "POST", "/11509.playfabapi.com/Client/LoginWithXbox", {
    "TitleId": "11509",
    "CreateAccount": True,
    "XboxToken": xbox_auth,
})
print("\n== 9. PlayFab LoginWithXbox (title 20CA2 for comparison) ==")
step(9, "LoginWithXbox 20CA2", "POST", "/20CA2.playfabapi.com/Client/LoginWithXbox", {
    "TitleId": "20CA2",
    "CreateAccount": True,
    "XboxToken": xbox_auth,
})
