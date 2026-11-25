# KONTEKST — całe repo po polsku

> Ten plik to jednorazowy przewodnik po całym projekcie: co jest gdzie, jak
> działa pipeline kosmetyków, jakie decyzje zapadły i dlaczego. Szczegóły
> techniczne żyją w `docs/*.md` — tu jest mapa i kontekst. Aktualizowany przy
> dużych zmianach (ostatnio: 2026-11-25).

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
  `experimentalAutoRepairRigs`, `customSkeletonMode`, `maxGeometryFormatVersion`.
  **Baseline (defaulty): limit 45, onlySupported ON, fill ON, auto-repair OFF,
  customSkeletonMode = FlatOnly.**
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
  (tylko face_accessory), oversized head bounds.
- **customSkeletonMode = FlatOnly (default)**: piece z custom kośćmi NIE są
  ukrywane, bo strippem STAJĄ SIĘ płaskie — rewriter przy serwowaniu scala
  custom kości w najbliższy standardowy przodek (rigid, rest pose 1:1).
  Dzięki temu zakładki twarzy (head/eyes/mouth/face_accessory — prawie
  wszystkie mają custom kości) wypełniają się do limitu zamiast być puste.
  Ukryte pozostają tylko piece, których rewriter nie naprawi (particles,
  render controllers, texture binding, format cap, oversized bounds,
  face-bone face_accessory).
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

`/Admin/PersonaPacks` (marketplace management), `PackPicker`, `Files`
(przeglądarka wolumenu), `PlayerManagement`, `FabricLogs`, `ImportBuildplate`,
`LinkedAccounts`. Start serwera gry czeka na resource pack, prompt o map key,
autodetekcja LAN IP. Konfiguracja systemowa: live marketplace ON/OFF,
interval syncu, consent na Mojang assets.

## 10. Apace-Launcher (mobilne) — skrót

Osobne repo `KotPasztet/Apace-Launcher` (push → `main` = CI buduje APK).
Kanał „dev" porównuje wersję z obrazem `ghcr.io/kotpasztet/apace:dev`,
„main" z GitHub Releases. **Prompt update (od v1.1.15):** nowsza wersja +
webserver OFF (STOPPED/ERROR) + brak aktywnego snooze → dialog **przy każdym
otwarciu apki** (bez gate'u „raz na wersję"); przy działającym serwerze bez
dialogu. Auto-start czeka na decyzję („Update" → svcUpdate, „Later" → start).
APK aplikuje się z panelu (podpisane download URL-e).

## 11. CI/CD

- Apace: push → `dev` = Lint + **Docker image `:dev`** (na którym działa
  telefon kanałem dev); tagi/wersje = release.
- Apace-Launcher: push → `main` = artifact `ApaceLauncher` (APK).

## 12. Skrypty (scripts/)

`fetch_persona_catalog.py` (legacy crawl), `probe_persona_tags.py` (tagi packów),
`generate_mojang_asset_manifest.py` (manifest), `build_apace_piece.py`
(kontenery ppack0), `extract-persona-pieces.py` (piece'y z resource packa
klienta), `validate_theme_packs.py`, `smoke_live_catalog_sync.py`,
`update.sh/ps1`, `tailscale-setup.sh`, `entrypoint.sh` (Docker).

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

## 14. Znane problemy / otwarte wątki

- **Skin packs (Classic Skins row) masowo dead na CDN** — pack zipy z 2021
  404; vanilla Standard w kliencie działa, reszta czeka na cud.
- Text binding „złe kolory" na części piece'ów — do diagnozy (sekcja 6).
- Emotes: w indexie (~2.2k), ale layout klienta nie ma zakładki — nie oferowane.
- Kanał dev: obraz musi zawierać HEAD commitu; launcher pilnuje
  `ciPending` i re-checkuje co 3 min.
