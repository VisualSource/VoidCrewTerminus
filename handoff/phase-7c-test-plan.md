# Phase 7-C — Maintenance Burden (Random Shutoff) test plan

**Project:** VoidCrewTerminus
**Phase:** 7-C — Maintenance Burden system with the first burden type: `RandomShutoff`
**Status:** Shipped. Cursed relics now have gameplay teeth.
**Date:** 2026-07-14

## What this covers

Phase 7-C completes the cursed loop. When a commit consumes a cursed relic, an **independent roll** decides whether the target module also picks up a Maintenance Burden. The burden runs as a MonoBehaviour on the module; the first (and currently only) burden type is `RandomShutoff` — periodically calls `CellModule.TurnOff()` for a few seconds, then `TurnOn()`. Gameplay tension is operational, not statistical: the module still hits its damage/defense targets, it just goes dark at inconvenient moments.

**Design departure from the doc:** the original design had cursed relics roll from a "cursed-augmented perk pool" (a separate authored list). This is **not** how 7-C ships. Perk selection is unchanged from Phase 7-A (signature → category pool). Cursed relics only affect whether a burden attaches, not which perk lands. Perk and burden are orthogonal outcomes.

For scaffolding + spawn-time cursed roll, see [phase-7b-test-plan.md](phase-7b-test-plan.md). For signatures, see [phase-7a-test-plan.md](phase-7a-test-plan.md).

## Test environment

**Prereqs**
- BepInEx + VoidManager against a live Void Crew build with the latest `VoidCrewTerminus.dll`.
- Dev mode enabled: `EnableDevMode = true`.
- **Escalation must be active** (2+ bosses defeated) OR use `!setbosses 2` to force activation. Without activation, no cursed relics spawn (7-B gate).

**Config knobs relevant to this phase** (under `[forge]`)
| Key | Default | Purpose |
|---|---|---|
| `BurdenApplicationChance` | `0.75` | Chance a successful commit consuming ≥1 cursed relic attaches a burden ("high chance" per design intent; raised from the original 0.50) |
| `BurdenShutoffMinSeconds` | `2` | RandomShutoff: min seconds a shutoff event lasts |
| `BurdenShutoffMaxSeconds` | `4` | RandomShutoff: max seconds a shutoff event lasts |
| `BurdenIntervalMinSeconds` | `30` | RandomShutoff: min seconds between shutoff events |
| `BurdenIntervalMaxSeconds` | `90` | RandomShutoff: max seconds between shutoff events |
| `EscalationBossActivationThreshold` | `2` | Bosses required for cursed spawn + burden roll to activate |

**Dev commands added this phase**
| Command | Purpose |
|---|---|
| `!listburdens` | List every module carrying a Maintenance Burden with scheduling state |
| `!triggerburden` | Force every idle burden's next event to fire immediately |
| (also useful) `!perks`, `!cursedstatus`, `!difficulty`, `!setbosses` | Context from other phases |

**Automated coverage** (in `dotnet test`)
- Calculator burden roll: cursed + chance passes → applies baked burden; cursed + chance fails → `None`; no cursed relics → `None`; leftover cursed relics ignored; FIFO tie-break when multiple cursed relics consumed.
- `ForgeSnapshot`: `Burdens` starts empty; `WithBurdenAdded` idempotent per-type; different types stack; `WithLevel`/`WithPerk` preserve burdens; `Create` dedups and drops `None`.
- Not covered by automation: the actual `RandomShutoffBehavior` MonoBehaviour cycle, `CellModule.TurnOff/On` integration, tag stamping via `BuildMods`, spawn-time burden bake (all need game runtime). All verified by playtest below.

**Two-stage curse flow (Phase 7-C shipped design)**

1. **Spawn time (host, in-world).** When a relic spawns, `CursedRelicSpawnPatch` rolls cursed status per `CursedRelicRoll.ChanceFor(entry, scalar, ...)`. If the roll passes, the specific burden type is ALSO picked at that moment — uniformly from the relic's `RelicTierEntry.BurdenAffinity`. Both flag AND burden type are stamped onto the relic via `CursedRelicMarker.MarkCursed(go, burden)`. Immutable for that relic instance.
2. **Commit time (calculator).** For each consumed relic, read the baked burden from its marker (`None` if not cursed). Walk FIFO; the first consumed relic with a non-`None` burden wins. If the application-chance roll (`BurdenApplicationChance = 0.75` default) passes, that specific burden gets applied to the module.

Today all 29 relic entries use the default affinity `[RandomShutoff]` — the only shipped burden type. So every cursed relic bakes `RandomShutoff` at spawn. When 7-D and future burden types (`HeatTick`, `ManualReset`) land, per-relic affinity overrides will encode lore fit:
| Relic lore | Suggested affinity (when authored) |
|---|---|
| Breakers (`_02`, `_06`, `_09`, `_19`, `_24`) | `[RandomShutoff]` |
| Defects (`_11_A`, `_11_B`) | `[ManualReset]` |
| Temperature / Biomass (`_18`, `_15`) | `[HeatTick]` |
| Vulnerability (`_03`, `_16`) | `[RandomShutoff, HeatTick]` (uniform pick at spawn) |
| Generic Common | `[RandomShutoff]` (default) |

## Test scenarios

### T1 — Burden attaches on cursed-relic commit (roll passes)

**Goal:** Confirm that when a cursed relic is consumed and the burden roll passes, the module gets `RandomShutoff`.

**Setup:** Force escalation active. Get a cursed relic via `!forcecursed on` on a spawned relic. Boost `BurdenApplicationChance = 1.0` temporarily so the roll always passes for deterministic testing.

**Steps:**
1. Deconstruct a Mark III module into the Forge. Insert the cursed relic. Commit.
2. Rebuild the module.
3. Run `!listburdens`. Expected: the module appears with `RandomShutoff` and a countdown to next shutoff (`Xs until next shutoff`).
4. Run `!perks`. Expected: whatever perk landed (or none) is unchanged from vanilla Phase 7-A behavior — cursed did not affect perk selection.

**Pass criteria:** Cursed commit → burden appears. Perk selection is independent.

---

### T2 — Burden roll can fail

**Goal:** Confirm `BurdenApplicationChance` is honored — not every cursed commit attaches a burden.

**Setup:** Set `BurdenApplicationChance = 0.0`. Force cursed relic, commit.

**Steps:**
1. Commit with a cursed relic. `!listburdens` should show no new burden.
2. Set `BurdenApplicationChance = 0.5`. Repeat the commit 6–8 times on different modules.
3. Expected: roughly half get burdens (noisy small-sample; check trend, not exact ratio).

**Pass criteria:** Zero chance → never burdens. Nonzero chance → statistically consistent frequency.

---

### T3 — Non-cursed commit never burdens

**Goal:** Confirm the burden roll only fires when a cursed relic is consumed.

**Steps:**
1. `BurdenApplicationChance = 1.0`. Commit a Mark III module with only non-cursed relics.
2. `!listburdens` — the module should NOT appear.

**Pass criteria:** Non-cursed commits never attach burdens, regardless of chance config.

---

### T4 — Leftover cursed relics don't count

**Goal:** Confirm only CONSUMED relics factor into the burden roll — cursed leftovers in the Forge that weren't spent don't trigger it.

**Setup:** `BurdenApplicationChance = 1.0`. Get 3 relics: 2 non-cursed Common + 1 cursed Legendary.

**Steps:**
1. At Forge L3, insert all 3 relics into the Forge (2 Commons at positions 0-1, cursed Legendary at position 2). Commit.
2. Expected: greedy cost walk consumes 2 relics (L3→L4 costs 1, L4→L5 costs 1) — the Legendary at position 2 is a leftover.
3. `!listburdens` — module should NOT be burdened. The consumed relics were both non-cursed.
4. Now commit again with the leftover Legendary alone. Now the cursed relic IS consumed. `!listburdens` — module IS burdened.

**Pass criteria:** Only consumed relics count. Consumption order = FIFO (same rule as Phase 7-A signature order).

---

### T4b — Burden type respects per-relic affinity (forward-looking)

**Goal:** Prep verification for when more burden types exist. Today all cursed relics have `BurdenAffinity = [RandomShutoff]`, so this test currently only asserts the default. When 7-D or later ships a second burden type, this test should be extended.

**Steps (today):**
1. Force any cursed relic. Commit. If the burden roll passes, verify `!listburdens` shows `RandomShutoff` (the only shipped option).

**Steps (future, once a second burden type ships):**
1. Author a specific relic (e.g., `Relic_18_PowerForBiomassTemperature`) with `BurdenAffinity = [HeatTick]`.
2. Force that relic cursed. Commit. Verify `!listburdens` shows `HeatTick`, never `RandomShutoff`.
3. Force a relic with `BurdenAffinity = [RandomShutoff, HeatTick]`. Commit repeatedly. Verify both types appear with roughly equal frequency across many commits.

**Pass criteria (today):** All cursed commits that fire a burden produce `RandomShutoff`. Data-model prep is invisible until 7-D. **Pass criteria (future):** Affinity mapping steers the burden pick as authored.

---

### T5 — RandomShutoff visibly toggles the module

**Goal:** Confirm the burden actually powers the module off during shutoff windows.

**Setup:** Burdened module (from T1). Shorten timing for observation: `BurdenIntervalMinSeconds = 5`, `BurdenIntervalMaxSeconds = 5`, `BurdenShutoffMinSeconds = 2`, `BurdenShutoffMaxSeconds = 2`.

**Steps:**
1. Reboot the game (config change requires restart or hot-reload).
2. Rebuild a burdened module (or apply a new burden per T1).
3. Watch the module for ~10 seconds.
4. Expected: every 5 seconds, the module powers off for exactly 2 seconds. A notification fires: `"{name} powered down."` then `"{name} restored."`. In-game: any lights on the module go dark, if it's a weapon it stops firing, if it's a power provider its power output drops.
5. Run `!listburdens` mid-shutoff — should show `SHUT OFF (Xs until recovery)`.

**Pass criteria:** Shutoff visibly changes the module's operational state. Notifications match. `TurnOff`/`TurnOn` integrate with the game's `PowerDrain.IsOn` system.

---

### T6 — `!triggerburden` fires shutoffs on demand

**Goal:** Confirm the dev command bypasses the interval timer.

**Setup:** Burdened module in idle state (not currently shut off; `!listburdens` shows `Xs until next shutoff`).

**Steps:**
1. Run `!triggerburden`. Expected: `"Triggered next shutoff on 1 module(s)."`.
2. Within one frame: module powers off, notification fires.
3. Run `!triggerburden` again while the module is already shut off. Expected: `"No idle burdens to trigger (all are already shut off or absent)."`.

**Pass criteria:** Idle burdens fire on-demand. Already-shutoff burdens are skipped.

---

### T7 — Burden persists through deconstruct/reconstruct

**Goal:** Confirm the burden rides the snapshot bridge like level + perks.

**Steps:**
1. Burdened module in the world. Verify via `!listburdens`.
2. Deconstruct the module.
3. Rebuild it elsewhere on the ship.
4. `!listburdens` on the rebuilt module. Expected: same burden still attached, timer resumes from scratch (the seeded RNG is deterministic per ViewID — but ViewID may change on rebuild; that's fine, still deterministic within that instance's lifetime).

**Pass criteria:** Burden survives deconstruct/reconstruct. `MaintenanceBurdenBehavior` component reattaches on rebuild via `ForgeModuleState.ApplySnapshot`.

---

### T8 — Multiple burden TYPES stack (forward-looking, mostly a no-op today)

**Goal:** Confirm the data model can carry multiple distinct burden types. Only `RandomShutoff` exists in this phase, so this test is a placeholder to verify the code path.

**Steps:**
1. Land a `RandomShutoff` burden on a module.
2. Attempt to add another `RandomShutoff` (via commit + roll). `!listburdens` should still show ONE `RandomShutoff` (idempotent).
3. Later burden types (`HeatTick`, `ManualReset`) will stack additively — a module carrying all three would show three entries in `!listburdens`.

**Pass criteria:** Same-type re-application is idempotent. Model supports stacking (will be verified when 2nd burden type ships).

---

### T9 — Burden is not removable in v1

**Goal:** Confirm there's no way to remove a burden once applied (design constraint — "no cleansing" was called out).

**Steps:**
1. Burdened module in world.
2. Try to "cleanse" via any known means: dying and restarting run (this resets everything, so does clear the burden — but only via full run reset). No in-run mechanism should remove it.

**Pass criteria:** Only per-run reset clears burdens. No in-run removal path exists.

---

### T10 — Burden tag stamped for game-visible queries

**Goal:** Confirm the `Burden_RandomShutoff` CsTag is on the module's `LocalTags`, enabling future mod-side or vanilla filters to query "is this burdened?"

**Steps:**
1. Burdened module.
2. Run `!dumptags` on the module (from `ForgeDevCommands`). Expected: `Burden_RandomShutoff` appears in the runtime-tags list alongside `Forge_Upgraded`.

**Pass criteria:** The tag stamp mechanism (zero-value marker StatMod with `TagsToAdd`) works for burdens the same way it works for `Forge_Upgraded`.

---

### T11 — Perk selection is unaffected

**Goal:** Confirm cursed relics do NOT change the perk pool. This is the critical divergence from the design doc.

**Steps:**
1. Force a cursed non-flagship relic (e.g., cursed `Relic_00_Solo`). Commit on a Weapon module.
2. Expected: the perk that rolls (if the tier gate passes) is one of the Weapon category-pool perks. Never a cursed variant, never a signature (Relic_00 has no signature).
3. Repeat with a cursed flagship (`Relic_15_BiomassForThrustersAndDamage`). Expected: signature `sig_biomass_ram` rolls as usual (unaffected by cursed).
4. In both cases, whether burden attaches is decided separately by the burden roll.

**Pass criteria:** Perk selection identical to Phase 7-A with or without cursed status. Only burden roll cares about cursed.

---

### T12 — Per-run reset clears burdens

**Goal:** Confirm burdens don't leak between runs.

**Steps:**
1. Burdened module in current run.
2. Die or return to hub, then start a new Endless run.
3. Any surviving modules should have zero burdens (they'll be freshly-reset since `ForgeStateStore.ClearAll` fires on new session).

**Pass criteria:** Fresh runs start clean of all forge overlay state including burdens.

---

## Known limitations (do NOT fail QA on these)

1. **No cursed-augmented perk pool.** Design doc's original intent is deliberately not shipped. Cursed relics only affect burdens, not perks. This is a design refinement, not a bug.
2. **Only one burden type shipped.** `RandomShutoff` is the flagship burden. `HeatTick`, `ManualReset`, and any other burden types are future work — model supports them but they're not authored.
3. **Multiplayer sync of burden state is deferred to Phase 8.** `AppliedBurden` is computed by the host during commit; the resulting snapshot is host-authoritative. Clients see the snapshot via the existing ForgeStateStore mechanism (host-only), so if a client is running the game logic locally, their `ForgeModuleState` may diverge. The `MaintenanceBurdenBehavior` MonoBehaviour uses a deterministic seed (ViewID) so all clients converge on the same shutoff schedule IF they all locally attach the behavior — but attachment happens through `ForgeModuleState.ApplySnapshot`, which is currently host-driven.
4. **No visual indication that a module is burdened** beyond the notifications on shutoff/recovery. A subtle visual cue (glow, icon) is a follow-up.
5. **No "cleanse" mechanic.** Design constraint; not a limitation of the code.
6. **Timing seed source.** The `RandomShutoffBehavior` seed is `module.photonView.ViewID`. If a module is deconstructed and rebuilt, the new instance gets a new ViewID, so the shutoff schedule "reshuffles" on rebuild. Acceptable for MVP.
7. **`CellModule.TurnOff` semantics vary by subclass.** Some module types may have subclass-specific `EnterStateOff` implementations that do more than power down. Should be fine, but any weirdness (e.g., destructive shutoffs) would surface here.

## What NOT to test in this pass

- Phase 7-D dynamic-scaling signatures (Stalker/Hoarder — not shipped).
- Additional burden types beyond `RandomShutoff` (not shipped).
- Multiplayer sync of `AppliedBurden` / `ForgeModuleState.Burdens` (Phase 8).

## Reporting

For each scenario:
- **Pass** — record `!listburdens` output at key checkpoints + any notification messages that fired.
- **Fail** — capture `!listburdens`, `!perks`, `!cursedstatus` at the failure point, plus a screen recording of a shutoff event if the visible behavior didn't match.

Attach the BepInEx log — burden events log at info-level: `[Forge] Committed L{cur}→L{new} ...` includes the perk outcome; burden notifications go through the same `Messaging.Notification` pipeline.

## Files under test

**Feature code**
- `VoidCrewTerminus/Features/Forge/BurdenType.cs` — enum
- `VoidCrewTerminus/Features/Forge/ForgeSnapshot.cs` — `Burdens` field + `WithBurdenAdded` builder
- `VoidCrewTerminus/Features/Forge/ForgeModuleState.cs` — snapshot round-trip for burdens + burden tag markers + MonoBehaviour attach in `SyncBurdenBehaviors`
- `VoidCrewTerminus/Features/Forge/UpgradeCommitCalculator.cs` — `AppliedBurden` field + `RollBurden` logic (gathers affinity union, uniform pick)
- `VoidCrewTerminus/Features/Forge/UpgradeForgeBehavior.cs` — `TryCommit` applies burden to snapshot
- `VoidCrewTerminus/Features/Forge/Burdens/MaintenanceBurdenBehavior.cs` — base MonoBehaviour
- `VoidCrewTerminus/Features/Forge/Burdens/RandomShutoffBehavior.cs` — the actual shutoff cycle
- `VoidCrewTerminus/Features/Loot/RelicTierData.cs` — `RelicTierEntry.BurdenAffinity: IReadOnlyList<BurdenType>` with `[RandomShutoff]` default
- `VoidCrewTerminus/Utils/CsTagRegistry.cs` — `BurdenRandomShutoff` tag + `BurdenTagFor` helper

**Commands**
- `VoidCrewTerminus/Commands/BurdenCommands.cs` — `!listburdens`, `!triggerburden`

**Automated tests**
- `VoidCrewTerminus.Tests/ForgeSnapshotTests.cs` — burden snapshot behavior
- `VoidCrewTerminus.Tests/UpgradeCommitCalculatorTests.cs` — burden roll logic
