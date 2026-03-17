# Miningcore Live API

Miningcore exposes two API layers:

1. **Classic Stats API** (inherited from Miningcore)  
2. **Miningcore Live API** (new, in-memory, low-latency)

This page documents the **Miningcore Live API**, mounted at:

/api/live/...

The Live API uses in-memory structures (`LiveHashrateState`, `LiveRoundState`) to:

- avoid unnecessary database queries  
- allow aggressive polling (1-10 s)  
- provide consistent metrics per pool, per address, and per address.worker

---

## 1. Classic Stats API (Miningcore-compatible)

The classic API (usually on port `4000`) remains available for:

- pool list  
- basic pool stats  
- blocks  
- payments  
- balances  

Original documentation:  
https://github.com/oliverw/miningcore/wiki/API

Miningcore **does not remove** these endpoints to maintain compatibility.

---

## 2. Live API - Overview

Endpoint categories:

- **HEAVY** - LIVE + DB (network difficulty, pendingShares, blockHeight)  
- **LITE** - LIVE-only (zero DB; best performance)  
- **SSE** - Server-Sent Events (continuous streaming)

Common parameters:

- `windowSec` - hashrate calculation window (default: 600s)  
- `limit` - maximum results (default: 100, max: 500)  
- `page` / `pageSize` - pagination  

---

## 3. Endpoint Index

### HEAVY (LIVE + DB)

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/live/pools/snapshot` | Full live + network snapshot for all pools |
| GET | `/api/live/pools/{poolId}/snapshot` | Full snapshot for a single pool |
| GET | `/api/live/pools/snapinfo` | Compact SnapInfo for all pools |
| GET | `/api/live/pools/{poolId}/snapinfo` | Detailed SnapInfo for one pool |
| GET | `/api/live/status` | Cluster status (hashrate, miners, network) |
| GET | `/api/live/pools/{poolId}/round` | Current round state |
| GET | `/api/live/pools/{poolId}/miners` | Top miners + pendingShares (DB) |
| GET | `/api/live/pools/{poolId}/miners-all` | All miners, paginated, with pendingShares |
| GET | `/api/live/pools/{poolId}/miners/{address}/snapshot` | Live snapshot for a miner |

---

### LITE (LIVE-only)

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/live/pools/static-lite` | Static pool configuration |
| GET | `/api/live/status-lite` | Cluster live-only status |
| GET | `/api/live/pools/snapshot-lite` | Live-only snapshot of all pools |
| GET | `/api/live/pools/{poolId}/snapshot-lite` | Live-only snapshot of a single pool |
| GET | `/api/live/pools/{poolId}/online-lite` | Online miners/workers in a pool |
| GET | `/api/live/pools/online-lite` | Online miners/workers in the cluster |
| GET | `/api/live/miners/search-lite` | Global address search |
| GET | `/api/live/pools/{poolId}/top-miners-lite` | Live-only top miners |
| GET | `/api/live/pools/{poolId}/miners-lite` | Limited miner list (live-only) |
| GET | `/api/live/pools/{poolId}/miners-all-lite` | All miners, live-only (paginated) |
| GET | `/api/live/pools/{poolId}/miners/{address}/round-lite` | Round metrics for a miner |
| GET | `/api/live/pools/{poolId}/miners/{address}/workers-lite` | Workers belonging to an address |

---

### SSE

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/live/pools/{poolId}/feed` | Continuous SSE hashrate stream |

---

## 4. HEAVY Endpoints (LIVE + DB)

### 4.1 Snapshots

#### `GET /api/live/pools/snapshot`
Full snapshot for all pools.

Includes:
- live: `currentHashrate`, `sharesPerSec`, `minersOnline`, `round.actualShares`  
- DB: `network.height`, `network.difficulty`, `network.hashrate`

Query:
- `windowSec?`

---

#### `GET /api/live/pools/{poolId}/snapshot`
Same structure for a single pool.

---

#### `GET /api/live/pools/snapinfo`
Compact SnapInfo for all pools:

Includes:
- coin metadata  
- pool static config  
- live metrics  
- network stats  
- round state  

---

#### `GET /api/live/pools/{poolId}/snapinfo`
Single-pool version.

---

#### `GET /api/live/status`
Cluster status (LIVE + DB):

- `poolId`
- `algo`
- `unit`
- `currentHashrate`
- `minersOnline`
- `difficulty` / `blockHeight` (DB-backed)

---

### 4.2 Round

#### `GET /api/live/pools/{poolId}/round`
Current round state:

- `height`
- `startedAt`
- `actualShares` (live)
- `expectedShares` (from difficulty)
- `luckPercent`

---

### 4.3 Miners (DB-backed)

#### `GET /api/live/pools/{poolId}/miners`
Top miners with `pendingShares`.

Query:
- `windowSec`
- `limit`

---

#### `GET /api/live/pools/{poolId}/miners-all`
All miners, with pagination.

Query:
- `windowSec`
- `page`
- `pageSize`

---

#### `GET /api/live/pools/{poolId}/miners/{address}/snapshot`
Live-only snapshot for a miner:

- `hashrate`
- `sharesPerSec`
- `online`
- `lastShareAt`
- `unit`
- `windowSec`

---

## 5. LITE Endpoints (LIVE-only)

### 5.1 Cluster & Pools

#### `GET /api/live/pools/static-lite`
Static configuration for UI.

---

#### `GET /api/live/status-lite`
Live-only cluster status.

---

#### `GET /api/live/pools/snapshot-lite`
Live-only snapshot for all pools.

---

#### `GET /api/live/pools/{poolId}/snapshot-lite`
Live-only snapshot for a single pool.

---

### 5.2 Online Counters

#### `GET /api/live/pools/{poolId}/online-lite`
Online miners/workers.

Query:
- `mode=window|live`
- `windowSec?`

---

#### `GET /api/live/pools/online-lite`
Cluster-wide version.

---

### 5.3 Miners (address-level)

#### `GET /api/live/miners/search-lite`
Global address search.

Query:
- `q`
- `limit`
- `windowSec`

---

#### `GET /api/live/pools/{poolId}/top-miners-lite`
Live-only top miners.

---

#### `GET /api/live/pools/{poolId}/miners-lite`
Limited miner list (live-only).

---

#### `GET /api/live/pools/{poolId}/miners-all-lite`
All miners live-only, paginated.

---

#### `GET /api/live/pools/{poolId}/miners/{address}/round-lite`
Round metrics + miner view.

---

### 5.4 Workers

#### `GET /api/live/pools/{poolId}/miners/{address}/workers-lite`
Workers under an address:

- `worker`
- `hashrate`
- `sharesPerSecond`
- `online`
- `lastShareAt`

---

## 6. SSE

### `GET /api/live/pools/{poolId}/feed`

Continuous stream:

- `poolId`
- `asOf`
- `currentHashrate`
- `windowSec`
- `unit`

Query:
- `intervalSec`
- `windowSec?`

Ideal for real-time charts.

---

## 7. Best Practices

- Use **LITE** for dashboards -> faster, zero DB  
- Use **HEAVY** only when `pendingShares` or network stats are needed  
- For charts -> always use **SSE**, never polling  

---
