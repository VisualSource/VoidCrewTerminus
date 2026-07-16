# Phase 7-B — Per-instance Cursed Relics (scaffold) test plan

**Project:** VoidCrewTerminus
**Phase:** 7-B — Per-instance cursed relic infrastructure (subset of Phase 7 Content Pass)
**Status:** Scaffold shipped. **No gameplay effect yet** — cursed flag exists on relic GameObjects but is not consumed by the commit/perk pipeline. Phase 7-C will wire it into a cursed-augmented perk pool and Maintenance Burden.
**Date:** 2026-07-14

## What this covers

Phase 7-B lays the plumbing for per-instance cursed relics without any gameplay consequences. Verification confirms:
- Relics spawn with a per-instance cursed flag based on a probabilistic roll
- Chance formula combines base chance + per-relic modifier + escalation scalar bonus
- Cursed status only rolls when escalation is active (post-boss-threshold)
- Cursed relics are inspectable / manipulable via dev commands
- Commit pipeline sees the cursed flag on inserted relics (via `CommitRequest.RelicIsCursed[]`) but does nothing with it yet

**This is scaffolding.** Playtest verification here is limited to "does the flag propagate correctly?" — not "does cursed feel bad?" (that's 7-C).

## Test environment

**Prereqs**
- BepInEx + VoidManager against a live Void Crew build with the latest `VoidCrewTerminus.dll`.
- Dev mode enabled: `EnableDevMode = true`.
- **Escalation must be active** (2+ bosses defeated) OR use `!setbosses 2` to force activation. Cursed rolls are gated on `SectorEscalation.IsScalingActive` — with default config no cursed relics will spawn during warm-up.

**Config knobs relevant to this phase** (under `[forge]`)
| Key | Default | Purpose |
|---|---|---|
| `RelicBaseCurseChance` | `0.15` | Base chance any relic is cursed at spawn |
| `EscalationCurseChancePerScalar` | `0.03` | Additional cursed chance per `DifficultyScalar` tick |
| `EscalationBossActivationThreshold` | `2` | Cursed rolls gated on this (same gate as other escalation) |

**Per-relic modifiers** (baked into `RelicTierData`)
| Relic ID pattern | Modifier | Reason |
|---|---|---|
| `Relic_00_Solo` | `-0.05` | Safe single-axis buff |
| `Relic_02_PowerForBreakers` | `+0.10` | Breakers = instability |
| `Relic_03_VulnerabilityDuringVoidCharge` | `+0.10` | Vulnerability lore |
| `Relic_06_FireRateForBreakersCount` | `+0.10` | Breakers |
| `Relic_11_A_*` / `Relic_11_B_*` (defects) | `+0.10` | Defects |
| `Relic_16_VulnerabilityForAlloyReducedSpeed` | `+0.10` | Vulnerability |
| `Relic_19_ShieldRechargeForBreakers` | `+0.10` | Breakers |
| `Relic_20_PowerHungry` | `+0.10` | Instability lore |
| `Relic_24_EnergyDamageForBreakersCount` | `+0.10` | Breakers |
| `Relic_09_ScoopForBreakersCount` | `+0.05` | Milder breakers |
| `Relic_18_PowerForBiomassTemperature` | `+0.05` | Temperature drift |
| `Relic_15_BiomassForThrustersAndDamage` | `+0.15` | Legendary flagship |
| `Relic_28_PayloadRecharge` | `+0.15` | Legendary flagship |
| Everything else | `0.00` | Neutral |

**Dev commands added this phase**
| Command | Purpose |
|---|---|
| `!cursedstatus` | List 5 nearest relics with tier, cursed flag, and computed chance breakdown |
| `!forcecursed <on\|off>` | Toggle cursed on the nearest relic |
| `!difficulty`, `!setbosses <n>`, `!setdifficulty <n>` | Existing Phase 6 commands — use to trigger escalation for cursed testing |

**Automated coverage** (in `dotnet test`)
- 11 tests on `CursedRelicRoll` pure math: dormant returns 0, active sums correctly, negative modifiers reduce chance, both clamps, negative scalar treated as 0, `ShouldBeCursed` threshold behavior.
- No automated coverage of: `CursedRelicSpawnPatch` (scene-integrated Harmony hook), `CursedRelicMarker` (Unity component), dev commands (require live game). All verified by playtest below.

## Test scenarios

### T1 — Dormant escalation blocks all cursed rolls

**Goal:** Confirm no relics spawn cursed during the warm-up period.

**Setup:** Fresh Endless run. **Do not defeat any bosses.** Kill some enemies to trigger loot drops.

**Steps:**
1. Kill 5–10 enemies that drop relics.
2. For each spawned relic, run `!cursedstatus` and confirm chance = 0% and cursed flag = false.
3. Cross-check with `!difficulty` — should show `DORMANT (needs 2 more boss defeats)`.

**Pass criteria:** No cursed relics regardless of how many spawn during warm-up.

---

### T2 — Chance formula shape

**Goal:** Confirm the chance breakdown (`base + relic_mod + scalar*bonus`) reported by `!cursedstatus` matches the config.

**Setup:** Force escalation active. `!setbosses 2` and `!setdifficulty 3`.

**Steps:**
1. Kill enemies until 5+ relics are on screen with different `Relic_*` IDs (mix of Common with `-0.05` modifier, Rare with `+0.10`, Legendary with `+0.15`).
2. Run `!cursedstatus`. Expected output format per relic:
   `Relic_02_PowerForBreakers: Rare — chance would be 34.0% (base 15%, relic +0.10, scalar +9.0%)`
   - base `15%` matches `RelicBaseCurseChance = 0.15`
   - `relic +0.10` matches the modifier from RelicTierData
   - `scalar +9.0%` = `scalar (3) × EscalationCurseChancePerScalar (0.03)`
   - total = 34% ✓
3. Change `!setdifficulty 10`. Rerun `!cursedstatus`. Expected: scalar bonus jumps to `+30.0%`, totals push toward 55%+ for lore-cursed relics.

**Pass criteria:** Every relic's chance breakdown is arithmetically consistent with the config.

---

### T3 — Per-relic modifier drives spawn distribution

**Goal:** Verify high-modifier relics get cursed more often than low-modifier ones over many spawns.

**Setup:** Force escalation active. Push cursed base chance high enough that visible effects appear: `!setdifficulty 6`. Optionally boost `RelicBaseCurseChance` in config to `0.30` for faster observation.

**Steps:**
1. Grind ~30–50 relic drops. Note which ones spawn cursed via `!cursedstatus` on each.
2. Tally by relic ID.
3. Expected: Legendary relics (`Relic_15_*`, `Relic_28_*`, `+0.15` modifier) should be cursed at a noticeably higher rate than `Relic_00_Solo` (`-0.05` modifier).

**Pass criteria:** Distribution roughly matches the per-relic modifier table. Small sample sizes will be noisy — chi-squared instinct only.

---

### T4 — Idempotent spawn scan

**Goal:** Confirm a relic doesn't get re-rolled if multiple enemies drop loot simultaneously (or if `SpawnLootInCurrentPosition` fires multiple times in the same frame).

**Setup:** Force escalation active.

**Steps:**
1. Kill a dense cluster of enemies at once (2–3+ dying same frame).
2. For each resulting relic, run `!cursedstatus` twice in a row.
3. Cursed state should be **stable** on the same relic across both calls — no flip-flopping.
4. Move the relics around, come back later, re-check — status should still match.

**Pass criteria:** Once decided at spawn, cursed status never changes for that relic instance. `CursedProcessedMarker` sentinel prevents re-rolling.

---

### T5 — `!forcecursed` dev override

**Goal:** Confirm the dev command can toggle cursed status on demand.

**Steps:**
1. Find a non-cursed relic near the player.
2. Run `!forcecursed on`. Confirm with `!cursedstatus` — flag flipped to CURSED.
3. Run `!forcecursed off`. Confirm — flag cleared.
4. Toggle a few times to verify idempotency.

**Pass criteria:** Nearest relic's cursed flag reflects the command.

---

### T6 — Commit sees cursed flag but ignores it

**Goal:** Confirm `UpgradeForgeBehavior.TryCommit` builds `CommitRequest.RelicIsCursed[]` correctly from marker components, but the calculator doesn't act on it yet.

**Setup:** Get a cursed relic (`!forcecursed on` on a spawned relic) and a non-cursed relic. Have a Mark III module deconstructed into the Forge.

**Steps:**
1. Insert both relics into the Forge (cursed one at position 0, non-cursed at position 1).
2. Commit the upgrade.
3. Expected: commit succeeds normally. Perk roll is unaffected — the perk that lands is either category-pool or signature (per Phase 4/7-A), NOT a "cursed-augmented" variant.
4. Check the BepInEx log — should see the standard commit line `[Forge] Committed Lx→Ly ... perk={id}`. No cursed-related log noise.

**Pass criteria:** Commit completes with normal perk selection. Cursed flag is invisible to gameplay.

---

### T7 — Cursed status not lost across scene reloads (within a run)

**Goal:** Confirm the marker component survives whatever the relic's normal lifecycle throws at it.

**Setup:** Cursed relic exists (from T5 or a natural spawn).

**Steps:**
1. Note the cursed relic's location via `!cursedstatus`.
2. Move around the ship. Do a sector jump.
3. Return to the relic. Run `!cursedstatus` again.
4. Expected: cursed flag still present.
5. Pick up the relic, put it down. Cursed still present.

**Pass criteria:** Cursed marker survives sector jumps, pickup/drop, and any other in-run scene events. (End of run naturally resets everything as usual.)

---

### T8 — Per-run reset

**Goal:** Confirm cursed state doesn't leak across runs.

**Steps:**
1. Have several cursed relics in the current run.
2. Die or return to hub, then start a new Endless run.
3. In the new run, spawn relics — none should be cursed until you cross the boss threshold again.

**Pass criteria:** Fresh runs start clean — no cursed relics in the warm-up period, cursed state accumulates independently.

---

### T9 — Multiplayer (known limitation, verify expected behavior)

**Goal:** Document current MP behavior — cursed marker is HOST-ONLY. Clients don't see cursed status.

**Setup:** Host + 1 client, both with the mod.

**Steps:**
1. Host: force escalation active via `!setbosses 2`.
2. Kill an enemy that drops a relic in both clients' view.
3. Host: `!cursedstatus` — may report cursed for some relics.
4. Client: `!cursedstatus` — will report ALL relics as non-cursed (marker was added on host only, not synced).
5. Client picks up a cursed relic (per host's view). Commits an upgrade. `RelicIsCursed[]` in the commit request will be [false] on the client's side.

**Pass criteria:** This is the expected limitation for the 7-B scaffold. Since cursed has no gameplay effect yet, the divergence is harmless. Phase 7-C will need proper sync (probably via PhotonView instantiation data or an RPC on spawn).

## Known limitations (do NOT fail QA on these)

1. **No gameplay effect.** Cursed relics behave identically to non-cursed in Phase 7-B. Playtest cannot verify "does cursed feel like a curse?" — that's 7-C's job.
2. **Host-only marker.** Cursed status is not synced to clients. Deferred to 7-C or Phase 8 depending on which lands first.
3. **`FindObjectsOfType` scene walk.** The spawn patch does a full-scene scan of `CarryableObject`s. This is fine at low relic counts but could be optimized later if lots of relics are on screen.
4. **No visual indication.** Players cannot tell a cursed relic apart from a normal one without dev commands. Adding a visual hint (glow / suffix / hover text) is a follow-up.
5. **Cursed roll uses `UnityEngine.Random.value`.** Not seeded — same-relic re-rolls on a hot-reload would produce different results. This doesn't matter for the current scaffold but should be addressed if 7-C wants deterministic replay.

## What NOT to test in this pass

- Cursed-augmented perk pool (Phase 7-C).
- Maintenance Burden effects (Phase 7-C).
- Dynamic-scaling signatures (Phase 7-D).
- Multiplayer perk roll sync (Phase 8).

## Reporting

For each scenario:
- **Pass** — record chance breakdown for a few sample relics and log lines confirming spawn behavior.
- **Fail** — capture `!cursedstatus` output at the failure point + the BepInEx log line at the moment the relic spawned.

Attach the BepInEx log — spawn events emit `[Escalation] Relic {name} spawned CURSED (chance {n%})` at debug level.

## Files under test

**Feature code**
- `VoidCrewTerminus/Features/Loot/RelicTierData.cs` — `RelicTierEntry.BaseCurseChanceModifier` field and per-relic authoring
- `VoidCrewTerminus/Features/Loot/CursedRelicMarker.cs` — MonoBehaviour marker + static helpers
- `VoidCrewTerminus/Features/Loot/CursedRelicRoll.cs` — pure chance math (`ChanceFor`, `ShouldBeCursed`)

**Patches**
- `VoidCrewTerminus/Patches/CursedRelicSpawnPatch.cs` — postfix on `LootOnDeathDropper.SpawnLootInCurrentPosition`

**Commands**
- `VoidCrewTerminus/Commands/CursedRelicCommands.cs` — `!cursedstatus`, `!forcecursed`

**Calculator plumbing**
- `VoidCrewTerminus/Features/Forge/UpgradeCommitCalculator.cs` — `CommitRequest.RelicIsCursed[]`
- `VoidCrewTerminus/Features/Forge/UpgradeForgeBehavior.cs` — `TryCommit` reads markers

**Automated tests**
- `VoidCrewTerminus.Tests/CursedRelicRollTests.cs` — pure `ChanceFor` / `ShouldBeCursed` math
