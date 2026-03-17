# Miningcore

<img src="https://raw.githubusercontent.com/wombatlabs/miningcore/674eaa74a2c9486b5c4fc65a7694b41a1186f383/banner.png" width="2048">

---

Miningcore is a modern technical successor to the original  
[Miningcore](https://github.com/coinfoundry/miningcore).

It preserves Miningcore's proven stability while introducing a fully refreshed architecture:

- Live/Real-time in-memory metrics (pool, address, worker)
  Zero-DB live state for ultra-fast dashboards and monitoring.

- Next-generation Live API surface
  Clean, structured endpoints designed for modern frontends (Next.js, mobile apps, dashboards).

- Algorithm improvements & fixes
  Deep work on problematic algorithms (Equihash, YCash, KawPoW, etc.) to improve stability, speed and correctness.

- Worker-aware tracking
  Stats per address.worker, including accepted, rejected, stale and session activity.

- Legacy cleanup and modernization
  Refactoring old code paths (Stratum, VarDiff, share accounting, job managers) for performance and maintainability.

- New coin integrations
  Adding support for additional PoW coins and fixing broken or outdated templates.

- More predictable hashrate & smarter windows
  Hybrid hashrate computation (session-based + rolling window).

- New developer-friendly structure

This is not "yet another fork".  
It is a **technical continuation** and a reboot of the Miningcore engine for 2025 and beyond.

It keeps the battle-tested core of Miningcore, but pushes the system years ahead.

> Status: early 1.x line - experimental, under active development.

---

## Why Miningcore Exists

Miningcore remains one of the most widely used pool engines, but:

- many parts of the codebase are legacy (2017-2020 era)
- Stratum + job pipeline is hard to extend
- metrics depend too heavily on the database
- per-worker stats are nearly nonexistent
- Equihash, KawPoW, etc code paths required critical fixes
- dashboards need fast, DB-free, modern endpoints
- scaling beyond a few hundred miners becomes difficult

Miningcore directly addresses these pain points with a cleaner, faster, more predictable architecture while keeping full compatibility with existing pool configs and workflows.

---

## Features

Everything you expect from Miningcore **plus** Miningcore additions.

### Core (from Miningcore)

- Cluster support (multiple pools, independent coins)
- Ultra-low-latency async Stratum server (multi-threaded)
- Adaptive difficulty (vardiff)
- Native hashing implementations (maximum PoW speed)
- Session management for zombie workers / DDOS protection
- Automated payment processing
- Banning system
- REST API on port 4000
- WebSocket streaming (blocks, payouts, etc.)
- PoW & PoS support
- Full logging system per pool
- Linux & Windows (Linux recommended for production)

### Miningcore Enhancements

#### **1. Live Hashrate Engine (In-Memory)**
High-performance rolling diff tracking:

- per **pool**
- per **address**
- per **address.worker**

Built with:

- lock-free sharded maps  
- 2048-slot 1-second rings  
- TTL eviction  
- hybrid hashrate (session + window)  
- smoother short-window stabilization  

This fixes the classic Miningcore problem: *low hashrate in the first N minutes*, problem present in all Miningcore forks.

#### **2. Session Share Stats (address.worker)**  
In-memory counters that track each worker session:

- accepted shares  
- rejected shares  
- stale shares  
- firstSeen / lastSeen timestamps  
- live online state  
- session duration 
- zero DB overhead

Stored in-memory (no DB cost), resets automatically on disconnect or TTL expiry.

#### **3. New Next-Gen Live API Layer**  
Optimized HTTP endpoints requiring **zero DB queries** for most UI use cases.

Ideal for:

- Next.js apps  
- React/Vue dashboards  
- Mobile apps  
- High-frequency polling environments  
- High-traffic deployment 

See `live-api.md` for details.

#### **4. Algorithm & Job Manager Improvements**

- Refactored Equihash job pipeline
- Equihash cleanup  
- Overwinter/Sapling handling fixed 
- Fixes in payout handlers
- KawPoW / Equihash efficiency improvements  
- Rebuilt native libs  
- Serialization performance optimization  
- Partial refactor of BitcoinJob & EquihashJob pipelines  
- Significantly better stability under load  
- Faster native hashing libs 

#### **5. Codebase Cleanup & Modernization**

- Legacy fixes throughout Stratum path  
- Optimized Stratum -> JobManager -> ShareRecorder flow 
- Improved ShareRecorder performance  
- Reduced locking in hot paths  
- More stable low-latency behavior  
- More deterministic behavior under high load  
- Line-by-line refactors in job managers  
- New structure for future improvements  
- More robust worker state transitions 


---

## 📘 Documentation  

- **live-api.md** — Live API reference  
- **ROADMAP.md** — planned improvements  
- **CONTRIBUTING.md** — contribution rules  

---

## Support

There is **no guaranteed commercial support** for Miningcore.

- Use at your own risk.
- Issues and PRs are welcome.
- For configuration examples and general pool operations, you can still refer to the original Miningcore documentation and adapt it to this fork.

---

## Contributions

Contributions are welcome.

- Default stable branch: `production`
- Active development branch: `dev`

See `CONTRIBUTING.md` for guidelines (branching, PR target, coding style).

---

## 📦 Installation, Build, Running  
*(Same as Miningcore - updated instructions coming soon)*

---

## Running a Production Pool

Running Miningcore in production requires more than just the backend binary.

### Linux Only (Production)
Linux is the only recommended production environment.  
Windows is fine for development and debugging, but **do not** deploy a public pool on it.

### You Still Need a Frontend
Miningcore is **the backend engine only**.  
To expose a public mining pool, you must pair it with a frontend that consumes both:

- the **classic Stats API** (blocks, payments, balances)
- the **Miningcore Live API** (real-time metrics: hashrate, miners, workers, rounds)

### Recommended Frontend (Official)
For best results, use the official Miningcore frontend:

**Miningcore-Pool-Website**  
Next.js production-ready dashboard built specifically for this engine.  
https://github.com/wombatlabs/Miningcore-Pool-Website

It includes:

- live dashboard powered by the new API surface  
- pool cards / algo stats / cluster overview  
- miner explorer  
- worker breakdown  
- caching tuned for Miningcore's metrics  
- clean responsive UI  
- extremely low latency thanks to static-lite + live-lite endpoints  

If you don't want to reinvent a pool website from scratch, this is the easiest and safest option.

### Security Requirements

**1. Secure your coin daemons**
- Run daemons on private network segments when possible  
- Disable any public RPC exposure  
- Use firewall rules (UFW/iptables)

**2. Secure your database**
- PostgreSQL must be accessible **only** to the pool backend host  
- Create a dedicated DB user with a strong password  
- Tune connection limits and monitoring

**3. Use a reverse proxy**
Recommended options:

- nginx  
- Caddy  
- Traefik  

These provide:
- TLS termination  
- rate-limiting  
- request normalization  
- cache headers for live endpoints  

### Monitoring

Running a pool blindly is how you get wrecked.

Monitor at minimum:

- Miningcore service uptime, CPU, memory  
- PostgreSQL (connections, slow queries, disk I/O, autovacuum)  
- Stratum port health  
- Hashrate anomalies (flatlines, spikes)
- Worker churn (disconnect storms)

### Typical Production Architecture

[Miners] → Stratum Ports
↓
[Miningcore Engine]
↓
PostgreSQL Database
↓
[Frontend / Dashboard]
↓
Reverse Proxy (TLS, caching)

A realistic production deployment includes:

- **1× Miningcore instance** (or more behind LB)  
- **1× PostgreSQL instance** (local or managed, e.g. AWS RDS)  
- **1× Reverse proxy** (nginx / Caddy / Traefik)  
- **1× Web frontend** (Next.js recommended)

Miningcore provides the backend logic.  
**Miningcore-Pool-Website** gives you a full production-grade UI immediately.

Running a public pool means you are expected to understand everything above.  
This is *not* a plug-and-play toy — it's real infrastructure.

---

## Credits

Miningcore stands on the shoulders of:

- Coinfoundry Miningcore (original project)
- All community forks and contributors over the years

Without that base, this project would not exist.

---

## Donations

If you wish to support Miningcore development:

Sponsorship: (GitHub Sponsors coming soon)

* ETH:  `TO ADD`
* BTC:  `TO ADD`
