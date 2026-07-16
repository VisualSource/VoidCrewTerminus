# Phase 6 — Sector Escalation test plan

**Project:** VoidCrewTerminus
**Phase:** 6 — Sector Escalation (loot tier biasing + enemy density + HP + damage + activation gate)
**Status:** Shipped, ready for QA verification.
**Date:** 2026-07-14 (updated 2026-07-15)

## What this covers

Phase 6 introduces two counters (`DifficultyScalar`, `BossesDefeated`) and four escalation systems (loot bias, enemy density, enemy HP, enemy damage). This plan verifies each system independently and then the whole thing end-to-end.

For the design intent and implementation details, read [`docs/upgrade-forge-design.html`](../docs/upgrade-forge-design.html) — the `Sector Escalation` and `Phase 6` sections are the authoritative reference. The `Known limitations` list at the bottom of that section tells you what's intentionally out of scope for QA.

## Changes since the 2026-07-14 QA pass (re-verify these)

- **T1–T4 removed** — activation-gate warm-up (T1–T3) and basic loot reshape (T4) were verified on 2026-07-14. They're gone from this doc; the scenarios below start at T5.
- **Loot tier biasing is no longer gated by the boss-activation threshold.** It now reshapes from the first sector, driven purely by boss count: **0 bosses → Common only, 1 → Rare unlocked, 2 → Legendary**. (Previously it was suppressed until 2 bosses — exactly the point where the ceiling was already Legendary — so it never downgraded anything. That was the bug.) The enemy systems (density/HP/damage) **keep** the 2-boss warm-up gate; only loot changed.
- **Loot biasing is Endless-only.** Bosses are only defeatable in `EndlessQuest`, so the loot patch now no-ops in Pilgrimage/Survivor/etc. to avoid crushing all their loot to Common forever.
- **Forge Meter + DifficultyScalar now require a `Completed` objective.** Leaving a sector in `Started` (mission abandoned mid-jump), `Failed`, or `NoObjective` awards nothing — previously only `Failed` was blocked, so abandoned missions still paid out.
- **Density scaling now logs.** When a spawner intensity is actually scaled, `EnemyDensityPatch` emits `[Escalation] Density N → M (scalar X, rate Y)` at debug level — use it to confirm T6 without eyeballing spawn counts.
- **Density scaling was fundamentally broken and is now fixed.** The old patches scaled a spawner's *target* intensity, but `Spawner.SetTargetIntensity` clamps it to `maxTargetIntensity`, which is baked in from the profile at spawner creation (bypassing the patched mutators). So whenever a scenario drove intensity to its max — the common case — the boost was clipped and **no extra enemies spawned**. Fixed by a postfix on `Spawner.InitSpawner(SpawnerProfile)` that raises the ceiling (host-only) so the target scaling has headroom. Verify with `!spawners`.
- **Enemy scaling is now capped.** `EscalationScalarCap` (default 10) bounds the effective scalar for density/HP/damage so deep runs don't spawn an unbounded number of networked ships. Density plateaus at **2.2×**, HP/damage at **+50%**. Loot tiers and the raw scalar are unaffected. Density rate lowered `0.20 → 0.12` (the old value was never felt because scaling didn't work; retune via config).

## Test environment

**Prereqs**
- BepInEx + VoidManager installed against a live Void Crew build.
- Latest `VoidCrewTerminus.dll` deployed to `BepInEx/plugins/`.
- Dev mode enabled: set `EnableDevMode = true` in `BepInEx/config/void.crew.terminus.cfg`.

**Config knobs relevant to this phase** (all under `[forge]` in the config file)
| Key | Default | Purpose |
|---|---|---|
| `EscalationBossActivationThreshold` | `2` | Boss defeats required before the **enemy** systems (density/HP/damage) activate. Does **not** gate loot biasing — that runs from the first Endless sector. |
| `EscalationRareUnlockScalar` | `3` | Below this scalar, Rares get downgraded to Common |
| `EscalationLegendaryUnlockScalar` | `6` | Below this scalar, Legendaries get downgraded to Rare |
| `EscalationBossScalarBonus` | `1` | Scalar bump per boss defeat (post-activation) |
| `EscalationDensityScalarPerJump` | `0.12` | Density multiplier per scalar tick (primary axis). At the cap of 10 → density tops out at 2.2×. |
| `EscalationStatScalarPerJump` | `0.05` | HP + damage multiplier per scalar tick (minor axis). At the cap of 10 → +50%. |
| `EscalationScalarCap` | `10` | Upper cap on the **effective** scalar used for enemy scaling (density/HP/damage). Raw scalar still climbs for loot/display; enemy pressure plateaus here. `0` = uncapped. |

**Dev commands available**
| Command | Purpose |
|---|---|
| `!difficulty` | Prints `ACTIVE`/`DORMANT` status + current scalar + boss count |
| `!setdifficulty <n>` | Force `DifficultyScalar` to a value |
| `!setbosses <n>` | Force `BossesDefeated` to a value |
| `!lootdump` | Dump current sector's per-rarity loot pool (post-reshape) with tier counts |
| `!spawners` | Dump every live `AIDirector` spawner's Target/Current/Max intensity — the direct way to verify density scaling without counting ships |
| `!forgemeter` | Show Forge Meter progression |
| `!setmeter <v>` / `!setforgelevel <n>` | Direct meter/level manipulation |

**Automated coverage** (already passing in `dotnet test`)
- Pure algorithms: `EnemyScaling.ScaleIntensity`, `SectorEscalation.MaxAllowedTier`, `SectorEscalation.DowngradeRelics`.
- Faction membership helpers.
- Activation gate: `IsScalingActive` true/false thresholds, `ResetForRun` clears state.
- **What automated tests do NOT cover:** anything that touches Unity runtime — StatMod attachment (HP scaling), `DestroyableComponent.CalculateRawDamage` interception (damage scaling), the actual `AIDirector` intensity mutations, in-game loot spawns. These need in-game verification.

## Test scenarios

> **T1–T4** (activation-gate warm-up dormancy, threshold-crossing boss defeat, post-activation ramp, basic loot reshape) were verified on 2026-07-14 and have been retired. Scenarios resume at T5. Note the ramp/gate behaviour they covered is unchanged **for the enemy systems**; only loot was decoupled from the gate (see "Changes since the 2026-07-14 QA pass").

### T5 — Loot tier biasing: boss-count ceiling (always-on, Endless-only)

**Goal:** Confirm loot lists reshape from the first sector, driven by boss count, with `MaxAllowedTier = max(scalarCeiling, bossCeiling)`. Loot biasing is **not** gated by the activation threshold anymore.

**Setup:** Fresh Endless run. No dev overrides yet.

**Steps:**
0. **Name-match sanity check (do this first).** Run `!lootdump` in any relic-bearing sector. If it prints `WARNING: 0 relics recognized by RelicTierData …` plus a `raw filenames: …` line, then `RelicTierData`'s keys don't match the live `CraftableItemRef.Filename` values and **loot gating is a silent no-op** — capture those raw filenames and report them so the key map can be fixed. If you see `Common=/Rare=/Legendary=` tier counts, the names match and gating is live; continue.

> **The `Reshape:` line is your main signal.** `!lootdump` now prints `Reshape: ceiling=<tier> (scalar N, bosses M); downgraded X, dropped Y. [bucket] Cn/Rn/Ln → Cn/Rn/Ln …` — the before→after tier histogram of the last reshape. This is the direct proof the biasing ran and what it did. The same summary is logged as `[Forge] Loot reshaped: …`.
>
> **Gotcha — test at 0 or 1 boss, not 2.** At `bosses=2` the ceiling is **Legendary**, so nothing is over the ceiling and the reshape correctly downgrades **nothing** — `!lootdump` looks identical to vanilla. That's not a failure. To *see* downgrades: `!setbosses 0` (everything → Common) or `!setbosses 1` (Legendary → Rare).
1. **0 bosses (fresh run, no forcing):** enter the first relic-bearing sector and run `!lootdump`. Expected: **every relic is Common** — all Rares/Legendaries downgraded to Common (or dropped if the list had no Common candidate). Non-relic entries untouched. This is the key regression check: before the fix, 0-boss sectors passed through unchanged.
2. `!setbosses 1` (boss ceiling = Rare), re-enter a sector, `!lootdump`. Expected: Legendaries downgrade to Rare, Rares stay, Commons stay.
3. `!setbosses 2` (boss ceiling = Legendary), `!lootdump`. Expected: nothing downgrades — Legendary is the ceiling.
4. Confirm `MaxAllowedTier` respects the scalar side too: `!setbosses 0` + `!setdifficulty 6`, `!lootdump`. Expected: nothing downgrades (scalar alone hits the Legendary ceiling).
5. Kill enemies at step 1's state; verify actual dropped relics match the reshaped (all-Common) distribution. Sample: 10–20 loot droppers.
6. Re-enter the same sector — reshape must be identical (determinism: `quest.Seed + sector.Id` seeds both the shuffle and our downgrade).

**Pass criteria:**
- 0-boss Endless sectors show **all-Common** loot (the fix).
- Boss count promotes the ceiling (0→Common, 1→Rare, 2→Legendary); scalar can promote it independently.
- Actual drops match the reshaped list; re-entry is deterministic.

**Negative case (Endless-only guard):** Start a **Survivor** quest (the wave-based mode — base `Quest`, not `EndlessQuest`). Run `!lootdump` in a relic sector. Expected: loot passes through **untouched** (vanilla tiers) — the loot patch no-ops outside `EndlessQuest`. This must hold even though `BossesDefeated=0`.

> **Naming note:** the game's main roguelike mode is *labeled* **"Pilgrimage"** in the menu but is internally an `EndlessQuest` (`GamemodeType.Default` → `SelectEndlessQuest`). Escalation therefore **does** apply to the "Pilgrimage" menu mode — that's the mode QA has been testing, and why boss defeats register there. The `is EndlessQuest` guard only excludes base-`Quest` modes (Survivor, and any Generic/Tutorial/Special content).

---

### T6 — Density scaling: spawner intensity (the fix)

**Goal:** Confirm `AIDirector` spawners actually produce more enemies at high scalar — the case that was silently broken by the intensity clamp before this fix.

**Setup:** Post-activation state (`!setbosses 2` to activate). Must be an `EndlessQuest`-backed run (the "Pilgrimage" menu mode). **Host client** — density scaling is host-authoritative.

> With `EscalationScalarCap=10` and rate `0.12`, effective density is `1 + min(scalar,10)*0.12`: scalar 5 → 1.6×, scalar 10 → **2.2× (plateau)**, scalar 25 → still 2.2×.

**Steps:**
1. `!setdifficulty 0`, then `!spawners` during an encounter. Note each spawner's `Target`/`Max` — these are the **vanilla** profile values (baseline).
2. `!setdifficulty 10`. Enter a **fresh** sector (the ceiling is scaled at spawner *creation*, so it applies to spawners made after the change — don't expect already-spawned encounters to retroactively grow). Trigger the same encounter type.
3. `!spawners` again. Expected: `Max` and `Target` are **~2.2× the baseline** from step 1. This is the direct proof the ceiling fix works — before the fix, `Max` stayed at the vanilla value and `Target` was clamped to it.
4. Count enemies over ~60s at step 1 vs step 3. Expected: visibly more at scalar 10 (up to ~2.2× per encounter).
5. **Log confirmation:** `[Escalation] Density N → M …` (scenario target writes) and `[Escalation] Spawner intensity T/M → …` (ceiling scaled at creation) appear at scalar 10, none at scalar 0.
6. **Cap check:** `!setdifficulty 25`, fresh sector, `!spawners`. Expected: identical `Max`/`Target` to scalar 10 — density plateaus at the cap, does not keep growing.

**Pass criteria:** `!spawners` shows `Max`/`Target` scaled ~2.2× at scalar ≥10 (and no higher past the cap); visibly more enemies; log lines present. Density affects the general `AIDirector` path, not Survivor waves.

**Negative case:** Start a **Survivor** quest. `!spawners` intensities should stay at vanilla values regardless of scalar (Survivor uses `WavesSpawnManager`, not `AIDirector`, and escalation is gated to `EndlessQuest` anyway).

---

### T7 — HP scaling: enemy MaxHitPoints

**Goal:** Confirm hostile-faction enemies get an HP boost proportional to scalar.

**Setup:** Post-activation state. Vanilla scaling for baseline comparison.

**Steps:**
1. `!setdifficulty 0`. Engage a Hollows enemy of known type (e.g., a Fighter). Note approximately how many hits from a standard weapon kill it.
2. `!setdifficulty 6`. Engage the same enemy type. Expected: ~30% more effective HP (5 hits → ~6-7 hits). (HP scales at `min(scalar,10)*0.05`, so it plateaus at **+50%** by scalar 10.)
3. Also check wildlife (space whale, drones) — these should NOT be scaled (wildlife faction ≠ Hollows/Remnant).
4. **Cap check:** `!setdifficulty 25` — HP should be the same +50% as scalar 10, not higher.

**Pass criteria:** Enemy HP visibly higher at high scalar. Wildlife untouched. Neutral objects untouched.

---

### T8 — Damage scaling: player takes more damage from enemies

**Goal:** Confirm enemy→player damage is scaled up at high scalar.

**Setup:** Post-activation. Ship in combat range of Hollows.

**Steps:**
1. `!setdifficulty 0`. Take a controlled hit (let an enemy shoot the hull once). Note approximate damage received.
2. `!setdifficulty 6`. Take the same type of hit. Expected: ~30% more damage.
3. Have the ship fire at ANOTHER enemy in the sector — player→enemy damage should NOT be scaled (only enemy→player).
4. Watch enemy→enemy friendly fire (if it happens) — should NOT be scaled.

**Pass criteria:** Only the enemy→player direction gets amplified. Player outgoing damage unchanged; NPC crossfire unchanged.

---

### T9 — Meter vs escalation asymmetry

**Goal:** Confirm the meter is gated on Forge presence but escalation counters are not.

**Setup:** Fresh Endless run, **no Forge installed**.

**Steps:**
1. Jump through 3 sectors normally. Run `!forgemeter`. Expected: Meter is 0 (or 0/threshold), Forge Level stays at 1 — no meter fill without a Forge.
2. Complete 2 boss objectives. Run `!difficulty`. Expected: `BossesDefeated=2/2, ACTIVE`. Yes — escalation activated even without a Forge.
3. Jump another sector. Run `!difficulty`. Expected: `DifficultyScalar=1` — sector jump bumped scalar despite no Forge on board.
4. **Now install a Forge.** Enemies immediately face the current scalar's density/HP/damage boost. Player experiences +5% enemy stats etc.

**Note on loot:** loot reshaping is independent of the Forge — it runs on `LootManager` driven by boss count, so sectors have been reshaping to the boss-count ceiling since run start regardless of whether a Forge is installed. Installing the Forge doesn't change the loot picture.

**Pass criteria:** No meter without Forge. Counters tick regardless. Installing a Forge mid-run applies the accumulated enemy intensity.

---

### T10 — Per-run reset

**Goal:** Confirm all Phase 6 state resets between runs.

**Setup:** Existing run with high scalar / high boss count (continue from T9, or force with `!setdifficulty 6` + `!setbosses 2`).

**Steps:**
1. Note current `!difficulty` and `!forgemeter` state.
2. Die or return to hub, then start a new Endless run.
3. Run `!difficulty` at the start of the new run. Expected: `DifficultyScalar=0, BossesDefeated=0/2, DORMANT`.
4. Run `!forgemeter`. Expected: Meter=0, Forge Level=1.
5. `!lootdump` in the first sector. Expected: **all relics Common** — with `BossesDefeated=0` the ceiling is Common, so the fresh run immediately downgrades to Common (loot biasing is always-on in Endless, not gated by activation). This is the inverse of the old expectation; confirm the reset didn't leave a stale higher ceiling.

**Pass criteria:** All counters, meter, and Forge level reset to zero/baseline on each new run; first-sector loot ceiling is back to Common.

---

### T11 — Quest-type scoping (base-`Quest` modes stay vanilla)

**Goal:** Confirm escalation only touches `EndlessQuest`-backed runs, and base-`Quest` modes are left completely alone.

> **Read the naming note in T5 first.** The menu's **"Pilgrimage"** mode is internally an `EndlessQuest` — escalation **is** active there (that's the main mode QA tests). This scenario is about the *other* modes that run on the base `Quest` class: **Survivor** (and Generic/Tutorial/Special content).

**Steps:**
1. Start the main **"Pilgrimage"** menu mode. Confirm escalation behaves normally (bosses count, loot reshapes, `!difficulty` reaches `ACTIVE` after 2 bosses). This is the positive control — it must scale.
2. Start a **Survivor** quest. Complete sectors / survive waves normally.
3. Run `!difficulty`. Expected: `DORMANT` forever — Survivor is a base `Quest`, so no bosses are registered and the loot patch no-ops.
4. `!lootdump` in Survivor. Expected: vanilla loot tiers, no downgrade (even at `BossesDefeated=0`).
5. Enemy density/HP/damage in Survivor should stay vanilla (Survivor uses `WavesSpawnManager`, which is untouched regardless).

**Pass criteria:** "Pilgrimage" menu mode scales; Survivor and other base-`Quest` modes never activate escalation and never reshape loot.

**Note:** To test the enemy escalation systems *without* clearing bosses, use `!setbosses 2` in an `EndlessQuest`-backed run. `!setbosses` won't make a Survivor run escalate — the loot patch and boss hook are quest-class-gated, not just counter-gated.

---

### T12 — Sector jump edge cases (regression)

**Goal:** Confirm Phase 5's sector-jump gating still works, now under the tightened **`Completed`-only** award rule.

> **Changed rule:** the Forge Meter and `DifficultyScalar` now award **only** when the departed sector's objective is `Completed`. Leaving a sector in `Started` (mission abandoned mid-jump), `Failed`, or `NoObjective` awards nothing. Previously only `Failed` was blocked, so abandoned/unfinished sectors still paid out — that was the bug this fixes.

**Steps:**
1. Leave a sector whose objective is **Failed**. `!forgemeter` unchanged, `!difficulty` shows `DifficultyScalar` unchanged. Log line: `objective Failed (not Completed) — meter award withheld`.
2. Leave a sector whose objective is still **Started** (jump away before finishing the mission). Same result — **no meter, no scalar** (this is the specific case that used to wrongly pay out; verify it now withholds). Log line: `objective Started (not Completed) — …`.
3. Leave a **Completed** sector. Meter +20 and scalar +1 (scalar only if escalation is active). This is the positive control.
4. Bounce between two sectors (A → B → A). Each sector only pays out once per run (Phase 5 dedup). Confirm scalar/meter don't double-count.
5. Interdiction: get interdicted mid-jump, resume. Only ONE award per successful arrival at a new sector.
6. Leaving the starting hub-adjacent sector at run start: no meter award AND no scalar tick.

**Pass criteria:** Only `Completed` sectors award meter/scalar; `Started`/`Failed`/`NoObjective` all withhold. Phase 5 dedup and start-zone gating remain intact.

---

### T13 — Multiplayer determinism (2-client basic smoke test)

**Goal:** Confirm the loot reshape is consistent across host and clients.

**Setup:** Host a session, invite a second player, both have the mod.

**Steps:**
1. Both clients: `!difficulty` at run start. Should match (both `DifficultyScalar=0, BossesDefeated=0, DORMANT`).
2. Force activation: `!setbosses 2` on host — but note this only affects host's local state (Phase 8 sync is deferred). Confirm the divergence via `!difficulty` on the client.
3. **Known limitation to verify:** loot spawns are host-authoritative, so the client will see whatever the host reshaped. Density scaling is host-only in effect. HP scaling is client-local but eventually consistent via Photon stat sync.
4. On the host, kill an enemy — the client sees the same relic drop (host-authoritative spawn).

**Pass criteria:** Host's authoritative decisions replicate cleanly. Local counter divergence between clients is expected (Phase 8 will fix); is not a Phase 6 bug.

---

### T14 — Config tuning

**Goal:** Confirm all six escalation configs actually affect behavior.

**Steps for each config key:**
1. Edit `BepInEx/config/void.crew.terminus.cfg`. Change the value. Reload the game (or use ScriptEngine hot-reload if set up).
2. Run through a small test scenario that would trigger the config's path.
3. Verify the new value takes effect (e.g., changing `EscalationBossActivationThreshold = 0` should skip warm-up entirely).

**Specific quick tests:**
- `EscalationBossActivationThreshold = 0` → escalation active from run start.
- `EscalationBossActivationThreshold = 5` → warm-up takes 5 bosses.
- `EscalationDensityScalarPerJump = 0` → density scaling off, HP/damage still on.
- `EscalationStatScalarPerJump = 0.20` → HP/damage same rate as density.
- `EscalationRareUnlockScalar = 0` → Rares always drop (scalar ceiling never restricts Rare).
- `EscalationLegendaryUnlockScalar = 3` → Legendaries drop earlier.

**Pass criteria:** Every config value changes behavior as documented.

---

## Known limitations (do NOT fail QA on these)

These are intentional gaps documented in the design and TODO. Report them if they matter, but they aren't Phase 6 regressions:

1. **Boss HP is not excluded from scaling.** Boss enemies get the same `+scalar × 0.05` HP boost as regular Hollows. Linking a spawned ship to its ObjectiveData boss reference needs another pre-flight pass. At default rates, a scalar-6 boss is +30% HP — meaningful but not blocking.
2. **Survivor waves are not scaled.** Only `AIDirector` spawns are affected. `WavesSpawnManager` (Survivor mode) uses author-defined wave counts and its own difficulty progression.
3. **DifficultyScalar and BossesDefeated are not synced across clients.** Each client tracks its own counters. Late joiners will see stale values. Deferred to Phase 8.
4. **Loot reshape is host-authoritative but client-side visibility is via Photon sync only.** Non-host clients' `!lootdump` may show pre-reshape lists if the sector setup hasn't fully propagated.
5. **`AIDirector` intensity changes take effect for future spawn ticks only.** Spawners that were mid-cycle at the moment of activation might not immediately spike in count.

## What NOT to test in this pass

- Perk roll math (Phase 4). Covered separately.
- Multiplayer roll sync (Phase 8). Not shipped.
- Cursed relics / Maintenance Burden (Phase 7). Not shipped.
- Signature perks (Phase 7). Not shipped.
- Save format / cross-run persistence. Never planned — mod is per-run only.

## Reporting

For each scenario:
- **Pass** — record the observed values that matched expectations.
- **Fail** — capture `!difficulty` + `!lootdump` output at the failure point, plus a screen recording of the encounter if visual (density / damage feel bugs).
- **Cannot verify** — note what's blocking (missing config, quest type never encountered, etc.).

Attach the BepInEx log (`BepInEx/LogOutput.log`) — Phase 6 code emits `[Forge]` / `[Escalation]` info-level lines for activation, boss defeat, sector reshape, and errors.

## Files under test

For reference — the code exercised by this test plan:

**Feature code**
- `VoidCrewTerminus/Features/Escalation/*` — escalation state + activation gate + enemy scaling helpers
- `VoidCrewTerminus/Features/Loot/*` — loot reshape algorithm
- `VoidCrewTerminus/Features/Forge/ForgeMeterController.cs` — DifficultyScalar owner, sector-jump increment host

**Patches**
- `VoidCrewTerminus/Patches/BossDefeatHook.cs` — boss objective detection (gated on `is EndlessQuest`), activation trigger
- `VoidCrewTerminus/Patches/ForgeSectorHook.cs` — sector-exit meter + scalar increment; **awards only on `ObjectiveState.Completed`**
- `VoidCrewTerminus/Patches/LootTableEscalationPatch.cs` — loot reshape hook; **`EndlessQuest`-only, not gated by the activation threshold** (boss-count-driven from sector 1)
- `VoidCrewTerminus/Patches/EnemyDensityPatch.cs` — 4× `AIDirector` intensity prefixes **+ `Spawner.InitSpawner` postfix** (raises the ceiling — the density fix); emits `[Escalation] Density …` / `Spawner intensity …`; all capped by `EscalationScalarCap`
- `VoidCrewTerminus/Patches/EnemyStatScalingPatch.cs` — HP + damage postfixes; capped by `EscalationScalarCap`

**Commands**
- `VoidCrewTerminus/Commands/SectorEscalationCommands.cs` — `!difficulty`, `!setdifficulty`, `!setbosses`, `!lootdump`, `!spawners`

**Automated tests**
- `VoidCrewTerminus.Tests/SectorEscalationTests.cs`
- `VoidCrewTerminus.Tests/EnemyScalingTests.cs`
