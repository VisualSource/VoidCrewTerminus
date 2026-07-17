# Phase 7 — consolidated test plan (Signatures + Cursed + Maintenance Burden)

**Project:** VoidCrewTerminus
**Covers:** 7-A signature perks · 7-B per-instance cursed relics · 7-C Maintenance Burden (RandomShutoff)
**Status:** Code complete incl. the 2026-07-16 fix pass. **NONE verified in-game yet.**
**Date:** 2026-07-16

This is the short QA pass — 5 tests, each proving one causal chain **end-to-end in a real log**. It supersedes the long `phase-7a/b/c-test-plan.md` files for day-to-day verification; reach for those only if a test here fails and you need the granular breakdown.

## Definition of done (read first)

Phase 6 was marked "shipped" while enemy density was a silent no-op. So for Phase 7 **nothing counts as verified until its causal log line is observed in a captured `BepInEx/LogOutput.log`** — a dev command showing the right state is necessary but not sufficient. Each test below names the exact line to look for.

## Environment

- Latest `VoidCrewTerminus.dll` in `BepInEx/plugins/`; `EnableDevMode = true` in `void.crew.terminus.cfg`.
- **Play on the HOST**, in the main **"Pilgrimage"** menu mode (internally `EndlessQuest` — the only mode where escalation/cursed run). Cursed state lives in a host-only marker until Phase 8; on a client every relic reads clean and the commands say so.
- An installed Upgrade Forge with capacity for L4+ (`!forgespawn` if needed).

**Relevant config (`[forge]`)**

| Key | Default | Role |
|---|---|---|
| `RelicBaseCurseChance` | `0.15` | Base curse chance (applies from sector 1 — NOT gated on bosses) |
| `EscalationCurseChancePerScalar` | `0.03` | Added per `DifficultyScalar` (scalar only climbs post-activation) |
| `RelicMaxCurseChance` | `0.50` | Hard ceiling on final curse chance |
| `BurdenApplicationChance` | `0.75` | Chance a cursed-relic commit attaches the burden |
| `BurdenShutoffMin/MaxSeconds` | `2` / `4` | RandomShutoff dark window |
| `BurdenIntervalMin/MaxSeconds` | `30` / `90` | Gap between shutoffs |

**Dev commands:** `!cursedstatus` · `!forcecursed <on\|off> [burden]` · `!listburdens` · `!triggerburden` · `!perks` · `!forceperk` · `!setbosses` · `!setdifficulty` · `!forgespawn` · `!forgeinsert` · `!forgecommit`

---

### T1 — Cursed relics roll at spawn, ungated, capped

**Goal:** Curses roll once per relic at its own spawn, from the first sector (no boss gate), and the chance is held under the ceiling at depth. This is the 7-B fix pass: per-object `OnPhotonInstantiate` hook + ungate + `RelicMaxCurseChance`.

**Steps:**
1. Fresh run, **0 bosses**, `!setdifficulty 0`. Spawn a batch of relics (`!spawn Relic_02_PowerForBreakers`, etc., or clear a POI).
2. Watch the log during spawns. Expected line for each cursed one:
   `[Escalation] Relic Relic_02_PowerForBreakers spawned CURSED with RandomShutoff (chance 20.0%)`
   — appearing at **0 bosses** proves the gate is gone. At ~15–25% you'll see a minority cursed.
3. `!cursedstatus` on a spawned relic → confirms the marker matches the log (`CURSED (RandomShutoff)` vs `clean`).
4. `!setdifficulty 30`, spawn more, `!cursedstatus`. The breakdown must show `(capped from XX%)` and the effective chance must not exceed `50%`.

**Pass:** cursed spawn log lines appear at 0 bosses; a spawned relic's `!cursedstatus` agrees with its spawn log; at scalar 30 the chance reads capped at ≤50%. (Verifies ungate + per-object roll + ceiling in one test.)

---

### T2 — Signature perk beats the category pool

**Goal:** A consumed flagship relic yields its signature perk in preference to a category-pool draw. **This is the one with no automated coverage** (`PerkRoll_FlagshipRelic_PicksSignatureOverCategoryPool` is skipped on `StatType` init), so the log line is its *only* proof.

**Setup:** Forge installed with a free perk slot. Flagship relics & signatures: `Relic_15_BiomassForThrustersAndDamage`→`sig_biomass_ram`, `Relic_28_PayloadRecharge`→`sig_sustained_payload`, `Relic_12_BenedictionDamageForAccuracy`→`sig_holy_purpose`, `Relic_13_ConfessorFireRateForPower`→`sig_confessor_cadence`, `Relic_02_PowerForBreakers`→`sig_overcharged_grid`.

**Steps:**
1. Spawn a flagship relic (e.g. `Relic_12`), insert it, `!forgecommit`. Repeat until the perk roll lands (Legendary/Rare chance-gated).
2. On a landing commit, expected:
   `[Forge] Perk: SIGNATURE 'sig_holy_purpose' preferred over category pool (flagship relic Relic_12_BenedictionDamageForAccuracy; consumed [...]) → slot N`
3. Contrast: commit with a **non-flagship** relic of the same tier. Expected:
   `[Forge] Perk: POOL draw '<id>' (no signature among consumed [...]) → slot N`
4. `!perks` confirms the slot now holds the expected perk.

**Pass:** flagship commit logs the `SIGNATURE ... preferred over category pool` line naming the right relic; non-flagship logs `POOL draw`. Both reflected in `!perks`.

---

### T3 — Cursed relic → Maintenance Burden attaches (and survives rebuild)

**Goal:** Consuming a cursed relic runs the independent burden roll and, on success, stamps the burden onto the module — orthogonally to the perk. Burden persists through the L4+ module rebuild (snapshot bridge).

**Setup:** `BurdenApplicationChance = 1.0` temporarily for determinism. Forge installed.

**Steps:**
1. Hold a relic, `!forcecursed on` → `!cursedstatus` shows `[HELD] ... CURSED (RandomShutoff)`.
2. Insert it, `!forgecommit`. Expected:
   `[Forge] Burden: cursed x1 consumed, roll 100% → APPLIED RandomShutoff`
3. `!listburdens` shows RandomShutoff on the target module.
4. **Rebuild** the module (the L4+ deconstruct/reconstruct) and `!listburdens` again — the burden must still be there (rides `ForgeSnapshot.Burdens`).
5. Negative: set `BurdenApplicationChance = 0`, commit another cursed relic. Expected `→ none (roll failed)`, no burden. And commit a **non-cursed** relic → `[Forge] Burden: no cursed relics consumed — no roll.`

**Pass:** the `APPLIED RandomShutoff` line fires on a cursed commit; burden shows in `!listburdens` and survives rebuild; chance 0 and non-cursed commits both correctly attach nothing (with their distinct log lines).

---

### T4 — RandomShutoff behaves: owner-authoritative, loud on veto, respects the crew

**Goal:** The burden actually darkens the module, notifications track *real* power state, a vetoed request is never silent, and the burden never fights player intent. This is the 7-C fix pass (the `CellModule.TurnOff` = vetoable `RequestChange` trap).

**Setup:** A module carrying a RandomShutoff burden from T3.

**Steps:**
1. `!triggerburden`. The module should visibly power down; you get **"… powered down."**, then after 2–4s **"… restored."** These come from the real `PowerDrain.IsOn.OnChange`. Log:
   `[Burden] <module> shutoff applied (IsOn->False)` … `restore applied (IsOn->True)`.
2. **Crew-override case:** `!triggerburden`, and *while it's dark* manually power the module back on from the panel. The burden must **stand down**, not re-fight it. Log:
   `[Burden] <module> IsOn changed to True externally during our shutoff — will not restore.`
3. **Already-off case:** manually power the module off, then `!triggerburden`. It must **skip**, not schedule a stray TurnOn. Log:
   `[Burden] <module> already powered down — skipping shutoff, rescheduling.`
4. **Veto visibility:** if any shutoff is ever refused by the game, it must log `… DECLINED by a ChangeValidator …` — never a silent nothing.

**Pass:** module darkens & recovers with notifications matching real state; the crew-override and already-off cases log the stand-down/skip lines and never force power against the player; no shutoff is ever a silent no-op.

---

### T5 — `!cursedstatus` tooling + off-host honesty

**Goal:** A tester can read cursed state from the relic in-hand, and the tool refuses to lie on a client (host-only markers).

**Steps:**
1. Pick up a relic; `!cursedstatus`. First line is `[HELD] <name>: <tier> — clean/CURSED ...`; nearby relics follow with `[Xm]` distances.
2. `!forcecursed on RandomShutoff` on the held relic → `!cursedstatus` flips `[HELD]` to `CURSED (RandomShutoff)`; `!forcecursed off` → back to `clean`.
3. **(If 2-client available)** on the **client**, `!cursedstatus` must print the `NOTE: cursed state is host-only until Phase 8 — ... reads CLEAN ...` banner. Confirms the tool won't mislead off-host.

**Pass:** held relic is reported first and tracks `!forcecursed`; the client warns instead of silently reporting everything clean.

---

## Known limitations (do NOT fail QA on these)

1. **Cursed / burden state is host-only** until Phase 8 — client commits read relics as un-cursed; an off-host commit logs a `Commit running OFF-HOST …` warning. Expected.
2. **`BurdenAffinity` is inert** — only `RandomShutoff` exists, so every relic's affinity resolves to it. The per-relic affinity data is forward-looking for future burden types.
3. **7-D dynamic-scaling signatures (Stalker/Hoarder)** are not in this pass.
