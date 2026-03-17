# Miningcore Roadmap

This document outlines the planned direction of Miningcore.  
The project is an ongoing evolution of Miningcore with modern live metrics,
algorithm fixes, improved job managers, and a cleaner, more scalable architecture.

---

## Phase 1 - Core Refactor (Current)

### Live Metrics Engine
- In-memory rolling windows  
- Session tracking per worker  
- Live-only API layer  

### New Live API (LITE + HEAVY)
- `/static-lite`, `/status-lite`, `/miners-lite`  
- SSE feed for pool hashrate  
- Zero-DB UI endpoints  

### Job Manager Fixes
- Equihash cleanup  
- Overwinter/Sapling handling correctness  
- KawPoW / Equihash performance stabilization  

### Backend Cleanup
- Removed legacy bottlenecks  
- Safer share pipeline  
- Cache improvements  

**Status: Mostly complete**

---

## Phase 2 - Modernization (Active Development)

### 1. **Full Job Manager Modularization**
Goal: make job managers pluggable and composable.

- isolate Equihash, ProgPow/KawPoW, RandomX logic  
- unified share-validation pathway  
- cleaner serialization layer  

### 2. **Live Engine 2.0**
- probabilistic smoothing to stabilize miner hashrate  
- multi-window aggregation (1s, 10s, 30s, 5m, 10m)  
- spike detection (bad miners, botnets)  

### 3. **Database Optimizations**
- reduce write pressure  
- optional share batching  
- optional DB-free mode for solo miners  

### 4. **API Stability Pass**
- finalize live API shapes  
- versioned endpoints (`/api/v1/...`)  

---

## Phase 3 - New Features (Planned)

### 1. **Full Coin Template Rewrite**
- clean multi-coin handling  
- shared UTXO pipeline  
- fix broken / outdated coin templates  
- easier integration for Equihash variants  

### 2. **New Algorithms Support**
Tentative list:

- FiroPoW  
- NheavyHash family  
- Karlsen-hash  
- Custom forks on request  

### 3. **Multi-Instance Clustering**
Allow multiple Miningcore backends to sync:

- load-balanced Stratum  
- distributed miner state  
- shared live metrics via pub/sub  

### 4. **Web UI Integration Helpers**
- first-party API SDK (TypeScript)  
- ready-made endpoints for dashboards  
- integration examples for:
  - Next.js
  - React Native
  - Vue

---

## Phase 4 - Performance & Reliability

### 1. **Async Stratum Rebuild**
- reduce allocations in message parsing  
- per-message pool selection for merged mining  
- new GPU-heavy job scheduler  

### 2. **Share Pipeline 2.0**
- lock-free share dispatch  
- batch inserts  
- simplified share result objects  

### 3. **Hot Path Telemetry**
Telemetry for:

- share latency  
- per-address behaviour  
- stale/reject anomalies  

---

## Phase 5 - Security & Hardening

- RPC sanitization  
- portable sandbox for coin daemons  
- traffic rate limiting  
- anti-wildcard botnet protection  
- Stratum heartbeat enforcement  

---

## Phase 6 - Developer Experience

- improved logs + structured logging  
- dev-mode UI for job manager debugging  
- optional metrics export (Prometheus)  
- architecture diagrams  
- full documentation rewrite  

---

## Long-Term Vision

Miningcore aims to become:

- the **most modern** Miningcore continuation  
- fully modular  
- scalable to thousands of miners  
- predictable and stable under heavy load  
- flexible for new coins and algorithms  
- paired with a production-grade open-source frontend

This is not a short-term fork — it's a multi-year evolution.

Contributions are welcome.  
See `CONTRIBUTING.md` for how to get involved.
