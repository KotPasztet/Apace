# KONTEKST — całe repo po polsku

> Ten plik to jednorazowy przewodnik po całym projekcie: co jest gdzie, jak
> działa pipeline kosmetyków, jakie decyzje zapadły i dlaczego. Szczegóły
> techniczne żyją w `docs/*.md` — tu jest mapa i kontekst. Aktualizowany przy
> dużych zmianach (ostatnio: 2026-11-26 — **face-cosmetics recovery**
> (restore script, cache-proof landing, karta coverage), **fix-qr-permission-context**
> w patcherze, PR #4 iOS XAL keychain, „Cosmetics sources").

## 1. Co to jest

Apace to prywatny serwer + panel dla **Minecraft Earth** (zamrożony klient MCE
0.33.0, tytuł PlayFab `20ca2`) — gra oficjalnie martwa od 2021, ale klient nadal
się łączy i gra, jeśli podmienimy mu backend. Zestaw:

- **Apace.ApiServer** — emuluje wszystkie endpointy, których klient szuka
  (PlayFab `playfabapi.com`, Xbox Live `*xboxlive.com`, live.com, |
  environmentsettings itd.). Tu żyje cały pipeline kosmetyków.
- **Apace.LauncherUI (panel)** — Blazor Server: konfiguracja, zarządzanie
  graczami, plikami, kosmetykami; uruchamia serwer gry (Fabric 1.20.4) i
  ApiServer.
- **Apace-Launcher** (osobne repo) — aplikacja mobilna na Androida (PRoot,
  bez Gradle: `javac` + build-tools), która uruchamia cały stack na telefonie
  i aplikuje patche na APK/IPA klienta (patcher w tym samym repo).
- Serwer gry (Fabric) dostaje resource pack wygenerowany przez panel; klient
  MCE łączy się przez patched APK z adresem serwera (np. `e.choroszcz.org:1808`).

## 2. Repo — mapa

| Co | Gdzie |
| --- | --- |
| ApiServer (kontrolery, pipeline persona) | `src/Apace.ApiServer/` |
| Panel + zarządzanie | `src/Apace.LauncherUI/` |
| Baza (LiteDB/EarthDB) | `src/Apace.DB/` |
| Statyczne dane klienta (submodule `Earth-Restored/Solace.StaticData`) | `staticdata/` |
| Nasze nakładki na staticdata (skin pack twins, layout, StoreConfig) | `staticdata-extras/playfab/` |
| Manifest assetów (fakty: URL-e, sha256) | `staticdata-extras/playfab/mojang-assets.manifest.json` |
| Skrypty narzędziowe (crawl, build piece, probe) | `scripts/` |
| Dokumentacja głęboka | `docs/` |
| CHANGELOG (bardzo szczegółowy) | `CHANGELOG.md` |
| Publikacja (Docker, install) | `Dockerfile`, `install.sh`, `publish.ps1` |

Kluczowe pliki ApiServer (pipeline kosmetyków):

| Plik | Rola |
| --- | --- |
| `Services/LivePersonaCatalog.cs` | oferta, cache binarek, warm-up, lazy probe, kompatybilność geometrii |
| `Services/PersonaPackGate.cs` | wybrane pakiety (`packs.json`), bramka packów, knob-y |
| `Services/PersonaPieceTypes.cs` | grupy kategorii, bramka kompatybilności (`EvaluateCosmetic`) |
| `Services/PersonaRigRepairer.cs` | naprawa rigów w czasie serwowania (marker `r3`) |
| `Services/PersonaContentCrypto.cs` | AES kontenerów `ppack0.zip` |
| `Services/PersonaMissingPieces.cs` | registry „piece missing" (drop referencji z profili) |
| `Services/PersonaStubBinary.cs` | placeholder (importuje się, nie renderuje) |
| `Controllers/PlayfabApi/CatalogController.cs` | Catalog/Search + GetPublishedItem (oferta do klienta) |
| `Controllers/EarthApi/PersonaCdnController.cs` | pobieranie binarek przez klienta |
| `Controllers/EarthApi/MojangImagesCdnController.cs` | miniatury (lazy) |
| `Controllers/PlayfabApi/InventoryController.cs` | receipty z ContentKey (RS256) |
| Panel: `Services/LivePersonaCatalogSync.cs` | crawl marketplace'u → `index.json` |
| Panel: `Services/PersonaPackIndex.cs`, `MojangSkinPackBuilder.cs`, `MojangSkinTextureFetcher.cs` | pickery packów, build „Minecraft Default Skins" |

## 3. Dane na wolumenie (`<data>/mojang-assets/`)

- `persona-catalog/index.json` — skrawk marketplace'u (dedupe per piece UUID,
  ~12k pieces + skin packs). **Jedyny katalog** — nic z niego nie trafia do
  repo (fakty na wolumenie, kod w repo).
- `persona-catalog/items/<id>.json` — surowe metadane PlayFab.
- `persona-catalog/binaries/<itemId>.zip` — **mirror binarek** (warm-up).
- `persona-catalog/probe-state.json` — verdicty live/dead + URL-e.
- `persona-catalog/packs.json` — wybór packów i knob-y (hot-reload).
- `playfab/persona/*.zip` — mirror w tle (pliki ręczne/legacy nadpisują
  verdicty), `playfab/skinpacks/`, `images/`, `skins/`.
  **Knob „Enable legacy cosmetics”** (packs.json `enableLegacyCosmetics`,
  default ON): OFF = ten katalog jest ignorowany w całości (oferta, warm-up,
  dead→alive, serwowanie) — piece bez żywej binarki znikają z dressing roomu.

## 4. Pipeline kosmetyków (serce projektu)

```
Panel sync (co 24h / "Sync now")      ApiServer
  Catalog/Search (skip/top, ~1.4 r/s)   oferta = index ∩ binarki-na-dysku
  → index.json (~minuty)                (GetOffer / GetHiddenItemIds)
                  │
                  ▼
Warm-up (background, streaming)
  batch resolve: Catalog/GetItems, 20 id / paced call (bisect przy all-or-nothing)
  → od razu pobieranie (6 równoległych GET, CDN, bez pacera)
  → 404 na wszystkich kształtach CDN = dead verdict
  scope: skins ∪ selected bundles ∪ (wszystko gdy "Fill blank slots" ON)
                  │
                  ▼
Serwowanie: klient Widzi TYLKO piece z binarką na dysku
  (mirror-first: brak pliku = brak na liście — zero „wczytywanie…" w kółko)
```

**Fakty o CDN (zmierzone 25.11, 300-item sample):** ~48% katalogu żyje,
ale WYŁĄCZNIE na `content1/2.prod.catalog.playfab.com` (nowy CDN, nowe
republikacje). Wszystko hostowane na xforge (`xforgeassets001/002`) — martwe
na wszystkich kształtach (primary + alternatywy). Stary content z 2021
faktycznie został usunięty przez Microsoft; alternatywne kształty URL NIC
nie wskrzeszają (0/155). Miniatury żyją, bo mają własny lazowy tor
(live CDN URL → mirror GitHub `Daniel-Ric/Bedrock-Persona` → placeholder) —
dlatego „miniaturki są, a binarek nie ma": to dwie różne magistrale.

## 5. Wybrane pakiety i filler

- `packs.json` (`/Admin/PersonaPacks`): defaulty packów (theme tagi `tag.*`),
  granty per gracz, `categoryLimit`, `onlySupported`, `fillBlankSlotsWithRandomCosmetics`,
  `experimentalAutoRepairRigs`, `customSkeletonMode`, **`enableLegacyCosmetics`**
  (włącz/wyłącz mirror legacy — patrz sekcja 3).
  **Baseline (defaulty): limit 45, onlySupported ON, fill ON, auto-repair OFF,
  customSkeletonMode = FlatOnly, legacy cosmetics ON.**
- Akcesoria serwują się TYLKO z wybranych pakietów (theme tag ∩ allowed);
  skiny (`persona_skin` + MarketplaceDurableCatalog packs) zawsze.
- **Filler** („wypełnij puste sloty losowymi kosmetykami", default ON): pool =
  WSZYSTKIE zmirrorowane akcesoria przechodzące bramkę kompatybilności
  (deterministycznie, sort po item id), doklejany po pack-filtered przed
  limiterem grup — **każda kategoria wypełnia się kwalifikującymi się
  kosmetykami AŻ DO LIMITU (45)**. Warm-up ściaga binarki dla całego poola
  (scope = ten sam switch: ON → wszystko, OFF → selected bundles + skiny).
  Gdy OFF — pool pusty.
- **Limiter grup** (`ApplyGroupedCategoryLimit`): pierwsze 45 per grupa
  wygrywa — staticdata i customs liczą się pierwsze, potem pack-selected,
  potem filler. Grupy z layoutu; nieznane typy mają własny kubełek.
- **Cache oferty NIE może zamarzać**: ukryty zbiór (binarka na dysku?)
  odświeża się przez licznik `MarkBinaryLanded()` (każde lądowanie pliku).
  Poprzedni sygnał — mtime katalogu binarek — na Docker volume NIE DRGAŁ:
  oferta stała na 257 piece'ach mimo 759 pobranych, pool fillera na 255,
  a zakładki twarzy dostawały „matched no items". Objaw zamarznięcia w
  logu: „Blank-slot filler pool rebuilt: N candidates" z identycznym stampem
  przez wiele minut przy aktywnym warm-upie.

## 6. Kompatybilność i naprawa rigów (marker `r3`)

- Bramka (`EvaluateCosmetic`): hide przy positive evidence — format_version
  ≥ cap, particles, render controllers, brakujące texture binding, face bones
  (tylko face_accessory — ALE repair-aware: przy aktywnym serve-time
  transformie rewriter usuwa kość face, więc piece serwuje się zamiast być
  ukryta; bez transformy hide bez zmian), oversized head bounds.
- **customSkeletonMode = FlatOnly (default)**: piece z custom kośćmi NIE są
  ukrywane, bo strippem STAJĄ SIĘ płaskie — rewriter przy serwowaniu scala
  custom kości w najbliższy standardowy przodek (rigid, rest pose 1:1).
  Dzięki temu zakładki twarzy (head/eyes/mouth/face_accessory — prawie
  wszystkie mają custom kości) wypełniają się do limitu zamiast być puste.
  Ukryte pozostają tylko piece, których rewriter nie naprawi (particles,
  render controllers, texture binding, format cap, oversized bounds).
- **Face accessories wypełniają się jak reszta**: kość face rigu (MCPE-165232
  — klient jej nie binduje) nie blokuje już kategorii face_item: bramka
  przepuszcza face_accessory przy aktywnym transformie (FlatOnly itd.),
  a rewriter przy serwowaniu usuwa kość face (cubes → `head`/przodek;
  samotna kość face jest RENAMOWANA na `head` — reszta pose 1:1, bo
  origin/pivot cube'ów są absolutne). Bez transformy (skeleton mode `off`,
  auto-repair off) te piece pozostają ukryte jak dawniej.
- Head-family re-parent na `head` działa TYLKO gdy włączony auto-repair i NIE
  ma strip mode; subtree z rotacjami idzie ZAWSZE na rigid strip (rotacja
  wokół złego pivota to było „całkowicie psuje geometrię").
- **ANIMACJE: usuwane** — blok `animations` z geometrii, a pliki
  `*.animation.json` dostają pusty payload
  `{"format_version":"1.8.0","animations":{}}` (wpis w zipie zostaje,
  manifest się nie sypie). Piece ryzykowne TYLKO przez animacje serwują się
  statycznie.
- ETagi z markerem `r3`/`r3-strip`/`r3-flat` — zmiana markera = klient
  pobiera nową wersję.
- „Kolory ze złych części ciała" na niektórych piece'ach: podejrzewamy
  bindowanie tekstur po stronie klienta (piece referencuje teksturę, którą
  klient resolves do skóra gracza) — NIE wynika z rewriteru (ten nie dotyka
  UV/tekstur). Do diagnozy na próbce, gdy wróci temat.

## 7. Skiny klasyczne i statyczne

- Klient MCE nie ma skompilowanych piece'ów — wszystkie pobiera. Vanilla
  Standard pack (Steve/Alex) siedzi W KLIENCIE (`data/skin_packs`).
- „Minecraft Default Skins" buduje panel (`MojangSkinPackBuilder`) z 9 vanilla
  tekstur (minecraft.wiki, manifest kind `skin-texture`).
- `staticdata-extras/playfab/items/` = TYLKO to, czego live nie zastąpi
  (skin pack twins, layout `PersonaQueryManifest_V1.0`, `StoreConfig_V1.1`).
  **Bold Rabbit Feet** jest hardcoded w `CatalogController` (shop wymaga).
- **Usunięte 25.11:** 231 Microsoft persona pieces (dead linki + copyright)
  i Apace Cape (nie dało się wyekipować; kontener i receipty działały, klucz
  urządzeniowo niepotwierdzony).

## 8. Receipty / ContentKey

`InventoryController` serwuje JWS `Receipt` (RS256, własny cert z wolumenu)
z entitlementem `FriendlyId → ContentKey` dla **każdego** pack uuid, który
klient może pobrać (custom cosmetics + wszystkie piece z oferty + surowy index
+ staticdata). Klucz = wbudowany klucz Skin&Persona klienta. Bez wpisu klient
wisi na imporcie (download 200 → cisza). `GetReceiptIssuerCertificateAsPem`
serwuje twin certu.

## 9. Panel — strony

`/Admin/PersonaPacks` (marketplace management): karta **„Cosmetics sources”**
(„Enable legacy cosmetics” — packs.json, hot-reload; „Enable marketplace sync”
— config.json, bramkuje crawl + oba „Sync now”, default ON), karta
**„Cosmetics coverage”** (per zakładka dressing roomu: index / on disk / in
offer; czerwony badge = 0 w ofercie przy niezerowym indeksie — sygnatura
pustych face tabów; liczy wyłącznie na „Refresh”), `PackPicker`,
`Files` (przeglądarka wolumenu), `PlayerManagement`, `FabricLogs`,
`ImportBuildplate`, `LinkedAccounts`. Start serwera gry czeka na resource pack,
prompt o map key, autodetekcja LAN IP. Konfiguracja systemowa: live marketplace ON/OFF,
interval syncu, consent na Mojang assets.

## 10. Apace-Launcher (mobilne) — skrót

Osobne repo `ApaceProject/Apace-Launcher` (push → `main` = CI buduje APK).
Kanał „dev" porównuje wersję z obrazem `ghcr.io/apaceproject/apace:dev`,
„main" z GitHub Releases. **Prompt update (od v1.1.15):** nowsza wersja +
webserver OFF (STOPPED/ERROR) + brak aktywnego snooze → dialog **przy każdym
otwarciu apki** (bez gate'u „raz na wersję"); przy działającym serwerze bez
dialogu. Auto-start czeka na decyzję („Update” → svcUpdate, „Later” → start).
APK aplikuje się z panelu (podpisane download URL-e).

Patcher APK/IPA w tym repo (`src/Apace.LauncherUI/Patcher/` + łatek w
`repos/Minecraft_Earth_Patcher/src/Core/Patches/*.patch`): łatka
**fix-qr-permission-context** (Android, **default ON**) naprawia crash
skanera QR multiplayer — NPE na statycznym `MainActivity.mInstance` w
`genoa.Permission` (fallback na `ActivityThread.currentApplication()`) +
guardy `DeviceOrientation`/`AndroidNFC` (konstruktory budowane przez genoa
JNI bez ExceptionCheck; NFC degraduje do „unavailable"); zweryfikowana na
urządzeniu (Android 16, 26.11). Łatka **fix-arcore-session-race**
(default ON) — hex-patch 4 bajtów w libgenoa.so @ 0x10158E8:
`AndroidAR::stop()` niszczy sesję AR SYNCHRONICZNIE (był jedyny asynchroniczny
call-site → wyścig `ArSession_create` z trwającym `ArSession_destroy`
w ARCore 1.50 Dynamite → SIGSEGV/Breakpad przy ponownym wejściu na ekrany AR
skanera). Standalone patcher z P1–P7: `~/mce-qr-patcher/patch_mce_qr.py`
(poza repo), APK terenowe QRfix3.

## 11. CI/CD

- Apace: push → `dev` = Lint + **Docker image `:dev`** (na którym działa
  telefon kanałem dev); tagi/wersje = release.
- Apace-Launcher: push → `main` = artifact `ApaceLauncher` (APK).
- Stan (26.11, po face-recovery pushu): `dev` @ `6e92d63` — restore script,
  cache-proof landing binarek, karta coverage, łatki QR w patcherze
  (permission-context + arcore-session-race), fix multiplayer
  encounters/state, cisze w logach marketplace.

## 12. Skrypty (scripts/)

`fetch_persona_catalog.py` (legacy crawl), `probe_persona_tags.py` (tagi packów),
`generate_mojang_asset_manifest.py` (manifest), `build_apace_piece.py`
(kontenery ppack0), `extract-persona-pieces.py` (piece'y z resource packa
klienta), `validate_theme_packs.py`, `smoke_live_catalog_sync.py`,
**`restore_persona_mirror.py`** (ratunek 234 binarek persona z historii git
`8e0ec99^` → wolumen; `--list/--target/--tarball/--verify`; odmowa zapisu w
worktree), `update.sh/ps1`, `tailscale-setup.sh`, `entrypoint.sh` (Docker).

## 13. Decyzje i pułapki (chronologicznie, najważniejsze)

1. Klient jest zamrożony na geometrii ≤1.16.0 i standardowym rigu — wszystko
   poza tym = ryzyko (bramka), chyba że rewriter naprawi.
2. **Mirror-first** — klient nie może zobaczyć piece'a bez binarki na dysku
   (wcześniej: „wczytywanie…" i nic). Zasada nadrzędna.
3. Metadane = fakty (repo nic nie zawiera); binarki = wolumen; **zero
   Microsoft-copyright w repo** (231 piece'ów i miniatur już nie ma).
4. Receipt musi obejmować KAŻDY uuid, który klient może zażądać (inaczej
   wiszący import).
5. PlayFab API: Search bez Contents; GetItems all-or-nothing (bisect!);
   ContinuationToken nie działa (skip/top).
6. Patcher przyjmuje TYLKO oryginalny APK/IPA (hash check, hard fail).
7. Domyślne limity: ≤2 req/s do PlayFab API; CDN GET-y bez pacera (max 6
   równolegle w warm-upie).
8. **PR z zewnątrz**: tylko dev, squash-merge z konwencjonalnym tytułem;
   PR-y dotyczące Adventures/seasons/Challenges czekają (release-check).
   Release/tagów NIE robimy samodzielnie — tylko użytkownik.
9. **Face-cosmetics recovery (26.11)**: jedyny źródło binarek twarzy (era
   2019, CDN martwe globalnie) = 234 zipy z historii git (`8e0ec99^`); do
   repo NIGDY nie wracają (copyright), lądują na wolumen przez
   `scripts/restore_persona_mirror.py`. Cache oferty musi widzieć lądowanie
   z KAŻDEGO procesu (content stamps + wymuszony rebuild co 60 s — dir-mtime
   i licznik `MarkBinaryLanded` nie obejmują ręcznych/panelowych zapisów).
   Binary-less listowanie twarzy pozostaje ZAKAZANE (equip wisi — b4dbde5).
   Knob `deadRetryHours` (packs.json, default 24, hand-edit only).
10. **`server_jars/*.jar` NIGDY nie przebudowywać z forków (27.11 — wypadek)**.
    Jar fountain w repo to prebuilt z Apace-Rework (`a121f31`); jego źródła
    nie istnieją w żadnym repo na tej maszynie — fork
    `KotPasztet/Fountain-bridge` nie ma ani jednego commita z
    `RemotePlayers` (klasy renderujące zdalnych graczy jako nametagi i
    przechwytujące `AddPlayerPacket`, na którym klient Genoa pada).
    Przebudowa z forka = ciche usunięcie multiplayera (kick, gdy w
    instancji jest drugi gracz). Tak właśnie zepsułem dev w `65aa00f`
    (opisane błędnie jako „tylko logi"); cofnięte w `62a009d`.
    **Jedyne ważne binarki: te z Apace-Rework / z `main`.** Instrumentacja
    mostku i PR #1 z forka NIE są shipped. Przed zmianą czegokolwiek w
    `server_jars/`: porównać listę klas z wersją z `main`.

## 14. Znane problemy / otwarte wątki

- **Crash #2 — drugi guzik skanera QR (ekrany AR)**: root-caused w
  disassembly (asynchroniczny destroy w `AndroidAR::stop()` → wyścig z
  `ArSession_create`; SIGSEGV łapany przez Breakpad — crash-buffer pusty,
  minidumpy na urządzeniu `/data/user/0/com.kotpasztet.apace/files/error/minidump/new/*.dmp`,
  pull wymaga roota). Fix = łatka `fix-arcore-session-race` (default ON) +
  QRfix3 w terenie; **potwierdzenie w terenie oczekiwane** — jak crash wróci,
  wyciągnąć .dmp (root) i wrócić do hipotezy P4 (mutex w `_tryInitialize`).
- **Multiplayer „Couldn't connect" po skanie QR** — naprawione (26.11):
  `encounters/state` odpowiadał 400 na każdy poprawny request (JsonElement
  boxing); teraz 200 + Pristine. Reszta flow (join/instances, partitions)
  działała. Po HTTP gość łączy się przez bridge Bedrock (UDP 19132) — jak
  „timed out" wróci mimo fixa, podejrzeniem jest routing bridge/launchera,
  nie EarthApi.
  Root cause „timed out" u gości z internetu (26.11): klient wybiera
  `ipV4Address` z odpowiedzi instancji, a mobile launcher ma default
  `settings.IPv4 = 127.0.0.1` (Settings.cs) — adres nieosiągalny z sieci, do
  tego UDP 19132 musi być osiągalny od gościa. Fix = konfiguracja: Server
  Options → IPv4 na adres publicznie osiągalny dla gości + restart „Buildplate
  Launcher" (publicAddress czytany tylko przy starcie) + forward UDP 19132 /
  Tailscale; TCP 19132 refused z zewnątrz jest NORMALNE — bridge jest UDP-only
  (RakNet). Fix `encounters/state` (26.11) wymaga obrazu `:dev` na telefonie.
  **Kick „od razu” na dev (27.11)** — spowodowany przeze mnie: jar fountain
  bez `RemotePlayers` (patrz §13.10). Jary przywrócone z `main` w `62a009d`;
  **po wdrożeniu dev z tym commitem multiplayer ma wrócić do stanu z main —
  wymaga potwierdzenia w terenie (2×iPhone, iPhone+Android)**. Odrębne,
  nadal otwarte: gość wisi ~30 s na `buildplate_loading_screen` po udanym
  `playerConnected` (log z 19:25 i z 22:03). ApiServer tego nie widzi —
  potrzebny **log launchera buildplate** (`Instance.cs`: „Creating
  instance … on persistent Fabric server", „Instance … is ready", „host
  player must connect first") i **log Java Server** (zakładka w panelu).
  Od `2e9cf49` powód wcześniejszego stopu instancji trafia do loga
  ApiServer jako Warning i do body 500.
- **Face cosmetics (eyes/mouth/face_accessory/hair) — mechanizm domknięty,
  czeka na potwierdzenie w terenie** (26.11). Diagnoza: jedyny warunek
  zerujący te zakładki to brak binarki na dysku (pack/compat gate ich w
  ogóle nie dotyka); binarki 2019 martwe na CDN. Wgrane: restore script
  (§12/§13.9), cache-proof landing (§13.9), histogramy oferty + karta
  „Cosmetics coverage” (§9). **Do zrobienia na instalacji**: wypchnąć obraz
  `:dev` z tym HEAD, lądować mirror (`--target` na hoście docker albo
  `--tarball` + adb push + untar do `<data>/mojang-assets/playfab/persona/`),
  restart ApiServer, sprawdzić kartę coverage i equip w grze. Pokrycie po
  restore: hair 41/139, eyes 31/57, mouth 18/35, skin 7/37,
  **facial_hair 0/39** (tab pusty bez nowego materiału — craft to jedyna
  droga), face_accessory do pomiaru po restore. „Podstawowe” pozycje twarzy
  (plain_eyes/plain_mouth/body) są wbudowane w klienta — nie od nas.
- **Skin packs (Classic Skins row) masowo dead na CDN** — pack zipy z 2021
  404; vanilla Standard w kliencie działa, reszta czeka na cud.
- Text binding „złe kolory” na części piece’ów — do diagnozy (sekcja 6).
- Emotes: w indexie (~2.2k), ale layout klienta nie ma zakładki — nie oferowane.
- Kanał dev: obraz musi zawierać HEAD commitu; launcher pilnuje
  `ciPending` i re-checkuje co 3 min.
