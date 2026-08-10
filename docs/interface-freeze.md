# Interface Freeze — Apace Refactoring Contract

**Wersja:** v1.0
**Data zamrozenia:** 2026-08-10
**Zakres:** Apace C#, Fountain-bridge, Fountain-fabric, Fountain-connector-plugin-base
**Przeznaczenie:** Punkt synchronizacji dla Agentow 1-4 pracujacych rownolegle

---

## 1. Dokladny dzisiejszy przeplyw: od "play" do swiata

### Krok 1: Zewnetrzne API wywoluje start instancji

```
POST /api/buildplates/start (lub podobny kontroler)
```

Kod: `BuildplatesController` w `src/Solace.ApiServer/` → wywoluje `BuildplateInstancesManager` → wysyla `RequestAsync("buildplates", "start")` do event busa.

### Krok 2: InstanceManager odbiera zadanie "start"

Plik: `src/Solace.Buildplate/Launcher/InstanceManager.cs`

- `InstanceManager.CreateAsync()` (linie 58-239) rejestruje `RequestHandler` na kolejce `"buildplates"`
- Gdy przychodzi request typu `"start"` (linia 67), deserializuje `StartRequest` (linie 32-39):
  - `PlayerId`, `EncounterId`, `BuildplateId`, `Night`, `Type` (InstanceType enum: BUILD/PLAY/SHARED_BUILD/SHARED_PLAY/ENCOUNTER/PLAYER_ADVENTURE), `ShutdownTime`
- Na podstawie `InstanceType` ustala parametry (linie 95-153):
  - `survival`, `saveEnabled`, `inventoryType` (SYNCED/DISCARD/BACKPACK), `buildplateSource` (PLAYER/SHARED/ENCOUNTER), `shutdownTime`
- Generuje `instanceId = U.RandomUuid().ToString()` (linia 161)
- Wywoluje `_starter.StartInstance(...)` (linia 165)
- Publikuje event `"started"` z adresem i portem (linie 172-179)

### Krok 3: Starter przydziela porty i uruchamia Instance

Plik: `src/Solace.Buildplate/Launcher/Starter.cs`

- `StartInstance()` (linie 45-65):
  - Tworzy katalog tymczasowy `CreateInstanceBaseDir()` (linia 47) w `/tmp/vienna-buildplate-instance_{instanceId}`
  - Alokuje porty przez `FindPort()` (linie 53-54):
    - Port Bedrock (UDP): od `BASE_PORT=19132` wzwyz (`_portsInUse`)
    - Port Java server (TCP): od `SERVER_INTERNAL_BASE_PORT=25565` wzwyz (`_serverInternalPortsInUse`)
    - `FindPort()` (linie 67-79): lock, iteruje, sprawdza `CanBindPort()` (TCP+UDP proba bindowania)
  - Wywoluje `Instance.Run(...)` (linia 55)

### Krok 4: Instance.RunAsync — przygotowanie plikow

Plik: `src/Solace.Buildplate/Launcher/Instance.cs`

- `Instance.Run()` (linie 22-35) — metoda statyczna, tworzy obiekt, czeka na `_threadStartedSemaphore`, uruchamia `RunAsync()`
- `RunAsync()` (linie 117-304):
  1. Laczy sie z event busem: `AddPublisherAsync()`, `AddRequestSenderAsync()` (linie 140-141)
  2. Wysyla request o dane swiata (linie 145-151):
     - PLAYER: `"load"` → `BuildplateLoadRequest(playerId, buildplateId)`
     - SHARED: `"loadShared"` → `SharedBuildplateLoadRequest(buildplateId)`
     - ENCOUNTER: `"loadEncounter"` → `EncounterBuildplateLoadRequest(buildplateId)`
     - Odpowiedz: `BuildplateLoadResponse.ServerDataBase64` (NBT swiata zakodowane w base64)
  3. `SetupServerFiles(serverData)` (linie 630-749):
     - Kopiuje `fabricJarName` (fabric-server-mc.1.20.4-loader.0.15.10-launcher.1.0.1) do katalogu roboczego
     - Kopiuje katalogi: `.fabric/server`, `libraries`, `versions`, `mods`
     - Pisze `eula.txt` (eula=true)
     - Pisze `server.properties`: `online-mode=false`, `enforce-secure-profile=false`, `sync-chunk-writes=false`, `server-port={_serverInternalPort}`, `gamemode={survival/creative}`, `vienna-event-bus-address`, `vienna-event-bus-queue-name`
     - Tworzy katalog `world/` z `level.dat` (linie 770-833):
       - `GameType`: 0 (survival) / 1 (creative)
       - `Difficulty`: 1
       - `DayTime`: 6000 (dzien) / 18000 (noc)
       - GameRules: `doDaylightCycle=false`, `doWeatherCycle=false`, `doMobSpawning=false`, `fountain:doMobDespawn=false`, `keepInventory=true`
       - WorldGenSettings: `fountain:wrapper` dla `minecraft:overworld` (ground_level=63, inner=noise) i `minecraft:the_nether` (ground_level=32, inner=noise z nether_wastes)
     - Rozpakowuje `serverData` (zip z regionami swiata) do `world/`
  4. `SetupBridgeFiles()` (linie 836-849): tworzy pusty katalog bridge

### Krok 5: Start serwera Minecraft (Fabric)

- `StartServerProcessAsync()` (linie 865-917):
  - `ConsoleProcess`: `useShellExecute: true`, `redirect: false`, `openInNewWindow: true`
  - Komenda: `java -jar {fabricJarName} -nogui`
  - Uruchamiane w katalogu serwera

### Krok 6: Serwer Fabric startuje

Serwer Fabric (1.20.4) laduje mody:
- `fountain-core` (Main.java, `DedicatedServerModInitializer.onInitializeServer()`):
  - Rejestruje gamerule `fountain:doMobDespawn`
  - Rejestruje komende `fountain:earthmode`
  - Rejestruje custom payload channels: `fountain:earth_mode`, `fountain:inventory_sync_request`, `fountain:set_hotbar_request`, `fountain:item_particle`
- `fountain-generator` (Main.java, `ModInitializer.onInitialize()`):
  - Rejestruje custom bloki: `invisible_constraint`, `blend_constraint`, `border_constraint`, `solid_air`, `non_replaceable_air`
  - Rejestruje chunk generatory: `fountain:empty`, `fountain:wrapper`
- Plugin connector Vienna (buildplate-connector-plugin) uzywa `server.properties` (`vienna-event-bus-address`, `vienna-event-bus-queue-name`) do polaczenia z event busem C# na porcie 5532

Po pelnym uruchomieniu serwera, connector plugin wysyla event `"started"` na per-instance kolejke `buildplate_{InstanceId}`.

### Krok 7: Instance odbiera "started", uruchamia bridge

- `HandleConnectorEvent()` (linie 306-387): gdy `@event.Type == "started"`:
  - `StartBridgeProcessAsync()` (linie 919-997):
    - Komenda bridge: `java -jar {fountainBridgeJar} -port {Port} -serverAddress 127.0.0.1 -serverPort {_serverInternalPort} -connectorPluginJar {connectorPluginJar} -connectorPluginClass micheal65536.vienna.buildplate.connector.plugin.ViennaConnectorPlugin -connectorPluginArg {JSON} -useUUIDAsUsername`
    - JSON arg: `{EventBusAddress, EventBusQueueName, InventoryType}`
  - Publikuje `"ready"` na kolejce `"buildplates"` (linia 314)
  - Startuje timeout: `HOST_PLAYER_CONNECT_TIMEOUT=120s` (linie 315-317) lub `StartShutdownTimer()` dla ENCOUNTER

### Krok 8: Bridge startuje i nasluchuje

Plik: `repos/Fountain-bridge/src/main/java/micheal65536/fountain/Main.java`

- `Main.main()` (linie 81-211):
  - Parsuje CLI: `--port` (default 19132), `--serverAddress` (default 127.0.0.1), `--serverPort` (default 25565), `--connectorPluginJar`, `--connectorPluginClass`, `--connectorPluginArg`, `--useUUIDAsUsername`
  - Laduje connector plugin przez refleksje z zewnetrznego JARa (linie 214-282): `ClassLoader`, konstruktor, `init(arg, logger)`
  - Tworzy `SessionsManager` (linia 180)
  - Binduje `NioDatagramChannel` na `0.0.0.0:{port}` przez `RakChannelFactory.server(...)` (linie 198-210)

### Krok 9: Gracz klika "play" — klient Bedrock laczy sie

1. Klient Minecraft Bedrock laczy sie na `{PublicAddress}:{Port}` (UDP)
2. RakNet handshake odbywa sie w bibliotece Protocol (poza bridge)
3. Bridge otrzymuje `LoginPacket`

### Krok 10: Bridge autoryzuje gracza

Plik: `repos/Fountain-bridge/src/main/java/micheal65536/fountain/SessionsManager.java`

- `newClientConnection()` (linie 61-83): ustawia codec Bedrock_v425_Genoa, przypina `LoginBedrockPacketHandler`
- `handleLogin()` (linie 85-138):
  1. Ekstrahuje `LoginInfo` (uuid, username) z `LoginPacket`
  2. Sprawdza duplikaty UUID
  3. Wywoluje `connectorPlugin.onPlayerConnected(new PlayerLoginInfo(uuid))` — metoda in-memory Javy (linia 106)
  4. ViennaConnectorPlugin przez event bus wysyla `"playerConnected"` request do C# przez kolejke `buildplate_{InstanceId}`
  5. C# Instance: `HandleConnectorRequest("playerConnected")` (linie 393-426) — sprawdza czy host player, wysyla request na `"buildplates"` queue
  6. Jezeli zaakceptowano, bridge tworzy:
     - `MinecraftProtocol` z custom codekiem
     - `TcpClientSession` do `{serverAddress}:{serverPort}` (linie 126)
     - `PlayerSession` z `PlayerConnectorPluginWrapper` (linie 128)
     - `ClientPacketHandler` i `ServerPacketHandler` (linie 132-133)
     - Wywoluje `tcpClientSession.connect(true)` (linia 134)

### Krok 11: Bridge laczy sie z serwerem Java jako klient

- `TcpClientSession` z mcprotocollib laczy sie do serwera Fabric na `127.0.0.1:{_serverInternalPort}`
- Serwer Java wysyla `ClientboundLoginPacket` → bridge `PlayerSession.onJavaLogin()` (linie 240-352):
  - Ustawia dimensionId=0 (hardcoded, linia 257)
  - Ustawia chunk center na (0, 128, 0) (hardcoded, linia 112)
  - Ustawia chunk radius na 20 (hardcoded, linia 111)
  - Wysyla `StartGamePacket`, `ChunkRadiusUpdatedPacket`, `NetworkChunkPublisherUpdatePacket`
  - Wysyla puste chunki dla calego radiusa (linie 326-336)
  - Wysyla `fountain:earth_mode` custom payload (linie 341-346)

### Krok 12: Serwer Java wysyla chunki, bridge tlumaczy

- Serwer Fabric generuje chunki przez `fountain:wrapper` → `WrapperChunkGenerator`
- Bridge odbiera `ClientboundLevelChunkWithLightPacket` → `PlayerSession.onJavaLevelChunk()` → `ChunkManager.onJavaLevelChunk()` → translacja blokow Java→Bedrock → `LevelChunkPacket` do klienta
- Gracz widzi swiat

---

## 2. Miejsce tworzenia procesow per sesja

### Obecny model: 1 instance = 2 procesy Java

Kazde wywolanie `Instance.RunAsync()` spawnuje DOKLADNIE 2 procesy potomne:

### Proces 1: Serwer Fabric

| Wlasciwosc | Wartosc |
|---|---|
| Klasa/metoda | `Instance.StartServerProcessAsync()` (linia 865) |
| Plik | `src/Solace.Buildplate/Launcher/Instance.cs` |
| Komenda | `java -jar {fabricJarName} -nogui` |
| Katalog roboczy | `{baseDir}/server/` |
| Shell execute | `true` (otwiera nowe okno terminala) |
| Redirect I/O | `false` |

### Proces 2: Fountain-bridge

| Wlasciwosc | Wartosc |
|---|---|
| Klasa/metoda | `Instance.StartBridgeProcessAsync()` (linia 919) |
| Plik | `src/Solace.Buildplate/Launcher/Instance.cs` |
| Komenda | `java -jar {fountainBridgeJar} -port {Port} -serverAddress 127.0.0.1 -serverPort {_serverInternalPort} -connectorPluginJar ... -connectorPluginClass ... -connectorPluginArg {JSON} -useUUIDAsUsername` |
| Katalog roboczy | `{baseDir}/bridge/` |
| Shell execute | `true` (otwiera nowe okno terminala) |
| Redirect I/O | `false` |

### Wywolanie spawnujace

```
Starter.StartInstance()                               # Starter.cs linia 45
  → Instance.Run()                                    # Instance.cs linia 22
    → Instance.RunAsync()                             # Instance.cs linia 117
      → StartServerProcessAsync()                     # Instance.cs linia 228
      → ...czeka na "started" event...
      → StartBridgeProcessAsync()                     # Instance.cs linia 313
```

### Zarzadzanie procesami

- `ConsoleProcess` (`src/Solace.Common/ConsoleProcess.cs`): opakowuje `System.Diagnostics.Process`
- Bridge nie spawnuje procesu Java — **laczy sie** z juz dzialajacym serwerem
- `_runningInstanceCount` w `InstanceManager` (linia 17) sluzy wylacznie do drenazu przy shutdown — **brak twardego limitu instancji**

### Wiele sesji w jednym procesie bridge

W obrebie jednej instancji, bridge obsluguje wielu graczy na jednym procesie i jednym UDP gniezdzie:
- Kazdy gracz dostaje wlasna `PlayerSession` → `TcpClientSession` do tego samego backendu
- `SessionsManager.activeSessions`: `HashSet<ActiveSession>` z `{uuid, PlayerSession, BedrockServerSession}`
- Wszystkie sesje wspoldziela to samo gniazdo UDP (port 19132+)

---

## 3. Wersje

### Minecraft i ekosystem Fabric

| Komponent | Wersja | Zrodlo |
|---|---|---|
| Minecraft (Java Edition) | **1.20.4** | `build.gradle` Fountain-fabric linia 48 |
| Fabric Loader | **0.15.0** (testowany do 0.15.6) | `build.gradle` Fountain-fabric linia 50 |
| Fabric API | **0.91.1+1.20.4** (testowany do 0.94.1) | `build.gradle` Fountain-fabric linia 51 |
| Yarn mappings | **1.20.4+build.1:v2** | `build.gradle` Fountain-fabric linia 49 |
| Fabric Loom | **1.5-SNAPSHOT** | `build.gradle` Fountain-fabric linia 2 |
| Java (Fabric) | **17** | `build.gradle` Fountain-fabric linia 33 |

### Biblioteki bridge

| Komponent | Wersja | Zrodlo |
|---|---|---|
| Java (bridge) | **17** | `pom.xml` Fountain-bridge linia 12 |
| mcprotocollib | **1.20.4-2-20240116.220521-7** | `pom.xml` Fountain-bridge linia 90 |
| Cloudburst Bedrock Protocol | **3.0.0.Genoa-SNAPSHOT** | `pom.xml` Fountain-bridge linia 83 |
| Bedrock codec (Genoa) | **v425** | `SessionsManager.java` linia 74 |

### Biblioteki connector-plugin-base

| Komponent | Wersja | Zrodlo |
|---|---|---|
| Java | **17** | `pom.xml` Fountain-connector-plugin-base linia 12 |
| groupId | `micheal65536.fountain` | `pom.xml` linia 7 |
| artifactId | `connector-plugin-base` | `pom.xml` linia 8 |
| version | **0.0.1-SNAPSHOT** | `pom.xml` linia 9 |

### Serwer Fabric (JAR w server_jars)

| Plik | Oczekiwana nazwa |
|---|---|
| fabric-server | `fabric-server-mc.1.20.4-loader.0.15.10-launcher.1.0.1` |
| buildplate-connector-plugin | JAR z klasa `micheal65536.vienna.buildplate.connector.plugin.ViennaConnectorPlugin` |
| fountain (bridge) | JAR z `micheal65536.fountain.Main` |

### DataVersion / WorldVersion

| Wartosc | Zrodlo |
|---|---|
| DataVersion | `3700` (level.dat, Instance.cs linia 821) |
| Version Id | `19133` (level.dat, Instance.cs linia 822) |
| Version Name | `1.20.4` (level.dat, Instance.cs linia 825) |

---

## 4. Stan statyczny ChunkGenerator — WERDYKT

### `fountain:wrapper` NIE MA stanu statycznego. Jest bezpieczny do wielokrotnego instancjowania.

Dowody z `WrapperChunkGenerator.java` (linie 44-289):

```java
public class WrapperChunkGenerator extends ChunkGenerator implements NoiseConfigChunkGenerator
{
    public static final Codec<WrapperChunkGenerator> CODEC = ...;  // ← STATYCZNY, ale to CODEC (fabryka deserializacji), nie stan

    private final ChunkGenerator chunkGenerator;                    // ← final, instancyjny
    private final EarthConstraintBlocksCalculator earthConstraintBlocksCalculator; // ← final, instancyjny
```

- **CODEC** jest jedynym polem statycznym i jest to fabryka deserializacji Codec — NIE stan. Kazde wywolanie `CODEC.decode()` tworzy NOWA instancje `WrapperChunkGenerator`.
- **`chunkGenerator`** (linia 69): `private final` — delegat wewnetrznego generatora (np. `NoiseChunkGenerator`), ustawiany w konstruktorze.
- **`earthConstraintBlocksCalculator`** (linia 70): `private final` — kalkulator blokow ograniczajacych buildplate, ustawiany w konstruktorze.

### `EarthConstraintBlocksCalculator` — rowniez bez stanu statycznego

Plik: `EarthConstraintBlocksCalculator.java` (linie 9-138):

```java
public class EarthConstraintBlocksCalculator
{
    public final int buildplateWidth;        // ← final
    public final int buildplateDepth;        // ← final
    public final int buildplateGroundLevel;  // ← final
    public final int buildplateUndergroundHeight; // ← final

    private final BlockState airBlockState;                    // ← final, immutable
    private final BlockState bedrockBlockState;                // ← final, immutable
    private final BlockState invisibleConstraintBlockState;    // ← final, immutable
    private final BlockState blendConstraintBlockState;        // ← final, immutable
    private final BlockState borderConstraintBlockState;       // ← final, immutable
    private final BlockState solidAirBlockState;               // ← final, immutable
    private final BlockState nonReplaceableAirBlockState;      // ← final, immutable
```

- Wszystkie pola sa `final` — ustawiane raz w konstruktorze, nigdy nie modyfikowane
- `BlockState` z Minecraft to immutable snapshot — nie ma mutowalnego stanu
- Buildplate zakodowany na twardo wokol (0,0), rozmiar domyslnie 32
- `getEarthConstraintBlockAt(x, y, z)` (linie 33-121): czysta funkcja — zwraca `BlockState` na podstawie wspolrzednych i konfiguracji

### Wniosek

`fountain:wrapper` moze byc bezpiecznie instancjowany wielokrotnie — rownolegle instancje serwera Fabric kazda z wlasnym ChunkGeneratorem nie beda miec konfliktow stanu. Jedyne ryzyko: `CODEC` jest polem statycznym, ale to singleton fabryczny (deserializuje konfiguracje do nowych obiektow), a nie wspoldzielony stan.

---

## 5. Format komend Connector Plugin ↔ Bridge (Kontrakt v1)

### Mechanizm komunikacji

- **Czyste, bezposrednie wywolania metod Javy in-memory** — NIE string-commandy, NIE JSON
- Bridge tworzy instancje pluginu przez refleksje (`Main.loadConnectorPlugin()` w Fountain-bridge, linie 214-282)
- Bridge opakowuje plugin per-player w `PlayerConnectorPluginWrapper`
- Plugin dziedziczy po interfejsie `ConnectorPlugin` (z `Fountain-connector-plugin-base`)

### Interfejs ConnectorPlugin (v1)

Plik: `repos/Fountain-connector-plugin-base/src/main/java/micheal65536/fountain/connector/plugin/ConnectorPlugin.java`

```java
public interface ConnectorPlugin
{
    // Inicjalizacja
    void init(@NotNull String arg, @NotNull Logger logger) throws ConnectorPluginException;

    // Zamkniecie
    void shutdown() throws ConnectorPluginException;

    // Gracz probuje sie polaczyc — zwroc false zeby odrzucic
    boolean onPlayerConnected(@NotNull PlayerLoginInfo playerLoginInfo) throws ConnectorPluginException;

    // Gracz sie rozlaczyl — zwroc DisconnectResponse
    @NotNull DisconnectResponse onPlayerDisconnected(@NotNull String playerId, float health) throws ConnectorPluginException;

    // Gracz zginal — zwroc true zeby zrespawnowac
    boolean onPlayerDead(@NotNull String playerId) throws ConnectorPluginException;

    // Pobierz inventory gracza
    @NotNull Inventory onPlayerGetInventory(@NotNull String playerId) throws ConnectorPluginException;

    // Dodaj stackowalny przedmiot do inventory
    void onPlayerInventoryAddItem(@NotNull String playerId, @NotNull String itemId, int count) throws ConnectorPluginException;

    // Dodaj niestackowalny przedmiot (z instanceId — wear)
    void onPlayerInventoryAddItem(@NotNull String playerId, @NotNull String itemId, @NotNull String instanceId, int wear) throws ConnectorPluginException;

    // Usun stackowalny przedmiot — zwroc liczbe faktycznie usunietych
    int onPlayerInventoryRemoveItem(@NotNull String playerId, @NotNull String itemId, int count) throws ConnectorPluginException;

    // Usun niestackowalny przedmiot — zwroc true jesli usuniety
    boolean onPlayerInventoryRemoveItem(@NotNull String playerId, @NotNull String itemId, @NotNull String instanceId) throws ConnectorPluginException;

    // Aktualizuj zuzycie (wear) przedmiotu
    void onPlayerInventoryUpdateItemWear(@NotNull String playerId, @NotNull String itemId, @NotNull String instanceId, int wear) throws ConnectorPluginException;

    // Ustaw hotbar
    void onPlayerInventorySetHotbar(@NotNull String playerId, Inventory.HotbarItem[] hotbar) throws ConnectorPluginException;
}
```

### Typy danych

**PlayerLoginInfo** (`PlayerLoginInfo.java`):
```java
public final class PlayerLoginInfo {
    @NotNull public final String uuid;
    // TODO: should have join code here as well
}
```

**DisconnectResponse** (`DisconnectResponse.java`):
```java
public final class DisconnectResponse {
    // TODO: this should contain the fields required by the Genoa disconnect packet
    public DisconnectResponse() { /* TODO */ }
}
```

**Inventory** (`Inventory.java`):
```java
public final class Inventory {
    // StackableItem[] — przedmioty stackowalne (kazdy ma uuid + count)
    // NonStackableItem[] — przedmioty niestackowalne (kazdy ma uuid + instanceId + wear)
    // HotbarItem[7] — hotbar (7 slotow, kazdy moze byc null)
```

### Uwagi do v1 (istniejace TODO w kodzie)

1. `PlayerLoginInfo` — brak join code (pole do dodania)
2. `DisconnectResponse` — brak pol (pusta klasa, do wypelnienia danymi Genoa disconnect)
3. Brak pojecia instancji/dimensionow — kontrakt operuje wylacznie per `playerId`
4. "instanceId" w Inventory to identyfikator instancji przedmiotu (wear tracking), NIE identyfikator dimensionu

---

## 6. Kontrakt Event Bus Apace C# ↔ Java

### Architektura

```
[Apace C# EventBus Server] ← TCP:5532 (localhost) → [ViennaConnectorPlugin (Java)]
```

- EventBusServer: `src/Solace.EventBus.Server/`, nasluchuje na `127.0.0.1:5532`
- EventBusClient: `src/Solace.EventBus.Client/EventBusClient.cs`
- Protokol: **TCP, ASCII, newline-delimited**
- Wszystkie dane payloadu: **JSON**

### Format ramek (wire protocol)

Kazda wiadomosc to jedna linia tekstu zakonczona `\n`:

```
{channelId} {COMMAND} [{args}]
```

Gdzie:
- `channelId` — unikalny identyfikator kanalu (int, przydzielany przy rejestracji)
- `COMMAND` — jedno z: `PUB`, `SUB`, `REQ`, `HND`, `SEND`, `REP`, `NREP`, `ACK`, `ERR`, `CLOSE`

### Komendy

| Komenda | Kierunek | Format | Opis |
|---|---|---|---|
| `PUB` | Client→Server | `{ch} PUB` | Rejestruj jako publisher |
| `SUB` | Client→Server | `{ch} SUB {queueName}` | Subskrybuj kolejke |
| `REQ` | Client→Server | `{ch} REQ` | Rejestruj jako request sender |
| `HND` | Client→Server | `{ch} HND {queueName}` | Rejestruj jako request handler |
| `SEND` | Publisher→Server | `{ch} SEND {queueName}:{type}:{data}` | Publikuj event |
| `REQ` | Sender→Server | `{ch} REQ {queueName}:{type}:{data}` | Wyslij request |
| `REP` | Server→Sender | `{ch} REP {data}` | Odpowiedz na request |
| `NREP` | Server→Sender | `{ch} NREP` | Brak handlera / brak odpowiedzi |
| `ACK` | Server→Sender | `{ch} ACK` | Potwierdzenie odebrania requestu |
| `ERR` | Server→Client | `{ch} ERR` | Blad kanalu |
| `CLOSE` | Client→Server | `{ch} CLOSE` | Zamknij kanal |

### Kolejki

| Nazwa kolejki | Typ | Opis |
|---|---|---|
| `buildplates` | Wspolna | Komunikacja InstanceManager ↔ API server (start/preview/load/saved/playerConnected/inventory*) |
| `buildplate_{InstanceId}` | Per-instancja | Komunikacja Instance ↔ ViennaConnectorPlugin (started/ready/inventory*/playerConnected/playerDisconnected/playerDead/getInventory) |

### Typy wiadomosci na kolejce `buildplates` (wspolna)

| Typ | Kierunek | Format danych |
|---|---|---|
| `start` | REQ | `StartRequest` JSON |
| `preview` | REQ | `PreviewRequest` JSON (zawiera `ServerDataBase64`) |
| `load` | REQ | `BuildplateLoadRequest` JSON (playerId, buildplateId) |
| `loadShared` | REQ | `SharedBuildplateLoadRequest` JSON (sharedBuildplateId) |
| `loadEncounter` | REQ | `EncounterBuildplateLoadRequest` JSON (encounterBuildplateId) |
| `started` | PUB | `StartNotification` JSON (instanceId, playerId, address, port, type) |
| `ready` | PUB | `InstanceId` (string) |
| `shuttingDown` | PUB | `InstanceId` (string) |
| `stopped` | PUB | `InstanceId` (string) |
| `saved` | REQ | `RequestWithInstanceId{wrapper: WorldSavedMessage}` |
| `playerConnected` | REQ | `RequestWithInstanceId{wrapper: PlayerConnectedRequest}` |
| `playerDisconnected` | REQ | `RequestWithInstanceId{wrapper: PlayerDisconnectedRequest}` |
| `playerDead` | REQ | `RequestWithInstanceId{wrapper: playerId}` |
| `getInventory` | REQ | `RequestWithInstanceId{wrapper: playerId}` |
| `getInitialPlayerState` | REQ | `RequestWithInstanceId{wrapper: playerId}` |
| `findPlayer` | REQ | `RequestWithInstanceId{wrapper: FindPlayerIdRequest}` |
| `inventoryAdd` | REQ | `RequestWithInstanceId{wrapper: InventoryAddItemMessage}` |
| `inventoryRemove` | REQ | `RequestWithInstanceId{wrapper: InventoryRemoveItemRequest}` |
| `inventoryUpdateWear` | REQ | `RequestWithInstanceId{wrapper: InventoryUpdateItemWearMessage}` |
| `inventorySetHotbar` | REQ | `RequestWithInstanceId{wrapper: InventorySetHotbarMessage}` |

### Typy wiadomosci na kolejce `buildplate_{InstanceId}` (per-instancja)

| Typ | Kierunek | Opis |
|---|---|---|
| `started` | PUB (Java→C#) | Serwer Fabric gotowy, mozna startowac bridge |
| `saved` | PUB (Java→C#) | Swiat zapisany |
| `playerConnected` | REQ (Java→C#) | Gracz probuje sie polaczyc |
| `playerDisconnected` | REQ (Java→C#) | Gracz sie rozlaczyl |
| `playerDead` | REQ (Java→C#) | Gracz zginal |
| `getInventory` | REQ (Java→C#) | Pobierz inventory |
| `getInitialPlayerState` | REQ (Java→C#) | Pobierz stan poczatkowy gracza (earthmode) |
| `findPlayer` | REQ (Java→C#) | Znajdz gracza po nazwie Minecraft |
| `inventoryAdd` | PUB (Java→C#) | Przedmiot dodany do inventory |
| `inventoryRemove` | REQ (Java→C#) | Przedmiot usuniety z inventory |
| `inventoryUpdateWear` | PUB (Java→C#) | Zuzycie przedmiotu zaktualizowane |
| `inventorySetHotbar` | PUB (Java→C#) | Hotbar ustawiony |

---

## 7. Miejsca do zmiany — przydzial Agentow

### Agent 1: Fountain-fabric (serwer Fabric)

**Repo:** `repos/Fountain-fabric/`
**Odpowiada za:** zmiane architektury serwera z 1:1 per-instancja na multi-instancje

| Co zmienic | Plik | Ryzyko |
|---|---|---|
| Dodac obsluge wielu dimensionow na jednym serwerze | Nowy modul / refactor `core` | WYSOKIE — brak istniejacych hookow join/leave, tylko mixiny |
| Dynamiczna rejestracja `fountain:wrapper` per dimension | `generator/.../Main.java` | SREDNIE — rejestracja przez `Registry.register()`, moze wymagac dynamicznego CODEC |
| Per-dimension entity tracking | `core/.../PlayerManagerMixin.java` | SREDNIE — juz istnieje mixin, tylko podmiana logiki filtrowania |
| Earth mode per-player per-dimension | `core/.../earthmode/` | NISKIE — juz per-player, flaga `earthMode` |
| Zarzadzanie cyklem zycia dimensionow (create/load/unload) | Nowy kod | WYSOKIE — Minecraft nie ma natywnego API do dynamicznych dimensionow, wymaga custom rozszerzen |
| Wiele bridge'y laczy sie do jednego serwera | `server.properties` / connector plugin | SREDNIE — serwer musi akceptowac wiele polaczen z roznymi uuid |
| Usuniecie hardcodowanego `minecraft:overworld` jako jedynego dimensionu | `level.dat` generator + datapack | SREDNIE — world preset `wrapper.json` ma tylko overworld |

### Agent 2: Fountain-bridge (bridge Bedrock↔Java)

**Repo:** `repos/Fountain-bridge/`
**Odpowiada za:** zmiane architektury bridge z 1:1 per-instancja na wspoldzielony/multi-instancje

| Co zmienic | Plik | Ryzyko |
|---|---|---|
| Dodac mape routingu dimension→instance | `SessionsManager.java` | WYSOKIE — bridge dzis nie ma pojecia dimensionow, `PlayerSession` ma hardcoded `setDimensionId(0)` |
| Przy logowaniu gracza wybierac odpowiednia instancje/dimension | `SessionsManager.handleLogin()` linie 85-138 | WYSOKIE — dzis `connectorPlugin.onPlayerConnected()` nie zwraca informacji o dimensionie |
| Refactor `PlayerSession`: usunac hardcoded `HARDCODED_CHUNK_CENTER`, `HARDCODED_CHUNK_RADIUS` | `PlayerSession.java` linie 110-112 | SREDNIE — chunk center i radius powinny byc konfigurowalne per serwer |
| Wiele `TcpClientSession` do roznych backendow (lub jednego z roznymi dimensionId) | `SessionsManager.handleLogin()` linia 126 | WYSOKIE — zmiana z 1:1 bridge:backend na N:1 lub N:M |
| Nowy format `PlayerLoginInfo` z join code/dimensionId | `connector/PlayerConnectorPluginWrapper.java` | SREDNIE — wymaga zmiany kontraktu ConnectorPlugin v1→v2 |

### Agent 3: Fountain-connector-plugin-base (kontrakt pluginow)

**Repo:** `repos/Fountain-connector-plugin-base/`
**Odpowiada za:** rozszerzenie kontraktu ConnectorPlugin z v1 na v2

| Co zmienic | Plik | Ryzyko |
|---|---|---|
| Dodac `joinCode` do `PlayerLoginInfo` | `PlayerLoginInfo.java` linia 9 | NISKIE — TODO juz jest |
| Wypelnic `DisconnectResponse` polami Genoa disconnect | `DisconnectResponse.java` | SREDNIE — wymaga znajomosci protokolu Genoa |
| Dodac do `ConnectorPlugin` metody zwiazane z dimensionami (join/switch/leave) | `ConnectorPlugin.java` | SREDNIE — nowe metody nie lamia istniejacych (rozszerzenie interfejsu) |
| Dodac `instanceId` / `dimensionId` do `PlayerLoginInfo` | `PlayerLoginInfo.java` | NISKIE |
| Potencjalnie: zmiana z in-memory na wire protocol | Caly interfejs | NISKIE/WYSOKIE — zalezy od decyzji architektonicznej; dzis to czyste wywolania metod, co jest wydajne ale wymaga wspolnego classloadera |

### Agent 4: Apace C# (launcher + event bus)

**Repo:** `/home/aleksander/Apace/src/`
**Odpowiada za:** zmiane sposobu spawn processow i zarzadzania instancjami

| Co zmienic | Plik | Ryzyko |
|---|---|---|
| Zamiast 1:1 server+bridge per instance → wspoldzielone procesy | `Starter.cs`, `Instance.cs`, `InstanceManager.cs` | BARDZO WYSOKIE — fundamentalna zmiana architektury |
| Nowa logika `FindPort` — mniej portow (wspoldzielone) | `Starter.cs` linie 67-79 | SREDNIE |
| Zmiana `SetupServerFiles` — wiele swiatow w jednym serwerze | `Instance.cs` linie 630-749 | WYSOKIE — level.dat ze wszystkimi dimensionami, nie tylko overworld+nether |
| Zmiana `StartServerProcessAsync` — serwer startuje raz (lub z poola) | `Instance.cs` linie 865-917 | WYSOKIE |
| Zmiana `StartBridgeProcessAsync` — bridge startuje raz (lub z poola) | `Instance.cs` linie 919-997 | WYSOKIE |
| Nowa logika `connectorPluginArg` — JSON z lista dimensionow/instancji | `Instance.cs` linia 108-112 | SREDNIE |
| Lifecycle: start/stop serwera → start/stop dimensionu zamiast calego procesu | `Instance.cs` `RunAsync()` | WYSOKIE |
| Shutdown: per-dimension zamiast per-proces | `Instance.cs` `BeginShutdown()` linie 1044-1083 | SREDNIE |
| Nowy model timeoutow (HOST_PLAYER_CONNECT_TIMEOUT per-dimension) | `Instance.cs` linia 20 | SREDNIE |

### Macierz zaleznosci miedzy Agentami

```
Agent 3 (kontrakt) ──── definiuje v2 ────┬── Agent 1 (Fabric) ──── implementuje v2 po stronie serwera
                                         ├── Agent 2 (Bridge) ──── implementuje v2 po stronie bridge
                                         └── Agent 4 (Apace C#) ── implementuje v2 po stronie launcher
```

**Kolejnosc prac:**
1. Agent 3: zamrozenie kontraktu v2 (pierwszy krok — inni na niego czekaja)
2. Rownolegle: Agent 1 + Agent 2 + Agent 4 (po otrzymaniu kontraktu v2)

---

## Podsumowanie kluczowych faktow

| Fakt | Wartosc |
|---|---|
| Obecny model | 1 instance = 2 procesy Java (server Fabric + bridge), oba per-instancja |
| Port Bedrock | 19132 + offset per instancja |
| Port Java server (internal) | 25565 + offset per instancja |
| Event bus port | 5532 (TCP, localhost) |
| Serwer Fabric nasluchuje | na `_serverInternalPort` (localhost) |
| Bridge nasluchuje | na `Port` (publiczny, 0.0.0.0) |
| Komunikacja bridge↔Java | TcpClientSession (mcprotocollib) |
| Komunikacja bridge↔plugin | In-memory Java (ConnectorPlugin interfejs) |
| Komunikacja plugin↔Apace C# | TCP event bus (ASCII, JSON) |
| Protokol Bedrock↔bridge | RakNet UDP, codec v425_Genoa |
| ChunkGenerator | `fountain:wrapper` — BRAK stanu statycznego, bezpieczny do wielokrotnego instancjowania |
| Wymiar(y) | `minecraft:overworld` + `minecraft:the_nether` (obydwa przez `fountain:wrapper`) — brak rejestracji wlasnych/custom dimensionow |
| Hooki join/leave | NIE MA — mod jest 100% reaktywny |
| Chat | Vanilla, globalny |
| Komendy | Tylko `fountain:earthmode` (permission level 3) |
| GameRules | `fountain:doMobDespawn` (custom, default=false w level.dat) |
| Custom payload channels | `fountain:earth_mode`, `fountain:inventory_sync_request`, `fountain:set_hotbar_request`, `fountain:item_particle` |
| Buildplate | Zakodowany na twardo wokol (0,0), rozmiar 32 |
| API port (publiczny) | 5000 (ASP.NET) |
| Docker | Publikuje 5000, 1808, 5532, 19132/udp na 0.0.0.0 |
