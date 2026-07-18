# Phase 8 — Multiplayer sync (consolidated test plan)

**Project:** VoidCrewTerminus
**Covers:** 8-A meter/escalation state + alloy hop · 8-B cursed marker sync · 8-C authoritative commit + overlay.
**Status:** Code complete for all three. **NONE verified — Phase 8 is invisible without 2 clients.**
**Date:** 2026-07-17

7 tests. Supersedes the per-sub-phase `phase-8a/b/c` plans (kept in git history if you need the granular breakdown).

## Definition of done (read first)

**A message that sends proves nothing — only that the OTHER client applies it does.** So every sync logs both sides and a test passes only when a captured log from **both** instances shows the value converge:
- host: `[Net] → sent …`
- client: `[Net] ← applied …` (or `← buffered …` then `← applied buffered …`)

A host `→ sent` with no matching client `← applied` = discovery/routing broken. Authority = master client; solo (not in a room) is authority too, so single-player is unchanged and broadcasts no-op.

## Setup

- **Two instances**, latest `VoidCrewTerminus.dll`, `EnableDevMode = true`. One hosts a **"Pilgrimage"** run (internally EndlessQuest — the only mode escalation/cursed run in); the other joins.
- Capture `BepInEx/LogOutput.log` from **both**.
- Handy knobs: `PerkRollChanceRare = 1.0` (force a perk for T4/T5), `RelicBaseCurseChance` toward 1.0 (force cursed spawns for T3).

---

### T1 — Escalation + meter state converge, incl. client alloy feed (8-A)

1. Host: `!setbosses 2`, `!setdifficulty 5`, `!setforgelevel 3`. Each logs `[Net] → sent forge state {scalar=…, bosses=…, meter=…, level=…}`.
2. **Client:** `[Net] ← applied forge state {scalar=5, bosses=2, …, level=3}`; `!difficulty` / `!forgemeter` match the host; the client's forge shows L3 (tube visibility updated).
3. **Client** operates the alloy terminal → client `[Net] → sent alloy-spend request to host`; host `[Net] ← alloy-spend request … spent` → `→ sent forge state` → client `← applied` with the raised meter/level. (Pre-8-A a client couldn't feed the forge at all.)

**Pass:** client counters/meter/level match host; a client can feed the forge and the result comes back.

---

### T2 — Live progression converges + loot determinism (8-A)

1. Both in the same run; host completes sectors / defeats bosses **normally** (no dev commands). Each host award logs `→ sent`; client logs a matching `← applied` with the incremented value.
2. After a sector where the scalar changed, `!lootdump` on host and client → **same ceiling/tier counts** (client reshapes with the synced scalar).

**Pass:** client counters track the host live; loot reshape matches across both. *(If `!lootdump` ever diverges, the state broadcast lagged the sector's loot setup — note it.)*

---

### T3 — Cursed relics sync live + spawn-order race buffered (8-B)

1. Host spawns/curses relics (`!spawn …`, or raise `RelicBaseCurseChance`). A cursed one logs host `[Escalation] Relic … spawned CURSED …` + `[Net] → sent cursed relic viewID=N`.
2. **Client:** `[Net] ← applied cursed relic viewID=N` **or** `← buffered … object not spawned yet` then `← applied buffered …`. Over several spawns you should see at least one plain `← applied`; a buffered→applied pair confirms the race path.
3. Client `!cursedstatus` on that relic shows `CURSED (RandomShutoff)` — matching the host. **No host-cursed relic ends up unmarked on the client.**
4. **Honesty check:** on the client, `!forcecursed on` a relic → prints the `local-only` warning; the host's `!cursedstatus` on it is **not** cursed (client force doesn't propagate).

**Pass:** every host-cursed relic shows cursed on the client regardless of message/object order; client `!forcecursed` stays local.

---

### T4 — Client-operated commit is authoritative and converges (8-C)

1. On the **client**: `!forgespawn`, dock a Mk III box, insert relics, click **Commit**. Client log: `[Net] → sent commit request box=N (K relics)`.
2. Host: `[Net] ← commit request … resolved` → `[Forge] Committed L…→L…` + `[Forge] Perk: SIGNATURE/POOL …` → `[Net] → sent commit result box=N L…`.
3. Operator client: `[Net] ← applied commit result box=N` + "Upgrade committed by the host (consumed K relics)"; its relics vanish **once** (not doubled).
4. **Converge:** rebuild the module → host and client show the **same** level + perk (`!perks` / the module).

**Pass:** host rolls; all clients apply the same snapshot; relics consumed exactly once; rebuilt module matches.

---

### T5 — Host-operated commit + cursed→burden authority (8-C over 8-B)

1. On the **host**: dock + insert (include a **cursed** relic) + Commit inline. Host log: `[Forge] Committed …` + `[Forge] Burden: cursed x1 consumed, roll … → APPLIED RandomShutoff` + `→ sent commit result box=N`.
2. **Client:** `← applied commit result box=N L…`. Rebuild → the module carries the **same** level, perk, and burden on both (`!listburdens`).
3. Reverse it: a **client** commits a cursed relic; the host resolves cursed from **its own** marker and still logs `APPLIED RandomShutoff` — proving the roll is host-authoritative, not client-supplied.

**Pass:** commit outcome (incl. the burden) is decided by the host and identical on all clients, whichever side operates.

---

### T6 — Late joiner catches up on everything (8-A + 8-B + 8-C)

1. Host advances state **before** the second player joins: `!setbosses 2`, `!setdifficulty 6`, `!setforgelevel 4`; curse a few relics; upgrade a module to L5 with a perk.
2. Second player **joins**. Host logs three targeted pushes: `→ sent forge state … to joiner #N`, `→ sent cursed snapshot (K relics) to joiner`, `→ sent overlay snapshot (M boxes) to joiner`.
3. Joiner logs `← applied` for the state, the cursed relics (as their objects spawn), and each box overlay.
4. Joiner `!difficulty` / `!forgemeter` match immediately; cursed relics read `CURSED`; the upgraded module shows L5 + perk once it reconstructs.

**Pass:** a mid-run joiner sees current counters, cursed relics, and upgraded modules — not a clean slate.

---

### T7 — Host migration doesn't freeze escalation (8-A)

1. 2 clients in a run with escalation active. **Host leaves** (close the host instance).
2. Surviving client becomes master: `[Net] Became master client — asserting forge-state authority.`
3. That client completes a sector / defeats a boss → its log shows the host-side award (`[Forge] Meter +…`, `[Escalation] Boss defeated …`).

**Pass:** escalation keeps advancing under the new master — it does **not** silently freeze.

---

## Known limitations (do NOT fail QA on these)

1. **Notifications are host-side** (meter/level toasts, "Escalation is now active"). Clients get the state; the operator client gets a basic "Upgrade committed by the host" — full perk/burden detail + the 7-A/7-C causal logs are on the host.
2. **Loot-reshape determinism** depends on the state broadcast arriving before the next sector's loot setup (reliable ordering makes this very likely; T2 is the check).
3. **Overlay-on-join is a race** — the snapshot must arrive before the joiner's module reconstructs, else it shows vanilla until rebuilt (usually fine — modules reconstruct during the joiner's scene load).
4. **One operator per commit** — `_relics`/`_moduleBox` docking state is local, so the flow assumes one crewmate docks + inserts + commits; two players inserting into the same forge at once is unsupported.
5. **`!forcecursed` is not synced** (local dev override by design); only the host's spawn roll broadcasts. Cross-run buffered cursed ViewIDs are harmless and clear on hot-reload.
