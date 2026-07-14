# Phase 6 — Sector Escalation test plan

**Project:** VoidCrewTerminus
**Phase:** 6 — Sector Escalation (loot tier biasing + enemy density + HP + damage + activation gate)
**Status:** Shipped, ready for QA verification.
**Date:** 2026-07-14

## What this covers

Phase 6 introduces two counters (`DifficultyScalar`, `BossesDefeated`) and four escalation systems (loot bias, enemy density, enemy HP, enemy damage), all gated behind a boss-defeat activation threshold. This plan verifies each system independently and then the whole thing end-to-end.

For the design intent and implementation details, read [`docs/upgrade-forge-design.html`](../docs/upgrade-forge-design.html) — the `Sector Escalation` and `Phase 6` sections are the authoritative reference. The `Known limitations` list at the bottom of that section tells you what's intentionally out of scope for QA.

## Test environment

**Prereqs**
- BepInEx + VoidManager installed against a live Void Crew build.
- Latest `VoidCrewTerminus.dll` deployed to `BepInEx/plugins/`.
- Dev mode enabled: set `EnableDevMode = true` in `BepInEx/config/void.crew.terminus.cfg`.

**Config knobs relevant to this phase** (all under `[forge]` in the config file)
| Key | Default | Purpose |
|---|---|---|
| `EscalationBossActivationThreshold` | `2` | Boss defeats required before any escalation activates |
| `EscalationRareUnlockScalar` | `3` | Below this scalar, Rares get downgraded to Common |
| `EscalationLegendaryUnlockScalar` | `6` | Below this scalar, Legendaries get downgraded to Rare |
| `EscalationBossScalarBonus` | `1` | Scalar bump per boss defeat (post-activation) |
| `EscalationDensityScalarPerJump` | `0.20` | Density multiplier per scalar tick (primary axis) |
| `EscalationStatScalarPerJump` | `0.05` | HP + damage multiplier per scalar tick (minor axis) |

**Dev commands available**
| Command | Purpose |
|---|---|
| `!difficulty` | Prints `ACTIVE`/`DORMANT` status + current scalar + boss count |
| `!setdifficulty <n>` | Force `DifficultyScalar` to a value |
| `!setbosses <n>` | Force `BossesDefeated` to a value |
| `!lootdump` | Dump current sector's per-rarity loot pool (post-reshape) with tier counts |
| `!forgemeter` | Show Forge Meter progression |
| `!setmeter <v>` / `!setforgelevel <n>` | Direct meter/level manipulation |

**Automated coverage** (already passing in `dotnet test`)
- Pure algorithms: `EnemyScaling.ScaleIntensity`, `SectorEscalation.MaxAllowedTier`, `SectorEscalation.DowngradeRelics`.
- Faction membership helpers.
- Activation gate: `IsScalingActive` true/false thresholds, `ResetForRun` clears state.
- **What automated tests do NOT cover:** anything that touches Unity runtime — StatMod attachment (HP scaling), `DestroyableComponent.CalculateRawDamage` interception (damage scaling), the actual `AIDirector` intensity mutations, in-game loot spawns. These need in-game verification.

## Test scenarios

### T1 — Activation gate: warm-up dormancy

**Goal:** Confirm nothing scales until the boss threshold is crossed.

**Setup:** Fresh Endless run. Install an Upgrade Forge on the ship (use `!forgespawn` if needed).

**Steps:**
1. Run `!difficulty` at run start. Expected: `Escalation DORMANT — DifficultyScalar=0, BossesDefeated=0/2, max relic tier: Common`.
2. Jump through 3–5 sectors, completing objectives normally. **Do not fight boss sectors yet.**
3. After each jump, run `!difficulty`. Expected: `DifficultyScalar` stays at 0 (does NOT tick up during warm-up). `BossesDefeated` stays at 0.
4. Run `!lootdump` in one of the sectors. Expected: pool passes through untouched (whatever the quest designer put in shows up as-is). Any Rare/Legendary relics that would normally drop still do.
5. Enter combat in one of these sectors. Enemies should feel vanilla — no visibly-tougher spawns, no HP bloat, no extra damage taken.

**Pass criteria:** No scaling visible anywhere in the warm-up period.

---

### T2 — Activation gate: threshold-crossing boss defeat

**Goal:** Confirm the 2nd boss defeat activates escalation without itself contributing to scalar.

**Setup:** Fresh Endless run, Forge installed.

**Steps:**
1. Find and complete a boss objective (Endless quest boss sector). Expected: silent — no on-screen notification, `!difficulty` shows `DifficultyScalar=0, BossesDefeated=1/2, DORMANT (needs 1 more boss defeat)`.
2. Find and complete a second boss objective. Expected: on-screen notification **"Boss defeated — the Forge stirs to life. Escalation is now active."** After this, `!difficulty` reports `Escalation ACTIVE — DifficultyScalar=0, BossesDefeated=2/2`.
3. **Critical check:** After boss #2, `DifficultyScalar` is still **0**, not 1 or 2. The threshold-crossing defeat does not itself bump scalar — only post-activation events do.

**Pass criteria:** Silent boss #1, "Escalation is now active" message on boss #2, scalar still 0 after boss #2.

---

### T3 — Activation gate: post-activation ramp

**Goal:** Confirm scalar starts ticking on the FIRST post-activation event.

**Setup:** Continue from T2 (2 bosses defeated, scalar=0, activation just fired).

**Steps:**
1. Jump to the next sector. Run `!difficulty`. Expected: `DifficultyScalar=1` — the sector jump bumped it.
2. Complete another boss objective. Expected: on-screen notification **"Boss defeated — the Forge unlocks Legendary-tier relics."** (Third boss = already at Legendary ceiling from boss ceiling, but this is the first *post-activation* boss so the tier-unlock message shows.) `!difficulty` reports `DifficultyScalar=2` (bumped by both the jump above and the boss).
3. Jump another sector. Expected: `DifficultyScalar=3`.

**Pass criteria:** Every post-activation sector exit and boss defeat bumps scalar by 1 each.

---

### T4 — Loot tier biasing: sector reshape

**Goal:** Confirm sector loot lists get reshaped once escalation is active.

**Setup:** Continue from T3 or use `!setdifficulty 6` + `!setbosses 2` to force post-activation state.

**Steps:**
1. Enter a sector known to contain relic drops (check with `!lootdump` in a vanilla sector first to identify one).
2. Run `!lootdump`. Expected output resembles:
   `[Common] total=8, Common=3, Rare=2, Legendary=0, non-relic=3` (numbers vary by quest, but the tier counts should be present).
3. At `DifficultyScalar=0` (use `!setdifficulty 0`), re-enter a sector and `!lootdump` — Legendary count should drop to 0 (all downgraded to Rare) but only if Rare candidates existed in the pool; otherwise those slots drop entirely.
4. At `DifficultyScalar=6`, re-enter a sector — nothing gets downgraded (scalar hits Legendary ceiling naturally).
5. Kill enemies; verify actual dropped relics match the reshaped pool's tier distribution. Sample size: destroy 10–20 loot droppers.

**Pass criteria:**
- Reshape occurs (`!lootdump` shows downgraded distribution vs vanilla).
- Actual drops match the reshaped list.
- Same sector re-entered gives the same reshape (determinism — `quest.Seed + sector.Id` seeds both the shuffle and our downgrade).

---

### T5 — Loot tier biasing: boss ceiling overrides scalar ceiling

**Goal:** Confirm `MaxAllowedTier = max(scalarCeiling, bossCeiling)` behavior.

**Setup:** `!setdifficulty 0` (scalar ceiling = Common), `!setbosses 1` (boss ceiling = Rare).

**Steps:**
1. Run `!difficulty`. Expected: `max relic tier: Rare` (max of Common from scalar, Rare from boss).
2. Enter a sector, `!lootdump`. Expected: Legendaries downgrade to Rare (ceiling), Rares stay, Commons stay.
3. `!setbosses 2` (boss ceiling = Legendary), `!lootdump` again. Expected: nothing downgrades — Legendary is the ceiling.

**Pass criteria:** Boss count promotes the ceiling above whatever scalar dictates.

---

### T6 — Density scaling: spawner intensity

**Goal:** Confirm `AIDirector` spawners produce more enemies when scalar is high.

**Setup:** Post-activation state. Compare two runs (or the same enemy encounter at different scalars).

**Steps:**
1. `!setdifficulty 0` (density scaling effectively off — no scalar means no bump). Trigger an enemy encounter (find a hostile POI or Hollows patrol). Count the number of spawned enemies over ~60 seconds.
2. `!setdifficulty 6` (density +120%). Trigger the same type of encounter. Count spawns over the same window.
3. Compare counts. Expected: scalar-6 encounter has roughly 2.2× the enemies (rounded up per scenario intensity value).

**Pass criteria:** Visibly more enemies at high scalar. Density affects the general `AIDirector` path, not Survivor waves.

**Negative case:** Start a Survivor-mode quest. Wave counts should NOT be affected by scalar (Survivor uses `WavesSpawnManager`, not `AIDirector`, and is intentionally left alone).

---

### T7 — HP scaling: enemy MaxHitPoints

**Goal:** Confirm hostile-faction enemies get an HP boost proportional to scalar.

**Setup:** Post-activation state. Vanilla scaling for baseline comparison.

**Steps:**
1. `!setdifficulty 0`. Engage a Hollows enemy of known type (e.g., a Fighter). Note approximately how many hits from a standard weapon kill it.
2. `!setdifficulty 6`. Engage the same enemy type. Expected: ~30% more effective HP (5 hits → ~6-7 hits).
3. Also check wildlife (space whale, drones) — these should NOT be scaled (wildlife faction ≠ Hollows/Remnant).

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
4. **Now install a Forge.** Enemies immediately face the current scalar's density/HP/damage boost. Player experiences +5% enemy stats etc. Loot in the next sector reshapes per current scalar.

**Pass criteria:** No meter without Forge. Counters tick regardless. Installing a Forge mid-run applies the accumulated intensity.

---

### T10 — Per-run reset

**Goal:** Confirm all Phase 6 state resets between runs.

**Setup:** Existing run with high scalar / high boss count (use T3 or T9).

**Steps:**
1. Note current `!difficulty` and `!forgemeter` state.
2. Die or return to hub, then start a new Endless run.
3. Run `!difficulty` at the start of the new run. Expected: `DifficultyScalar=0, BossesDefeated=0/2, DORMANT`.
4. Run `!forgemeter`. Expected: Meter=0, Forge Level=1.
5. `!lootdump` in the first sector. Expected: no downgrading (activation dormant).

**Pass criteria:** All counters, meter, and Forge level reset to zero/baseline on each new run.

---

### T11 — Non-Endless quest types

**Goal:** Confirm quest types that don't have bosses behave gracefully.

**Steps:**
1. Start a Pilgrimage quest. Complete sectors normally.
2. Run `!difficulty`. Expected: `DORMANT` forever (no bosses can be defeated in Pilgrimage, so the threshold is never crossed).
3. Loot pools and enemy stats should stay vanilla for the whole quest.
4. Try Survivor quest. Same expectation — bosses aren't a thing in Survivor, so escalation stays off.

**Pass criteria:** Non-Endless quests never activate escalation.

**Note:** If you want to test the escalation systems' effect *without* clearing bosses, use `!setbosses 2` in any quest type to force activation for playtest.

---

### T12 — Sector jump edge cases (regression)

**Goal:** Confirm Phase 5's sector-jump gating still works.

**Steps:**
1. Complete a sector with a failed objective. `!difficulty` should show `DifficultyScalar` unchanged (Phase 5 refuses meter award AND — by our activation gate — refuses scalar tick too).
2. Bounce between two sectors (go A → B → A). Each sector only pays out once per run (Phase 5 dedup). Confirm scalar doesn't double-count.
3. Interdiction: get interdicted mid-jump, resume. Only ONE scalar tick per successful arrival at a new sector.
4. Leaving the starting hub-adjacent sector at run start: no meter award (Phase 5) AND no scalar tick.

**Pass criteria:** All Phase 5 gating remains intact under Phase 6 scalar logic.

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
- `VoidCrewTerminus/Patches/BossDefeatHook.cs` — boss objective detection, activation trigger
- `VoidCrewTerminus/Patches/ForgeSectorHook.cs` — sector-exit meter + scalar increment
- `VoidCrewTerminus/Patches/LootTableEscalationPatch.cs` — loot reshape hook
- `VoidCrewTerminus/Patches/EnemyDensityPatch.cs` — 4× `AIDirector` intensity prefixes
- `VoidCrewTerminus/Patches/EnemyStatScalingPatch.cs` — HP + damage postfixes

**Commands**
- `VoidCrewTerminus/Commands/SectorEscalationCommands.cs` — `!difficulty`, `!setdifficulty`, `!setbosses`, `!lootdump`

**Automated tests**
- `VoidCrewTerminus.Tests/SectorEscalationTests.cs`
- `VoidCrewTerminus.Tests/EnemyScalingTests.cs`
