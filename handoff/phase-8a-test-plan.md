# Phase 8-A — Meter & escalation state sync + alloy hop (test plan)

**Project:** VoidCrewTerminus
**Phase:** 8-A — host-authoritative sync of `DifficultyScalar`, `BossesDefeated`, `Meter`, `Level` + client→host alloy spend.
**Status:** Code complete. **Requires 2-client verification** — not done until convergence is seen in BOTH logs.
**Date:** 2026-07-17

## What changed

- **Host owns the state.** `ForgeSectorHook` (meter + scalar) and `BossDefeatHook` (bosses) now act **only on the master client** and call `ForgeNetSync.BroadcastState()`; non-host clients skip the increment and apply what the host sends. Dev setters (`!setdifficulty/!setbosses/!setmeter/!setforgelevel`) and the per-run reset also broadcast from the host.
- **Clients are pure receivers** — `ForgeStateSyncMessage.Handle` → `ApplyNetworkState`/`ApplyNetworkBosses` (silent; fires `LevelChanged` so tube visibility updates).
- **Alloy hop** — a client feeding the alloy terminal sends `AlloySpendRequestMessage` to the host, which spends against its authoritative supplies and broadcasts the new meter/level back.
- **Late joiner** — host pushes a targeted snapshot on `OnPlayerEnteredRoom`.
- **Host migration** — new master re-asserts state on `OnMasterClientSwitched` (authority is live-derived from `IsMasterClient`, so its hooks just resume).
- **Solo unchanged** — not-in-room counts as authority; broadcasts no-op with no peers.

## Definition of done

**2-client convergence, seen in BOTH captured logs.** Every sync logs paired lines:
- host: `[Net] → sent forge state {scalar=…, bosses=…, meter=…, level=…} to all.`
- client: `[Net] ← applied forge state {…}.`

A test passes only when the client's `← applied` line shows the same value the host `→ sent`. A host `→ sent` with no matching client `← applied` = discovery/routing broken (the whole point of logging both sides).

## Setup

- **Two instances**, both with the latest `VoidCrewTerminus.dll`, `EnableDevMode = true`. One hosts a **"Pilgrimage"** run, the other joins.
- Capture `BepInEx/LogOutput.log` from both.

---

### T1 — Counters converge (scalar + bosses)

1. Host: `!setbosses 2` then `!setdifficulty 5`.
2. Host log: `[Net] → sent forge state {scalar=5, bosses=2, …}`.
3. **Client log:** `[Net] ← applied forge state {scalar=5, bosses=2, …}`.
4. Client: `!difficulty` → shows `DifficultyScalar=5, BossesDefeated=2` (matches host).

**Pass:** client `← applied` matches host `→ sent`; client `!difficulty` agrees.

---

### T2 — Live sector/boss progression converges

1. Both in the same run. Host completes sectors / defeats bosses normally (no dev commands).
2. Each host-side award logs `→ sent`; each client logs a matching `← applied` with the incremented value.
3. **Determinism check:** after a sector where the scalar changed, `!lootdump` on host and client should report the **same ceiling/tier counts** (the client reshapes with the synced scalar). If they differ, note whether the state broadcast lagged the sector's loot setup.

**Pass:** client counters track the host live; loot reshape matches across both.

---

### T3 — Meter/Level converge + client alloy feed

1. Host: `!setforgelevel 3` → client `← applied … level=3`; client `!forgemeter` shows L3 (capacity/tubes updated via `LevelChanged`).
2. **Client** operates the alloy terminal. Client log: `[Net] → sent alloy-spend request to host.` Host log: `[Net] ← alloy-spend request from #N: spent` → `[Net] → sent forge state {…}` → client `← applied` with the raised meter/level.
3. Confirm the client could **not** spend before (pre-8-A it printed "Only the host can feed the Forge"); now it works via the host.

**Pass:** level syncs and updates client tube visibility; a client can feed the forge and the resulting meter/level comes back.

---

### T4 — Late joiner snapshot

1. Host advances state (`!setbosses 2`, `!setdifficulty 6`, `!setforgelevel 4`) **before** the second player joins.
2. Second player joins. Host log: `[Net] → sent forge state {…} to joiner #N`. Joiner log: `[Net] ← applied forge state {scalar=6, bosses=2, level=4}`.
3. Joiner `!difficulty` / `!forgemeter` immediately match the host — no need to wait for the next sector.

**Pass:** a mid-run joiner catches up to current state on join.

---

### T5 — Host migration doesn't freeze escalation

1. 2 clients in a run with escalation active. **Host leaves** (close host instance).
2. Surviving client becomes master. Its log: `[Net] Became master client — asserting forge-state authority.`
3. That client now completes a sector / defeats a boss → its log shows the host-side award (`[Forge] … Meter +…`, `[Escalation] Boss defeated …`) and a `→ sent` (harmless with no peers, or to any remaining joiners).

**Pass:** escalation keeps advancing under the new master — it does not silently freeze.

---

## Known limitations (do NOT fail QA on these)

1. **Notifications are host-side** — the "Forge Meter +20" / "Escalation is now active" popups show on the host only; clients get the state (incl. level) but not the toast. Client-side notifications can come later.
2. **Loot-reshape determinism depends on the state broadcast arriving before the next sector's loot setup.** Reliable ordering makes this very likely, but if T2's `!lootdump` ever diverges, this is the cause — revisit with explicit sequencing.
3. **8-B/8-C not included** — cursed markers and commit authority are still host-only; a client's `!cursedstatus` still warns, and off-host commits still log the divergence warning.
