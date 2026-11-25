# TODO

## In progress on `dev`

- iOS sign-in — the server flow and the patcher fixes are pushed; confirm end-to-end on a real device (signed IPA)
- Store tab titles do not load
- Tutorial — find the tokens for the first-time tutorial (daily login rewards already work)

## `gia-extras` branch (Gia's gameplay extras port)

Working: 14-day rolling per-player Super Season (graph solver, claim hardening, ruby rewards),
tappable-loot challenge credit + continuous auto-redeem, adventure chests/rewards via the
datapack grafted into the fountain jar, trading posts on every tile with 6-hour stock,
gold nuggets in map chests, raw_ore_sucks world datapack seeding.
Unfinished / to verify live: some season challenges still don't fire ("some work some don't" —
per-challenge verification against a running instance is pending); season-17 item-id tables are
hard-coded; fountain graft + datapack behavior unverified on a live server; see
`docs/gia-extras.md`.

## Privacy & anti-abuse (next major update, in priority order)

- Cloudflare Turnstile ("Enable CF") on the account creation page, plus rate limits on registration and login (per IP / per account)
- "Hide location" server option: player coordinates handled RAM-only for the duration of a single request, never logged (log scrubber), never written to the database, rounded to the map tile grid before use
- Client-side (APK patch) coordinate rounding before sending, so the server never receives precise GPS at all
- Public privacy-info endpoint + banner on the login/registration page + native APK confirmation dialog showing whether the server owner can see the player's location
- Release integrity: reproducible builds, signed per-artifact SHA-256 manifests from CI, published public verification key, /.well-known/apace/integrity endpoint, launcher/client verification (documented honestly: full remote attestation is impossible without hardware support)

## Panel & launcher

- Shop management
- Player items management (beyond the existing pack grants)
- Launcher buildplate preview — liquid rendering
- Show exceptions in launcher logs; view and clear old logs (clearing needs a separate permission)
- Some kind of auth for the logs — pass a random secret via cli args and verify it in the controller
- Move the logs folder up next to `run_launcher.ps1` — cli arg for the log file location on all programs; per-instance logs (server/bridge)
- Make it easier to update — deleting `data/staticdata` is bad UX
- A lot of things are slower on Windows — investigate and/or add spinners

## Gameplay & world

- Encounter generation and AR
- Tappables: spawn on tiles — avoid water/forbidden areas, more trees in forests?
- Allow setting the maximum cache size for tiles
- Add the level reward buildplates and attach them to level ups
- Export buildplates in both formats
- Custom Java resource packs? (tool to convert them into Earth/Bedrock resource packs)
- Option to only allow custom login — Microsoft accounts cannot be verified
- Support importing Vienna data — warn and merge if some data already exists

## Accounts & permissions

- Associate a player profile with a user; permissions that only allow editing the associated player
- Show roles on the profile page
- Launch/connect to remote components — e.g. run the buildplate launcher on another PC
- View the player buildplate's template (if it exists) — open page, search by id?

## Refactoring

- Get rid of LinkedList
- Use Guid instead of string
- Load static data types only when needed
- Generated regex
- Use new extension syntax
