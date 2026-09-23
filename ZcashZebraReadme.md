# Mining Zcash (NU6.3) with Miningcore and Zebra

Zcash mainnet runs NU6.3 "Ironwood" (activated 2026-07-28). `zcashd` is end-of-life and does not
support NU6.3, so a Zcash pool must run against **Zebra** (the Rust node). Zebra has no wallet, so
Miningcore uses the coinbase transaction Zebra builds (rather than constructing its own) and pays
miners through a **separate transparent wallet daemon**.

This note covers the operator setup. The code side is enabled automatically for the built-in `zcash`
coin (`useNodeCoinbaseTx` is set on its networks in `coins.json`).

## How it works

- Miningcore polls Zebra's `getblocktemplate` and uses the returned `coinbasetxn` **verbatim**. That
  coinbase pays the address configured in Zebra (`mining.miner_address`). Miningcore does not rebuild
  it, because rebuilding would invalidate the block-commitments hash Zebra precomputes.
- Because the coinbase is fixed by the node, **the pool address and Zebra's `mining.miner_address`
  must be the same transparent (t1) address**, and the pool takes no coinbase-level fee (the whole
  miner reward plus fees goes to that address; the pool distributes from it).
- Found blocks are classified with `getrawtransaction` (Zebra has no wallet `gettransaction`).
- Payouts are transparent `sendmany` calls sent to a separate **wallet daemon** that holds the private
  key for the pool address.

## Prerequisites

1. **Zebra** v6.3.0 or later, fully synced.
2. A **transparent Zcash wallet** exposing a Bitcoin-compatible JSON-RPC (`sendmany`,
   `walletpassphrase`, `walletlock`, `validateaddress`) that holds the private key for the pool's
   t1 address. Any Bitcoin-RPC-compatible transparent Zcash wallet works; this is the operator's
   choice. Shielded (z-address) payouts are not supported in this mode.
3. The pool's payout t1 address, used in three places that must all match:
   - Zebra `mining.miner_address`
   - Miningcore pool `address`
   - the address whose key the wallet daemon controls

## 1. Configure Zebra (`zebrad.toml`)

```toml
[network]
network = "Mainnet"          # or "Testnet"

[mining]
miner_address = "t1YourPoolTransparentAddress"

[rpc]
listen_addr = "127.0.0.1:8232"
enable_cookie_auth = false    # Miningcore uses user/password (or none) rather than cookie auth
```

Notes:
- Use a **transparent (t1)** `miner_address`. A shielded miner address produces shielded coinbase
  outputs the pool cannot account for, and Miningcore will reject such a template.
- If you set RPC credentials, mirror them in the Miningcore daemon entry below.

## 2. Configure the wallet daemon

Run your transparent wallet daemon so it:
- holds the private key for the same t1 address as `mining.miner_address`, and
- serves JSON-RPC on its own host/port.

If the wallet is encrypted, set `walletPassword` in the pool's payment processing config (below).

## 3. Configure the Miningcore pool

Key points (see `examples/zcash_pool.json` for a full file):

```json
{
  "id": "zec1",
  "coin": "zcash",
  "address": "t1YourPoolTransparentAddress",
  "blockRefreshInterval": 500,
  "daemons": [
    {
      "host": "127.0.0.1",
      "port": 8232,
      "user": "user",
      "password": "pass"
    },
    {
      "host": "127.0.0.1",
      "port": 8233,
      "user": "user",
      "password": "pass",
      "category": "wallet"
    }
  ],
  "paymentProcessing": {
    "enabled": true,
    "minimumPayment": 0.01,
    "payoutScheme": "PPLNS",
    "walletPassword": ""
  }
}
```

- **Daemon order matters.** The first daemon (no `category`) is the Zebra node, used for templates,
  block submission and block classification. The daemon with `"category": "wallet"` is the wallet,
  used only for payouts. List the node first.
- **`blockRefreshInterval` is required.** Zebra has no ZMQ block notifications, so Miningcore polls.
  500-1000 ms is a good range.
- **No `z-address` is required** in this mode (it is only used by the shielded payout path).
- Set `paymentProcessing.walletPassword` if the wallet daemon is encrypted. Set
  `minersPayTxFees: true` under paymentProcessing extra if miners should absorb the transaction fee.

## 4. Verify (testnet first)

NU6.3 is already active on testnet (height 4,134,000). Against a testnet Zebra:

1. Confirm the pool produces jobs and accepts shares.
2. Mine and **submit a real block**; confirm Zebra accepts it (`getblock <hash>` returns it). If
   Miningcore logs a "merkle root mismatch" at job creation, the coinbase handling is wrong - stop and
   investigate before mainnet.
3. Let a found block reach maturity and confirm it is classified `confirmed` (not stuck pending).
4. Trigger a payout and confirm the wallet daemon broadcasts a transparent `sendmany` transaction.

## Constraints and gotchas

- Transparent (t1) addresses only, end to end. No shielded payouts in this mode.
- The three addresses in the Prerequisites must be identical; a mismatch means the pool credits itself
  for a reward paid elsewhere. Miningcore asserts the node coinbase pays the pool address and refuses
  the job otherwise.
- The pool takes no fee via the coinbase. Any pool fee must be handled through payout accounting.
- Coinbase maturity applies before a block can be paid out (default from the coin template; override
  with `minimumConfirmations` on the daemon endpoint if needed).
