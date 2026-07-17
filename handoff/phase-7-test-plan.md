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
| `BurdenIntervalMin/MaxSeconds` | `30` / `90` | Gap between shutoff events (burden only turns the module OFF; the crew restores it) |
| `PerkRollChanceRare` / `…Legendary` | `0.40` / `0.75` | Perk roll gate — set to `1.0` to make T2 deterministic |

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

> **Two gotchas that block this test** (both hit on the first attempt): the perk roll is chance-gated (Rare 40% / Legendary 75%), so it often just fails; **and** the relic must be an actual **flagship** — a plain Rare like `Relic_11_A` yields a `POOL draw` even when the roll lands. Make it deterministic: set **`PerkRollChanceRare = 1.0`** (and/or `…Legendary = 1.0`) so every roll succeeds, and use a flagship relic below.

**Setup:** Forge installed with a free perk slot; `PerkRollChanceRare = 1.0`. Flagship relics & signatures:
`Relic_15_BiomassForThrustersAndDamage`→`sig_biomass_ram` (Legendary), `Relic_28_PayloadRecharge`→`sig_sustained_payload` (Legendary), `Relic_12_BenedictionDamageForAccuracy`→`sig_holy_purpose` (Rare), `Relic_13_ConfessorFireRateForPower`→`sig_confessor_cadence` (Rare), `Relic_02_PowerForBreakers`→`sig_overcharged_grid` (Rare).

**Steps:**
1. `!spawn Relic_12_BenedictionDamageForAccuracy`, insert it, `!forgecommit`. With the roll forced to 100% it lands immediately. Expected:
   `[Forge] Perk: SIGNATURE 'sig_holy_purpose' preferred over category pool (flagship relic Relic_12_BenedictionDamageForAccuracy; consumed [...]) → slot N`
2. Contrast: commit a **non-flagship** relic of the same tier (e.g. `Relic_11_A_WeaponFireRateForDefects`). Still 100% roll, but expected:
   `[Forge] Perk: POOL draw '<id>' (no signature among consumed [...]) → slot N`
3. `!perks` confirms each slot holds the expected perk (the signature vs a generic pool perk).

**Pass:** the flagship commit logs `SIGNATURE ... preferred over category pool` naming the right relic; the non-flagship logs `POOL draw`. Both reflected in `!perks`.

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

### T4 — RandomShutoff behaves: shuts OFF only, never restores

**Goal:** The burden cuts power to the module and **leaves it off** — restoring it is the crew's job. It never turns the module back on, and a vetoed shutoff is never silent. Owner-authoritative.

**Setup:** A module carrying a RandomShutoff burden from T3.

**Steps:**
1. `!triggerburden`. The module powers down and you get **"… powered down — switch it back on manually."** (driven by real `PowerDrain.IsOn.OnChange`). Log: `[Burden] <module> shutoff applied (IsOn->False).` **Then wait** — the module must **stay off**. There must be **no** `restore`/`IsOn->True` line and no auto power-on.
2. Switch the module back on yourself (panel). The burden says nothing and does not fight it. After the next interval it may shut off again — that's the tax.
3. **Already-off case:** leave the module off (or switch it off yourself), then `!triggerburden`. It must do nothing — log `[Burden] <module> already off — nothing to shut off this cycle.` — and never a stray power-on.
4. **Veto visibility:** if a shutoff is ever refused by the game, it logs `… shutoff DECLINED by a ChangeValidator …` — never a silent nothing.
5. `!listburdens` while off shows `currently OFF — crew must switch it back on`; while running shows `Ns until next shutoff`.

**Pass:** the module goes dark and **stays** dark until the crew restores it (no auto-restore line ever); the already-off cycle no-ops; no shutoff is ever silent.

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
