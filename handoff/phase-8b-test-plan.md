# Phase 8-B — Cursed marker live sync (test plan)

**Project:** VoidCrewTerminus
**Phase:** 8-B — host broadcasts per-relic cursed state (by PhotonView.ViewID) to clients; join snapshot; spawn-order race buffered.
**Status:** Code complete. **Requires 2-client verification.**
**Date:** 2026-07-17

## What changed

- **Host rolls + broadcasts.** `CursedRelicSpawnPatch` still rolls cursed authoritatively on the master client, then calls `ForgeNetSync.BroadcastCursed(pv, burden)` → `CursedRelicMessage {viewID, burden}` to all clients.
- **Clients mirror it.** `ApplyIncomingCursed` marks the relic (found by `PhotonView.Find`). If the relic hasn't instantiated on the client yet, the flag is **buffered** in `_pendingCursed` and drained from the client's `OnPhotonInstantiate` (`TryApplyPendingCursed`).
- **Join snapshot.** On `OnPlayerEnteredRoom` the host sends the joiner every currently-cursed relic (`FindObjectsOfType<CursedRelicMarker>`); the joiner buffers/applies as its objects spawn.
- **Awareness only.** Cursed sync feeds `!cursedstatus` and the future hover UI. Outcomes stay host-authoritative (8-C), so a client mis-seeing cursed can't change a commit.
- `!cursedstatus` on a client now reads correctly (warning removed). `!forcecursed` on a client is still **local-only** (not synced host-ward) and says so.

## Definition of done

2-client convergence in BOTH logs:
- host: `[Net] → sent cursed relic viewID=N (RandomShutoff) to all.`
- client: `[Net] ← applied cursed relic viewID=N (RandomShutoff).` (or `← buffered …` then `← applied buffered …`)

A client `!cursedstatus` showing the same cursed relic the host sees.

## Setup

Two instances, latest dll, `EnableDevMode = true`, host a "Pilgrimage" run + join. Capture both logs. (Curses roll from sector 1 — no boss threshold needed. Raise `RelicBaseCurseChance` toward 1.0 to guarantee cursed spawns for testing.)

---

### T1 — Live cursed sync

1. Host spawns relics (`!spawn Relic_11_A_WeaponFireRateForDefects`, or clear a POI). A cursed one logs on host `[Escalation] Relic … spawned CURSED …` + `[Net] → sent cursed relic viewID=N`.
2. **Client log:** `[Net] ← applied cursed relic viewID=N (RandomShutoff).`
3. Client picks up / stands near that relic, `!cursedstatus` → shows it `CURSED (RandomShutoff)` — matching the host.

**Pass:** client `← applied` matches host `→ sent`; client `!cursedstatus` agrees.

---

### T2 — Spawn-order race is buffered

The two orderings must both resolve (they're timing-dependent, so watch for whichever occurs):
- **Message-before-object:** client logs `← buffered cursed relic viewID=N … object not spawned yet`, then shortly `← applied buffered cursed relic viewID=N`.
- **Object-before-message:** client logs only `← applied cursed relic viewID=N` (found immediately).

Over several spawns you should see at least the plain `← applied`; a `← buffered … → applied buffered` pair confirms the race path works. Either way, **no cursed relic ends up unmarked** on the client.

**Pass:** every host-cursed relic ends up `CURSED` on the client (`!cursedstatus`), regardless of message/object order.

---

### T3 — Join snapshot

1. Host, **alone**, forces several cursed relics: `!forcecursed on` on a few (or raise `RelicBaseCurseChance` and spawn a batch).
2. Second player **joins**. Host log: `[Net] → sent cursed snapshot (K relics) to joiner #N`.
3. Joiner log: `← applied` / `← buffered` for those viewIDs as its relic objects spawn in.
4. Joiner `!cursedstatus` on those relics → all show `CURSED`.

**Pass:** a mid-run joiner sees the already-cursed relics, not a clean slate.

---

### T4 — Client `!forcecursed` is local-only (honesty)

1. On the **client**, `!forcecursed on` a relic → prints the `NOTE: !forcecursed is local-only on a client …` warning; the client's `!cursedstatus` shows it cursed **locally**.
2. Host `!cursedstatus` on the same relic → **not** cursed (the client's force didn't propagate — only host spawn-rolls are authoritative).

**Pass:** the warning shows; the client-forced flag does not appear on the host.

---

## Known limitations (do NOT fail QA on these)

1. **Awareness only** — cursed sync doesn't affect outcomes yet; the authoritative commit is 8-C. An off-host commit still logs the divergence warning.
2. **`!forcecursed` is not synced** — it's a local dev override by design; only the host's spawn roll broadcasts.
3. **Cross-run pending staleness** — a buffered ViewID from a prior run is harmless (its object is gone; it never resolves). Pending clears on hot-reload teardown.
