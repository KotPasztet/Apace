# TODO

## Privacy & anti-abuse (next major update, in priority order)

- Cloudflare Turnstile ("Enable CF") on the account creation page, plus rate limits on registration and login (per IP / per account)
- "Hide location" server option: player coordinates handled RAM-only for the duration of a single request, never logged (log scrubber), never written to the database, rounded to the map tile grid before use
- Client-side (APK patch) coordinate rounding before sending, so the server never receives precise GPS at all
- Public privacy-info endpoint + banner on the login/registration page + native APK confirmation dialog showing whether the server owner can see the player's location
- Release integrity: reproducible builds, signed per-artifact SHA-256 manifests from CI, published public verification key, /.well-known/apace/integrity endpoint, launcher/client verification (documented honestly: full remote attestation is impossible without hardware support)

- Use EFCore for earth DB, migrations are way easier that way, but also need to write an initial migration from the current schema
- Show exceptions in launcher logs
- Make it easier to update, deleting data/staticdata is kinda bad UX
- Launcher buildplate preview - liquid rendering
- Shop management
- Player items management
- Encounter generation and AR
- Use tiles when spawning tappables - don't spawn on water/forbidden areas, spawn more trees in forest?
- Allow setting maximum cache size for tiles
- Allow custom java resourcepacks? (tool to turn them into earth(bedrock) resourcepacks)
- Some option to only allow custom login - because we cannot verify microsoft accounts
- Support importing Vienna data - if some data already exists - warn and merge
- Move logs folder up to the folder with run_launcher.ps1 - cli arg to specify log file location on all programs
- View old logs in launcher?
- Clear logs - seperate permission
- Show roles on profile page
- Export buildplates in both formats
- Launch/connect to remote components - e.g. run buildplate launcher on another PC
- View the player buildplate's template (if exists) - open page, search id?
- Add the level reward buildplates and add them to level ups
- NFC mini figures
- Some kind of auth for the logs, maybe pass a random secret to the cli args and verify it in the controller?
- A lot of things are quite slower on windows, investigate and/or add spinners
- Associate a player profile with a user, permissions that only allow editing the associated player.
- find out the tokens for the first time tutorial, daily login and possibly more
- per instance logs server/bridge

## Refactoring

- Get rid of LinkedList
- Use Guid instead of string
- Load static data types only when needed
- Generated regex
- Use new extension syntax
