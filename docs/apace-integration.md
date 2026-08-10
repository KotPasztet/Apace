# Apace C# Orchestrator — Integration Guide (v2 Dimension-Based Architecture)

> **Status:** Work-in-progress reference for the v2 refactoring.
> This guide describes the **target** dimension-based architecture agreed in the frozen
> contracts (`connector-contract-v2.md` and `interface-freeze.md`). The C# launcher
> code is being migrated from the v1 per-process model to this v2 model. Where a
> component is planned but not yet implemented, it is called out explicitly.
>
> **Related documents:**
> - `/home/aleksander/Apace/docs/interface-freeze.md` — v1 system architecture, event bus protocol, versions
> - `/home/aleksander/Apace/repos/Fountain-connector-plugin-base/docs/connector-contract-v2.md` — the frozen v2 Java interface contract

---

## 1. Architecture Overview

In v1 every game instance was a full JVM process pair (one Fabric server + one
bridge) with its own ports. In v2 the whole buildplate subsystem is collapsed into
**two long-lived JVM processes** shared by every instance, and a game instance is
now a **dimension** dynamically registered inside the single Fabric server.

```
                      ┌────────────────────────────────────────────┐
                      │            Bedrock clients                 │
                      │         (Minecraft Earth app)              │
                      └─────────────────────┬──────────────────────┘
                                            │ RakNet / UDP
                                            │ 19132/udp  (SINGLE shared port)
                                            ▼
                      ┌────────────────────────────────────────────┐
                      │    Persistent Fountain-bridge   (1 JVM)    │
                      │  SessionsManager · PlayerSession(s)        │
                      │  loads ViennaConnectorPlugin via reflection│
                      └───────────────┬───────────────────┬────────┘
                                      │ in-memory         │ mcprotocollib TcpClientSession
                                      │ ConnectorPlugin   │ (one TCP conn per player,
                                      │ interface calls   │  each with own dimensionId)
                                      ▼                   ▼
                      ┌────────────────────────────────────────────┐
                      │   ViennaConnectorPlugin (in bridge JVM)    │
                      │   createInstance · destroyInstance         │
                      │   bindPlayerToInstance                     │
                      └───────────────┬────────────────────────────┘
                                      │ TCP event bus — port 5532
                                      │ (ASCII, JSON, newline-delimited)
                                      ▼
                      ┌────────────────────────────────────────────┐
                      │  Apace C# Orchestrator (BuildplateLauncher)│
                      │  Program.cs  →  PersistentProcessManager   │
                      │             →  InstanceManager  →  Starter │
                      └────────────────────────────────────────────┘
                                      │  dimension lifecycle (mediated
                                      │  by the plugin — see note)
                                      ▼
                      ┌────────────────────────────────────────────┐
                      │   Persistent Fountain-fabric   (1 JVM)     │
                      │   dynamic multi-dimension support          │
                      │   instance = a registered dimension        │
                      │   fountain:wrapper chunk generator         │
                      └────────────────────────────────────────────┘
```

**Key properties of the new model:**

- **One Fabric server.** The `fountain:wrapper` chunk generator is stateless
  (verified in `interface-freeze.md` §4) and safe to instantiate many times, so a
  single persistent Fabric JVM can host every dimension concurrently.
- **One bridge.** The bridge binds a single UDP port (`19132/udp`) and opens one
  `TcpClientSession` to the Fabric server **per player session**. Each session carries
  its own `dimensionId` in the Java login packet, so players in different instances
  are placed into the correct dimension.
- **The orchestrator never talks to Fabric directly.** The C# side only speaks the
  event bus protocol. The `ViennaConnectorPlugin` (running inside the bridge JVM)
  translates event bus messages into `ConnectorPlugin` interface calls; the actual
  dimension registration happens against the Fabric mod.
- **Persistent state.** The single Fabric server keeps its worlds under
  `/app/launcher/persistent_fabric` (host mount
  `/opt/apace-persistent/fabric-data`), so all dimensions survive container restarts.

> **Note on the dimension control channel:** the exact transport by which the
> `ViennaConnectorPlugin` invokes the Fabric mod's dimension API (in-JVM, a dedicated
> control connection, etc.) is left to the Agent 1/2 implementation. From the C#
> orchestrator's point of view this is invisible — it only exchanges event bus
> messages with the plugin.

---

## 2. Key Changes from v1 (Per-Process) to v2 (Dimension-Based)

| Aspect | v1 (per-process) | v2 (dimension-based) |
|---|---|---|
| Fabric server processes | 1 per game instance | **1 persistent, shared** by all instances |
| Bridge processes | 1 per game instance | **1 persistent, shared** by all instances |
| What an instance is | A full JVM process pair | A **dimension** inside the shared Fabric server |
| Instance identity | Port offset + process boundary | Orchestrator-level **UUID string** → dimension key |
| Bedrock port | `19132 + offset` per instance | `19132/udp` — **single shared** bridge port |
| Java internal port | `25565 + offset` per instance | Single internal port (one Fabric server) |
| Player routing | Implicit — login to the instance's own process | **Explicit** — `bindPlayerToInstance` returns `dimensionId` |
| Instance lifecycle | Spawn/kill JVM processes | `createInstance` / `destroyInstance` (event bus) |
| World data | Per-process `world/` under `/tmp/...` | Dimension under `/app/launcher/persistent_fabric` |
| Start-up cost of an instance | Full JVM boot (seconds) | Dimension registration (fast) |
| Port allocation (`FindPort` / `CanBindPort`) | Per-instance scan + bind test | **Removed** — no per-instance ports |
| Process management | `StartServerProcessAsync` / `StartBridgeProcessAsync` per instance | **`PersistentProcessManager`** owns the two shared processes |

---

## 3. Component Diagram

```
┌──────────────────────────────────────────────────────────────┐
│  Apace C# Orchestrator (BuildplateLauncher)                  │
│                                                              │
│  ┌──────────────┐  ┌──────────────┐  ┌────────────────────┐ │
│  │ Program.cs   │  │ Starter.cs   │  │ InstanceManager.cs │ │
│  │ Entry point  │  │ Instance     │  │ Request handler,   │ │
│  │ Starts PPM   │  │ factory      │  │ instance tracking  │ │
│  └──────┬───────┘  └──────┬───────┘  └─────────┬──────────┘ │
│         │                 │                     │            │
│  ┌──────┴─────────────────┴─────────────────────┴──────────┐ │
│  │  PersistentProcessManager                               │ │
│  │  - StartFabricAsync()  StartBridgeAsync()  StopAllAsync()│ │
│  └──────────────────────────┬──────────────────────────────┘ │
│                             │                                │
│  ┌──────────────────────────┴──────────────────────────────┐ │
│  │  EventBusClient (TCP:5532)                              │ │
│  └──────────────────────────┬──────────────────────────────┘ │
└─────────────────────────────┼────────────────────────────────┘
                              │
        ┌─────────────────────┼─────────────────────┐
        │                     │                     │
        ▼                     ▼                     ▼
┌──────────────┐    ┌──────────────┐    ┌────────────────────┐
│ Persistent   │    │ Persistent   │    │ ViennaConnector-   │
│ Fabric       │    │ Fountain     │    │ Plugin (in bridge) │
│ (JVM, 25565) │◄───│ Bridge       │    │                    │
│              │    │ (UDP 19132)  │    │                    │
└──────────────┘    └──────────────┘    └────────────────────┘
```

- `Program.cs` is the entry point. It parses CLI options, connects to the event bus,
  builds the `Starter`, starts the persistent processes via
  `PersistentProcessManager`, then hands control to `InstanceManager`.
- `Starter` becomes the **instance factory** — given an instance request it produces
  the `generatorSettings` payload rather than spawning processes.
- `InstanceManager` owns the `buildplates` request handler and tracks every live
  instance (`instanceId → { dimensionKey, state, players }`).
- `PersistentProcessManager` owns the two shared JVM processes and their health.
- `EventBusClient` is the single TCP connection to the event bus server
  (`127.0.0.1:5532`), multiplexed into publishers, subscribers, request senders and
  request handlers.

---

## 4. Startup Sequence

The v2 orchestrator boots the two shared processes **before** it accepts any
instance requests.

```
Program.Main(args)
  │
  ├─ 1. Parse CLI options (CommandLine) — see §11
  ├─ 2. Configure Serilog (console + logs/buildplate_launcher/log.txt)
  ├─ 3. EventBusClient.ConnectAsync("localhost:5532")
  │         │  (fatal if the event bus server is unreachable)
  ├─ 4. javaCmd = JavaLocator.Locate()
  ├─ 5. starter = new Starter(eventBusClient, ...)
  │
  ├─ 6. PersistentProcessManager.StartFabricAsync()
  │         │  - prepare persistent working dir /app/launcher/persistent_fabric
  │         │  - copy server template (fabric JAR, mods, libraries, .fabric/server)
  │         │  - write eula.txt / server.properties (single internal port 25565)
  │         │  - launch: java -jar {fabricJarName} -nogui
  │         │  - wait for "started" (Fabric ready) before continuing
  │
  ├─ 7. PersistentProcessManager.StartBridgeAsync()
  │         │  - launch: java -jar {fountainBridgeJar}
  │         │            -port 19132
  │         │            -serverAddress 127.0.0.1 -serverPort 25565
  │         │            -connectorPluginJar {connectorPluginJar}
  │         │            -connectorPluginClass ...ViennaConnectorPlugin
  │         │            -connectorPluginArg {JSON}
  │         │            -useUUIDAsUsername
  │         │  - bridge binds 0.0.0.0:19132/udp and loads the plugin
  │
  ├─ 8. instanceManager = await InstanceManager.CreateAsync(eventBusClient, starter)
  │         │  - registers a RequestHandler on the "buildplates" queue
  │         │  - (PPM subscribes to connector events / instance lifecycle)
  │
  ├─ 9. Ready — publish "ready" on "buildplates"
  │
  └─10. Main loop: while(true) Thread.Sleep(1000)
            Ctrl+C / SIGTERM → InstanceManager.ShutdownAsync() → PPM.StopAllAsync()
```

**Fabric readiness gate.** The Fabric server must finish initialising (register the
`fountain:wrapper` generator, load mods, open its internal port) before the bridge is
started, and the bridge must be bound to `19132` before the orchestrator reports
`ready`. This is the v2 replacement for the v1 `HOST_PLAYER_CONNECT_TIMEOUT` gating
per instance.

---

## 5. Instance Lifecycle

Creating an instance no longer spawns JVMs — it registers a dimension.

```
External caller (API server)
  │ POST /api/...  (e.g. start buildplate)
  ▼
BuildplatesController / BuildplateInstancesManager
  │ event bus REQ "start" on "buildplates"
  ▼
InstanceManager (RequestHandler)
  │ 1. deserialize StartRequest { PlayerId, EncounterId, BuildplateId,
  │                              Night, Type, ShutdownTime }
  │ 2. resolve params by InstanceType:
  │      BUILD          survival=false, save=true,  SYNCED,   PLAYER
  │      PLAY           survival=true,  save=false, DISCARD,  PLAYER
  │      SHARED_BUILD   survival=false, save=false, DISCARD,  SHARED
  │      SHARED_PLAY    survival=true,  save=false, DISCARD,  SHARED
  │      ENCOUNTER / PLAYER_ADVENTURE  survival=true, save=false,
  │                                   BACKPACK, ENCOUNTER, shutdownTime
  │ 3. instanceId = U.RandomUuid()   (orchestrator-level UUID)
  │ 4. build generatorSettings JSON (§10) from the resolved params
  ▼
Starter.StartInstance(...)                    [instance factory]
  │ - (no FindPort / CanBindPort / temp dirs in v2)
  │ - produce CreateInstanceRequest { instanceId, "fountain:wrapper", generatorSettings }
  ▼
event bus REQ "createInstance" on "buildplates"
  ▼
ViennaConnectorPlugin.createInstance(request)
  │  - register a new dimension on the persistent Fabric server
  │  - if worldData present: decode base64, unzip, place region files
  ▼
Fabric — dynamic dimension registered
  │  returns dimensionKey  e.g. "fountain:instance_abc123"
  ▼
event bus REP → CreateInstanceResult { success, dimensionKey }
  │
  ├─ success: C# stores  instances[instanceId] = { dimensionKey, state="ready", players=[] }
  │            publishes "started" notification on "buildplates"
  └─ failure: log error, report to caller (no instance created)
```

**C# side expectation (mapping kept by `InstanceManager`):**

```
instances[instanceId] = {
    dimensionKey: "fountain:instance_abc123",
    state:        "ready",           // starting → ready → shuttingDown → stopped
    players:      []
}
```

**Notes:**
- The numeric `dimensionId` (e.g. `42`) is **not** tracked by C# — it is a
  bridge-level routing detail returned by `bindPlayerToInstance`.
- `generatorType` is always `"fountain:wrapper"` for every Apace game type.
- A live `start` request is refused while the orchestrator is shutting down.

---

## 6. Player Login Flow

All Bedrock clients connect to the **same** shared bridge port (`19132/udp`); the
bridge asks the orchestrator which instance each player belongs to, then routes them
to the right dimension.

```
Bedrock client                      Bridge (shared)              ViennaConnectorPlugin        C# orchestrator
     │  1. LoginPacket (RakNet/UDP)      │                               │                         │
     │──────────────────────────────────►│                               │                         │
     │                                   │ 2. extract uuid + joinCode    │                         │
     │                                   │    from LoginPacket           │                         │
     │                                   │ 3. PlayerLogin (event bus REQ)│                         │
     │                                   │───────────────────────────────────────────────────────►│
     │                                   │                               │                         │ 4. authorise player,
     │                                   │                               │                         │    resolve target
     │                                   │                               │                         │    instanceId from joinCode
     │                                   │ 5. PlayerLogin response       │                         │
     │                                   │    { accepted: true,          │                         │
     │                                   │      instanceId: "x" }        │                         │
     │                                   │◄───────────────────────────────────────────────────────│
     │                                   │ 6. bindPlayerToInstance(uuid, instanceId)              │
     │                                   │──────────────────────────────►│                         │
     │                                   │                               │ 7. resolve instance     │
     │                                   │                               │    → numeric dimensionId │
     │                                   │ 9. BindPlayerResult.success(42)│                         │
     │                                   │◄──────────────────────────────│                         │
     │                                   │ 10. TcpClientSession to Fabric│
     │                                   │     with dimensionId = 42     │
     │                                   │──────────────────────────────►│ (Fabric places player in │
     │                                   │                               │  the correct dimension) │
```

**Step details:**

1. The Bedrock client connects to the shared bridge (`0.0.0.0:19132/udp`). The RakNet
   handshake happens in the Protocol library.
2. `SessionsManager.handleLogin()` extracts the player UUID and the optional
   `joinCode` from the `LoginPacket`.
3. The bridge (through the plugin) sends a player-login request to C# on the event bus.
4. C# authorises the player and resolves the target `instanceId` (from the join code,
   server state, etc.).
5. C# responds `{ accepted: true, instanceId: "<target-uuid>" }` (or rejects).
6. The bridge calls `connectorPlugin.bindPlayerToInstance(playerId, instanceId)`.
7-8. The plugin resolves the instance to its numeric dimension ID.
9. The plugin returns `BindPlayerResult.success(42)`.
10. The bridge creates a `TcpClientSession` to the Fabric server and sends
    `dimensionId = 42` in the `ClientboundLoginPacket`, so the player spawns in the
    correct dimension.

**Removed v1 hardcodes (bridge side, per `connector-contract-v2.md` §7.2):**

| Hardcode | v1 value | v2 source |
|---|---|---|
| `dimensionId` | `0` (always overworld) | `BindPlayerResult.dimensionId` |
| `HARDCODED_CHUNK_CENTER` | `(0, 128, 0)` | stored per-instance in `generatorSettings` |
| `HARDCODED_CHUNK_RADIUS` | `20` | stored per-instance in `generatorSettings` |

---

## 7. Instance Destruction Flow

An instance is torn down by unregistering its dimension — no JVM is killed.

```
InstanceManager (C#)                     ViennaConnectorPlugin             Fabric (persistent)
     │ 1. decide to shut down instance        │                               │
     │    (timeout, all players left,         │                               │
     │     explicit shutdown)                 │                               │
     │                                        │                               │
     │ 2. ensure all players have left        │                               │
     │    (migrate / disconnect)              │                               │
     │                                        │                               │
     │ 3. save world data                     │                               │
     │    (existing "saved" flow)             │                               │
     │────────────────────────────────────────────────────────────────────────►│
     │◄────────────────────────────────────────────────────────────────────────│
     │                                        │                               │
     │ 4. event bus REQ "destroyInstance"     │                               │
     │    { instanceId }                      │                               │
     │───────────────────────────────────────►│                               │
     │                                        │ 5. destroyInstance(instanceId)│
     │                                        │──────────────────────────────►│
     │                                        │                               │ 6. verify no players
     │                                        │                               │    in dimension
     │                                        │                               │ 7. unregister dimension
     │                                        │                               │ 8. unload chunks
     │                                        │ 9. DestroyInstanceResult       │
     │                                        │    .success()                 │
     │                                        │◄──────────────────────────────│
     │ 10. event bus REP success              │                               │
     │◄───────────────────────────────────────│                               │
     │ 11. remove instances[instanceId] map   │                               │
     │     publish "stopped" on "buildplates" │                               │
```

**Preconditions (enforced by the orchestrator before calling `destroyInstance`):**
- All players have left the instance (or have been migrated/disconnected).
- World data has been saved.

**Failure modes surfaced by the result:**
- `instance not found: <id>`
- `players still in instance` — destroy refused while players remain.
- A Fabric-level error string from the mod.

---

## 8. Event Bus Integration

The `ConnectorPlugin` Java interface is an in-memory contract between the bridge and
the plugin JAR. The C# orchestrator never calls those methods directly — it
communicates with the `ViennaConnectorPlugin` exclusively over the **event bus**.

### Event bus wire protocol (unchanged from v1)

- Transport: **TCP**, `127.0.0.1:5532`.
- Encoding: **ASCII, JSON payloads, newline-delimited** frames.
- Frame: `{channelId} {COMMAND} [{args}]`.

| Command | Direction | Format | Description |
|---|---|---|---|
| `PUB` | Client→Server | `{ch} PUB` | Register as publisher |
| `SUB` | Client→Server | `{ch} SUB {queueName}` | Subscribe to queue |
| `REQ` | Client→Server | `{ch} REQ` | Register as request sender |
| `HND` | Client→Server | `{ch} HND {queueName}` | Register as request handler |
| `SEND` | Publisher→Server | `{ch} SEND {queue}:{type}:{data}` | Publish event |
| `REQ` | Sender→Server | `{ch} REQ {queue}:{type}:{data}` | Send request |
| `REP` | Server→Sender | `{ch} REP {data}` | Reply to request |
| `NREP` | Server→Sender | `{ch} NREP` | No handler / no reply |
| `ACK` | Server→Sender | `{ch} ACK` | Acknowledge receipt |
| `ERR` | Server→Client | `{ch} ERR` | Channel error |
| `CLOSE` | Client→Server | `{ch} CLOSE` | Close channel |

Example frame (createInstance request, C# → Java):

```
42 REQ buildplates:createInstance:{"instanceId":"abc-123","generatorType":"fountain:wrapper","generatorSettings":{...}}
```

### Queues

| Queue | Type | Role in v2 |
|---|---|---|
| `buildplates` | shared | Orchestrator ↔ API server, and the shared orchestration queue for the new v2 lifecycle messages |
| `buildplate_{instanceId}` | per-instance | Legacy v1 per-instance channel (inventory / player state); with a single shared bridge this is being folded into the shared queue |

> **Note:** `connector-contract-v2.md` §5.1 marks the orchestration queue name as
> TBD ("may be `buildplates` or a new dedicated queue"). The message types below use
> `buildplates` per the current implementation; coordinate with Agent 2 before
> finalising.

### 8.1 New v2 Message Types

| Type | Queue | Direction | Payload |
|---|---|---|---|
| `createInstance` | `buildplates` | REQ C# → Java | `CreateInstanceRequest` JSON |
| `createInstance` | `buildplates` | REP Java → C# | `CreateInstanceResult` JSON |
| `destroyInstance` | `buildplates` | REQ C# → Java | `{instanceId: string}` |
| `destroyInstance` | `buildplates` | REP Java → C# | `DestroyInstanceResult` JSON |
| `bindPlayer` | `buildplate_{instanceId}` | REQ C# → Java | `{playerId, instanceId}` |
| `bindPlayer` | `buildplate_{instanceId}` | REP Java → C# | `BindPlayerResult` JSON |

**`createInstance` request (JSON):**

```json
{
  "instanceId": "abc-123-def-456",
  "generatorType": "fountain:wrapper",
  "generatorSettings": {
    "buildplateWidth": 64,
    "buildplateDepth": 64,
    "buildplateGroundLevel": 63,
    "buildplateUndergroundHeight": 5,
    "gameType": 0,
    "difficulty": 1,
    "dayTime": 6000,
    "keepInventory": true,
    "worldData": null
  }
}
```

**`createInstance` response (success):**

```json
{
  "success": true,
  "dimensionKey": { "namespace": "fountain", "path": "instance_abc123" },
  "error": null
}
```

**`createInstance` response (failure):**

```json
{
  "success": false,
  "dimensionKey": null,
  "error": "generator 'fountain:wrapper' not found on Fabric server"
}
```

**`destroyInstance` response (success):**

```json
{ "success": true, "error": null }
```

**`bindPlayer` response (success):**

```json
{ "success": true, "dimensionId": 42, "error": null }
```

**`bindPlayer` response (failure):**

```json
{ "success": false, "dimensionId": -1, "error": "instance not found: abc-123" }
```

### 8.2 Existing Message Types (unchanged)

The v1 message types remain active (inventory and player-state traffic is unchanged;
only instance creation/destruction and player routing move to the v2 types above).

**On the shared `buildplates` queue:**

| Type | Direction | Payload |
|---|---|---|
| `start` | REQ | `StartRequest` JSON |
| `preview` | REQ | `PreviewRequest` JSON (contains `ServerDataBase64`) |
| `load` | REQ | `BuildplateLoadRequest` JSON (playerId, buildplateId) |
| `loadShared` | REQ | `SharedBuildplateLoadRequest` JSON (sharedBuildplateId) |
| `loadEncounter` | REQ | `EncounterBuildplateLoadRequest` JSON (encounterBuildplateId) |
| `started` | PUB | `StartNotification` JSON (instanceId, playerId, encounterId, buildplateId, address, port, type) |
| `ready` | PUB | `InstanceId` (string) |
| `shuttingDown` | PUB | `InstanceId` (string) |
| `stopped` | PUB | `InstanceId` (string) |
| `saved` | REQ | `RequestWithInstanceId{ WorldSavedMessage }` |
| `playerConnected` | REQ | `RequestWithInstanceId{ PlayerConnectedRequest }` |
| `playerDisconnected` | REQ | `RequestWithInstanceId{ PlayerDisconnectedRequest }` |
| `playerDead` | REQ | `RequestWithInstanceId{ playerId }` |
| `getInventory` | REQ | `RequestWithInstanceId{ playerId }` |
| `getInitialPlayerState` | REQ | `RequestWithInstanceId{ playerId }` |
| `findPlayer` | REQ | `RequestWithInstanceId{ FindPlayerIdRequest }` |
| `inventoryAdd` | REQ | `RequestWithInstanceId{ InventoryAddItemMessage }` |
| `inventoryRemove` | REQ | `RequestWithInstanceId{ InventoryRemoveItemRequest }` |
| `inventoryUpdateWear` | REQ | `RequestWithInstanceId{ InventoryUpdateItemWearMessage }` |
| `inventorySetHotbar` | REQ | `RequestWithInstanceId{ InventorySetHotbarMessage }` |

**On the per-instance `buildplate_{instanceId}` queue** (legacy v1, Java → C#):

| Type | Direction | Description |
|---|---|---|
| `started` | PUB (Java→C#) | Fabric server ready for the instance |
| `saved` | PUB (Java→C#) | World saved |
| `playerConnected` | REQ (Java→C#) | Player attempting to connect |
| `playerDisconnected` | REQ (Java→C#) | Player disconnected |
| `playerDead` | REQ (Java→C#) | Player died |
| `getInventory` | REQ (Java→C#) | Fetch inventory |
| `getInitialPlayerState` | REQ (Java→C#) | Fetch initial player state (earth mode) |
| `findPlayer` | REQ (Java→C#) | Find player by Minecraft name |
| `inventoryAdd` | PUB (Java→C#) | Item added to inventory |
| `inventoryRemove` | REQ (Java→C#) | Item removed from inventory |
| `inventoryUpdateWear` | PUB (Java→C#) | Item wear updated |
| `inventorySetHotbar` | PUB (Java→C#) | Hotbar set |

---

## 9. PersistentProcessManager Reference

`PersistentProcessManager` owns the two shared JVM processes. It is created by
`Program.cs` during startup, which calls `StartFabricAsync()` and `StartBridgeAsync()`
directly. The `Starter` and `InstanceManager` types do **not** receive a reference to
PPM. Instead, they communicate with the persistent processes exclusively over the
event bus, using the constant `PersistentProcessManager.PERSISTENT_QUEUE_NAME`
(`"buildplate_persistent"`).

```csharp
public sealed class PersistentProcessManager
{
    // --- Lifecycle ---

    // Prepares the persistent working directory, copies the server template,
    // writes eula.txt / server.properties, creates the world directory with
    // level.dat, and launches the Fabric server JVM. Idempotent — returns
    // immediately if the Fabric process is already running.
    Task StartFabricAsync();

    // Launches the single shared bridge bound to UDP 19132, pointing at the
    // Fabric server's internal port (25565), and loads the ViennaConnectorPlugin.
    // Registers an exit handler that triggers shutdown if the bridge crashes.
    Task StartBridgeAsync();

    // Graceful shutdown: stop the bridge first, then the Fabric server.
    Task StopAllAsync();

    // --- Health / readiness ---

    bool IsFabricRunning { get; }   // Fabric process exists and ExitCode is null
    bool IsBridgeRunning { get; }   // Bridge process exists and ExitCode is null

    // --- Constants (used by Instance / InstanceManager) ---

    // Event bus queue for communication with the persistent Fabric server:
    const string PERSISTENT_QUEUE_NAME = "buildplate_persistent";

    // Port on which the persistent Fabric server listens internally:
    const int PERSISTENT_FABRIC_PORT = 25565;
}
```

**Responsibilities:**

- **Fabric process** — launched once as `java -jar {fabricJarName} -nogui` in the
  persistent working directory. On v1 this per-instance work lived in
  `Instance.StartServerProcessAsync()`; in v2 it moves here and runs exactly once.
- **Bridge process** — launched once with a fixed `-port 19132`,
  `-serverAddress 127.0.0.1`, `-serverPort 25565`, and the connector plugin JAR/class.
- **Crash handling** — if either process exits unexpectedly, `PersistentProcessManager`
  logs the exit code and triggers `StopAllAsync()` (all instances are lost, so the
  orchestrator shuts down cleanly rather than limping on).
- **Persistent storage** — the Fabric server's world directory must live under the
  mounted volume so dimensions survive restarts.

---

## 10. generatorSettings Reference

The `generatorSettings` field of `CreateInstanceRequest` is a Gson `JsonObject` passed
to the `fountain:wrapper` chunk generator. Every key is optional and has a default.

| Key | Type | Default | Description |
|---|---|---|---|
| `buildplateWidth` | int | 32 | Total width of the buildplate in blocks (X axis) |
| `buildplateDepth` | int | 32 | Total depth of the buildplate in blocks (Z axis) |
| `buildplateGroundLevel` | int | 63 | Y-level of the ground surface |
| `buildplateUndergroundHeight` | int | 5 | Height of the underground (bedrock-to-surface) layer |
| `gameType` | int | 0 | 0 = survival, 1 = creative |
| `difficulty` | int | 1 | 0 = peaceful, 1 = easy, 2 = normal, 3 = hard |
| `dayTime` | long | 6000 | Initial day time in ticks (6000 = noon, 18000 = midnight) |
| `keepInventory` | boolean | true | Whether players keep inventory on death |
| `worldData` | string (nullable) | null | Base64-encoded zip of region files (`.mca`) to pre-populate the dimension |
| `dimensionKey` | string (nullable) | null | Explicitly requested dimension key (e.g. `"fountain:buildplate_abc"`). If null, the plugin auto-generates one. |

**Full example produced by the C# orchestrator:**

```json
{
  "buildplateWidth": 64,
  "buildplateDepth": 64,
  "buildplateGroundLevel": 63,
  "buildplateUndergroundHeight": 5,
  "gameType": 0,
  "difficulty": 1,
  "dayTime": 6000,
  "keepInventory": true,
  "worldData": null,
  "dimensionKey": null
}
```

**Mapping from `StartRequest` params to `generatorSettings`** (derived from the v1
`Instance.CreateLevelDat()` logic):

| v1 `level.dat` field | Setting key | Source value |
|---|---|---|
| `GameType` | `gameType` | `survival ? 0 : 1` |
| `Difficulty` | `difficulty` | `1` (fixed) |
| `DayTime` | `dayTime` | `night ? 18000 : 6000` |
| `GameRules.keepInventory` | `keepInventory` | `true` (fixed) |
| `GameRules.doDaylightCycle` / `doWeatherCycle` / `doMobSpawning` / `fountain:doMobDespawn` | — | always `"false"` |

---

## 11. Configuration

### BuildplateLauncher CLI options

Parsed by `CommandLine` in `Program.cs`. These options are provided by the launcher
wrapper / Docker entrypoint.

| Option | Required | Default | Description |
|---|---|---|---|
| `--eventbus` | no | `localhost:5532` | Event bus address (`host:port`) |
| `--publicAddress` | yes | — | Public server address to report in instance info |
| `--bridgeJar` | yes | — | Fountain bridge JAR file |
| `--serverTemplateDir` | yes | — | Fabric server template directory (Fabric JAR, mods, libraries, `.fabric/server`) |
| `--fabricJarName` | yes | — | Name of the Fabric JAR within the server template directory |
| `--connectorPluginJar` | yes | — | Fountain connector plugin JAR |
| `--dir` | no | `./staticdata` | Static data path |
| `--logger-url` | no | `null` | URL to stream logs to |

### Docker environment variables

Set in the container image and/or `docker-compose.yml`:

| Variable | Value | Purpose |
|---|---|---|
| `DOTNET_SYSTEM_NET_DISABLEIPV6` | `1` | Work around IPv6 issues |
| `COMPlus_gcServer` | `0` | Workstation GC (fewer threads) |
| `COMPlus_gcConcurrent` | `1` | Concurrent GC |
| `DOTNET_GCHeapHardLimit` | `536870912` (512 MB) | Cap the orchestrator heap |
| `ASPNETCORE_URLS` | `http://0.0.0.0:5000` | Public API endpoint |
| `SOLACE_LOG_CLIENT_REQUESTS` | `1` | Log client requests |

### Exposed ports (v2 — all shared)

| Port | Protocol | Service |
|---|---|---|
| `5000` | TCP | Public HTTP API (ASP.NET Core) |
| `1808` | TCP | Launcher UI / dashboard |
| `5532` | TCP | Event bus (orchestrator ↔ Java connector plugin) |
| `19132` | UDP | **Single** shared bridge Bedrock port |
| `25565` | TCP | Fabric internal port (local container only, **not** published) |

### Volume mounts

| Host | Container | Purpose |
|---|---|---|
| `/opt/apace-persistent/launcher-data` | `/app/launcher/Data` | Launcher data |
| `/opt/apace-persistent/config.json` | `/app/launcher/config.json` | Launcher configuration |
| `/opt/apace-persistent/launcher-logs` | `/app/launcher/logs` | Launcher logs |
| `/opt/apace-persistent/data` | `/app/data` | Application data |
| `/opt/apace-persistent/dataprotection-keys` | `/root/.aspnet/DataProtection-Keys` | ASP.NET data protection keys |
| `/opt/apace-persistent/resourcepacks` | `/app/staticdata/resourcepacks` | Bedrock resource packs |
| `/opt/apace-persistent/server-template-dir` | `/app/staticdata/server_template_dir` | Fabric server template |
| `/opt/apace-persistent/logs` | `/app/logs` | General logs |
| `/opt/apace-persistent/fabric-data` | `/app/launcher/persistent_fabric` | **Persistent Fabric worlds (v2)** |

### connectorPluginArg JSON

The bridge is started with `-connectorPluginArg {JSON}`. The plugin uses this to find
the event bus and its queue. In v1 this was built per instance
(`Instance.cs`); in v2 it is built once by `PersistentProcessManager`.

```json
{
  "EventBusAddress": "localhost:5532",
  "EventBusQueueName": "buildplate_persistent",
  "InventoryType": "SYNCED"
}
```

---

## 12. Differences from v1

### Removed (v1)

| Item | v1 implementation | v2 status |
|---|---|---|
| `Starter.FindPort()` | lock + scan from `BASE_PORT`/`SERVER_INTERNAL_BASE_PORT` | **Removed** — no per-instance ports |
| `Starter.CanBindPort()` | TCP+UDP bind probe | **Removed** |
| `Starter.ReleasePort()` | return port to pool on shutdown | **Removed** |
| `Starter.CreateInstanceBaseDir()` | temp dir per instance under `/tmp/vienna-buildplate-instance_{id}` | **Removed** — replaced by one persistent dir |
| `Instance.StartServerProcessAsync()` | spawn one Fabric JVM per instance | **Moved** to `PersistentProcessManager.StartFabricAsync()` (once) |
| `Instance.StartBridgeProcessAsync()` | spawn one bridge JVM per instance | **Moved** to `PersistentProcessManager.StartBridgeAsync()` (once) |
| Per-instance port mapping | `19132+n` / `25565+n` | **Removed** — `19132/udp` shared; single internal port |
| `Instance.SetupServerFiles()` | copy template + write `level.dat` per instance | **Replaced** by one-time persistent Fabric setup |
| `HOST_PLAYER_CONNECT_TIMEOUT` per instance | 120 s host-connect gate per process | **Replaced** by orchestrator-level lifecycle management |

### Added (v2)

| Item | Description |
|---|---|
| `PersistentProcessManager` | Owns the single shared Fabric + bridge processes (start/stop/health) |
| `createInstance` / `destroyInstance` event bus messages | Dimension lifecycle, replacing process spawn/kill |
| `bindPlayer` event bus message | Explicit player→instance routing, replacing implicit per-process login |
| Dimension tracking | `instanceId → { dimensionKey, state, players }` in `InstanceManager` |
| Persistent Fabric storage | `/app/launcher/persistent_fabric` (host: `/opt/apace-persistent/fabric-data`) |
| `bindPlayerToInstance()` returns numeric `dimensionId` | Bridge routing detail; transparent to C# |
| `PlayerLoginInfo.joinCode` | New v2 field for join-code-based instance selection |

---

## 13. Troubleshooting

| Symptom | Likely cause | Action |
|---|---|---|
| `Could not connect to event bus` at startup | Event bus server not running on `127.0.0.1:5532` | Start the event bus server first; verify the `--eventbus` option. The launcher exits with code 1. |
| Fabric process fails to start | Missing/damaged server template, missing Java 17, port `25565` in use | Check `/app/staticdata/server_template_dir` contents (fabric JAR, mods, libraries), the `--fabricJarName` value, and that the internal port is free. |
| Bridge can't bind `19132/udp` | Another process holds the UDP port (or a stale container) | `ss -ulpn` to find the holder; stop the conflicting process. The bridge will fail to bind `0.0.0.0:19132`. |
| `createInstance` returns `success:false` with `generator 'fountain:wrapper' not found` | Fabric mods not loaded, or the wrapper generator wasn't registered | Verify `fountain-generator` mod is in `mods/`; check Fabric's own log for registry errors. |
| `createInstance` throws `ConnectorPluginException` | Malformed request (missing required field) | Validate the `generatorSettings` JSON against §10 before sending. |
| `destroyInstance` returns `players still in instance` | Players not migrated/disconnected first | Enforce the precondition in §7: move all players out before destroying. |
| `bindPlayer` returns `instance not found: <id>` | Player routed to a destroyed/never-created instance | Check the orchestrator's `instances` map; the `instanceId` must match a prior successful `createInstance`. |
| `bindPlayer` returns `player already bound` | Duplicate login for the same UUID | Track player→instance bindings in C#; reject duplicate `playerConnected` for an already-bound UUID. |
| `bindPlayer` returns `instance not ready` | Dimension still loading chunks | Retry with backoff, or gate login on `state == "ready"` in `InstanceManager`. |
| Players appear in the wrong dimension | Bridge used hardcoded `dimensionId = 0` instead of `BindPlayerResult.dimensionId` | Confirm the bridge was rebuilt against the v2 login flow (§6). |
| Orchestrator OOM / high memory | One Fabric server now hosts all instances | Raise the `deploy.resources.limits.memory` in `docker-compose.yml` (default 4g in v2). |
| Instance data lost on restart | Fabric world dir not on the persistent volume | Verify `/opt/apace-persistent/fabric-data` is mounted at `/app/launcher/persistent_fabric`. |
| `saved` events ignored | Instance has `saveEnabled=false` | Expected for `PLAY` / `SHARED_*` / `ENCOUNTER` types — only `BUILD` persists. |

---

## Appendix — Versions (frozen in `interface-freeze.md` §3)

| Component | Version |
|---|---|
| Minecraft (Java Edition) | 1.20.4 |
| Fabric Loader | 0.15.x (tested up to 0.15.6) |
| Fabric API | 0.91.1+1.20.4 (tested up to 0.94.1) |
| Yarn mappings | 1.20.4+build.1:v2 |
| Java (Fabric & bridge) | 17 |
| mcprotocollib | 1.20.4-2-20240116.220521-7 |
| Cloudburst Bedrock Protocol | 3.0.0.Genoa-SNAPSHOT |
| Bedrock codec (Genoa) | v425 |
| connector-plugin-base | 0.0.1-SNAPSHOT |
| level.dat DataVersion | 3700 |
| level.dat Version Id | 19133 |
