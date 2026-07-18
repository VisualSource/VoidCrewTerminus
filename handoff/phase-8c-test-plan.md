# Phase 8-C — Authoritative commit + overlay sync (test plan)

**Project:** VoidCrewTerminus
**Phase:** 8-C — host-authoritative Upgrade Forge commit; overlay snapshot for late joiners.
**Status:** Code complete. **Requires 2-client verification.**
**Date:** 2026-07-17

## What changed

- **Any client can commit; the host resolves it.** A client's `DoCommit` sends `CommitRequestMessage {boxViewID, relicViewIDs}` to the host (it no longer computes locally). The host resolves the **box by ViewID** and the relics by ViewID, re-derives tier (from name) and cursed (from its own markers) — **never trusting client-reported values** — rolls perk + burden once, persists, and broadcasts.
- **Full-snapshot result.** `CommitResultMessage {boxViewID, level, perkSlots[], burdens[], relicsConsumed}` — every client **overwrites** its snapshot for the box (no delta drift). The **operator** (the client holding the relics) also consumes them (it owns them, so the networked destroy propagates); non-operators no-op.
- **Solo/host inline unchanged.** On the authority `DoCommit` runs `TryCommit` → `ComputeAndPersist(_moduleBox, _relics)` + consume, exactly as before; the broadcast no-ops with no peers.
- **`ComputeAndPersist` is static (box-based)** because the host's own forge instance has no `_moduleBox` when a client docked (docking is a local interaction) — so the host computes from the box object, not an instance.
- **Late-joiner overlay** — on join the host pushes every box snapshot (`ForgeStateStore.AllSnapshots`) so the joiner's modules reconstruct with the right level/perks/burdens.

## Definition of done

2-client convergence in BOTH logs. Key paired lines:
- client operator: `[Net] → sent commit request box=N (K relics) to host.`
- host: `[Net] ← commit request from #A box=N (K/K relics resolved).` → `[Forge] Committed L…→L…` (+ the 7-A/7-C causal lines) → `[Net] → sent commit result box=N L… to all.`
- other clients: `[Net] ← applied commit result box=N L….`

Plus: the module, once rebuilt, shows the **same** level/perks/burden on host and client.

## Setup

Two instances, latest dll, `EnableDevMode = true`, host a "Pilgrimage" run + join. `PerkRollChanceRare = 1.0` to force a perk. Capture both logs.

---

### T1 — Client-operated commit converges

1. On the **client**: `!forgespawn`, dock a deconstructed Mk III box, insert relics, click **Commit**.
2. Client log: `→ sent commit request box=N …`. Host log: `← commit request … resolved` → `[Forge] Committed …` + `[Forge] Perk: SIGNATURE/POOL …` → `→ sent commit result box=N L…`.
3. Client (operator) log: `← applied` is skipped on the operator? No — the operator is a client, so it **does** get `← applied commit result box=N` and the `Upgrade committed by the host (consumed K relics)` notification; its relics vanish.
4. **Converge check:** rebuild the module. Host and client both show the **same** resulting level + perk (compare `!perks` / the module). Relics were consumed exactly once (not double).

**Pass:** host rolls; client applies the same snapshot; relics consumed once; rebuilt module matches on both.

---

### T2 — Host-operated commit converges

1. On the **host**: dock + insert + Commit (inline). Host log: `[Forge] Committed …` + `→ sent commit result box=N`.
2. **Client** log: `← applied commit result box=N L…`.
3. Rebuild → module matches on both.

**Pass:** a host commit reaches the client identically.

---

### T3 — Cursed → burden is authoritative

1. Client inserts a **cursed** relic (visible via `!cursedstatus` after 8-B) and commits.
2. Host resolves cursed from **its own** marker and logs `[Forge] Burden: cursed x1 consumed, roll … → APPLIED RandomShutoff` — even though the roll ran on the host, not the client.
3. Result broadcast carries the burden; client's snapshot for the box includes it; rebuilt module carries the burden on both (`!listburdens`).

**Pass:** the burden is decided by the host and appears identically on all clients.

---

### T4 — Late-joiner overlay

1. Host (solo, or with client) upgrades a module to L5 with a perk; **then** a second player joins.
2. Host log: `→ sent overlay snapshot (M boxes) to joiner #N`. Joiner log: `← applied commit result box=… L5` for each.
3. Joiner sees the upgraded module at L5 with the perk (after it reconstructs on their client).

**Pass:** a mid-run joiner sees already-upgraded modules, not vanilla ones.

---

## Known limitations (do NOT fail QA on these)

1. **Overlay-on-join is a race.** The snapshot must arrive before the joiner's module reconstructs. If a module was already built on the joiner before the snapshot lands, it shows vanilla until rebuilt. (Modules usually reconstruct during the joiner's scene load, after they enter the room, so this is usually fine.)
2. **Operator notification is basic** — the client operator sees "Upgrade committed by the host (consumed K relics)"; the full perk/burden description + the 7-A/7-C causal logs are on the **host**. The module itself shows the perks once rebuilt.
3. **One operator per commit.** The `_relics`/`_moduleBox` docking state is local, so the flow assumes a single crewmate docks + inserts + commits. Two players inserting into the same forge simultaneously is an unsupported edge (pre-existing docking-locality issue).
4. **Relic consumption needs operator ownership** — the operator must own the relics it carried in (normal case). If it somehow doesn't, `DestroyRelic` falls back to a local destroy (pre-existing behaviour).
