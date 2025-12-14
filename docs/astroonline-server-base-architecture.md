# AstroOnline — Server Base Architecture

This document describes the **baseline authoritative server architecture** for AstroOnline as it exists now. It is intended as a **resume point** so development can continue in a future session without re-deriving fundamentals.

---

## 1. High-Level Goals

- Headless, authoritative MMORPG server
- Deterministic fixed-tick simulation
- No Unity dependencies on the server
- Explicit protocol (no magic networking frameworks)
- Clear separation of **host / core logic / networking**

---

## 2. Repository Layout

```
AstroOnline/
  server/
    AstroOnline.Server.sln
    AstroOnline.Server.Host/
    AstroOnline.Server.Core/
    AstroOnline.Server.Net/

  client/
    AstroOnline.Client/   (Unity 6 project)

  docs/
```

The server and client live in the **same Git repository** but are cleanly separated.

---

## 3. Server Projects & Responsibilities

### 3.1 AstroOnline.Server.Host

**Purpose:**
- Process lifetime
- Fixed tick loop
- Startup / shutdown
- Wiring Core + Net

**Key characteristics:**
- Console application
- Headless
- Owns the authoritative tick
- Never mutates game state directly

---

### 3.2 AstroOnline.Server.Core

**Purpose:**
- All deterministic game logic
- World state
- Commands applied during ticks

**Rules:**
- No sockets
- No threads
- No timers
- No Unity types

#### Core Types

- `ServerWorld`
  - Owns `WorldState`
  - Has an inbound command queue
  - `Update()` is called exactly once per tick

- `WorldState`
  - Holds authoritative simulation data
  - Mutated only during `ServerWorld.Update()`

- `IWorldCommand`
  - Interface for all mutations
  - Enqueued by Host, applied by Core

This guarantees **deterministic, auditable simulation**.

---

### 3.3 AstroOnline.Server.Net

**Purpose:**
- UDP transport
- Packet parsing
- Protocol validation
- Inbound/outbound queues

**Rules:**
- No game logic
- No world mutation
- Only produces data for Host to interpret

#### Key Components

- `UdpNetServer`
  - Wraps `UdpClient`
  - Background receive loop
  - Thread-safe inbound queue
  - Explicit `SendAsync()` for outbound packets

- `ClientRegistry`
  - Maps `IPEndPoint → ClientId`
  - Issues unique `ulong ClientId`
  - Tracks last-seen timestamps

---

## 4. Fixed Tick Model

- Tick rate: **20 Hz**
- Tick loop owned by `Server.Host`
- Pattern:

```
Drain inbound packets
→ Decode / validate
→ Enqueue commands
→ world.Update()
→ (optional logging)
```

Rules:
- One world update per tick
- No mid-tick mutation
- No async code inside Core

---

## 5. Network Protocol

### 5.1 Packet Header (8 bytes)

```
0..1  Magic       0xA0 0x01
2     Version     0x01
3     Type
4..7  PayloadLen  uint32 (LE)
```

All packets must match the declared payload length exactly.

---

### 5.2 Packet Types (Current)

| Type | Name    | Direction | Payload |
|-----:|---------|-----------|---------|
| 1    | Ping    | Client → Server | uint32 clientTimeMs |
| 2    | Pong    | Server → Client | uint32 echoed clientTimeMs |
| 10   | Connect | Client → Server | none |
| 11   | Accept  | Server → Client | uint64 clientId |

---

## 6. Client Handshake Flow

```
Client → Connect
Server → Accept (ClientId)
Client → Ping
Server → Pong
```

Clients are identified by **ClientId**, not endpoint alone.

---

## 7. Current Status

At this point:
- Server boots cleanly
- Unity client connects
- ClientId is issued
- Ping/Pong RTT is measured
- End-to-end pipeline is verified

This is the **locked foundation** for future gameplay work.
