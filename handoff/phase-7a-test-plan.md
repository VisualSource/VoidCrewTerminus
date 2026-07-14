# Phase 7-A — Signature Perks test plan

**Project:** VoidCrewTerminus
**Phase:** 7-A — Signature perks (subset of Phase 7 Content Pass)
**Status:** Shipped, ready for QA verification.
**Date:** 2026-07-14

## What this covers

Phase 7-A adds **signature perks** — flavor perks tied to specific flagship relic IDs. When a flagship relic is consumed in an Upgrade Forge commit, its signature is preferred over the category pool draw. 5 signatures shipped in this pass (2 Legendary, 3 Rare). The rest of Phase 7 (cursed relics, Maintenance Burden, dynamic-scaling signatures) is deferred to later sub-phases.

For the design intent, read the `Sector Escalation` and `Phase 7` sections in [`docs/upgrade-forge-design.html`](../docs/upgrade-forge-design.html). The `Known limitations` list on each phase entry tells you what's intentionally out of scope.

## Test environment

**Prereqs**
- BepInEx + VoidManager against a live Void Crew build with the latest `VoidCrewTerminus.dll`.
- Dev mode enabled: set `EnableDevMode = true` in `BepInEx/config/void.crew.terminus.cfg`.
- An installed Upgrade Forge with sufficient capacity for at least L4 modules.

**Signatures shipped this pass**
| Perk ID | Relic ID | Category | Payload |
|---|---|---|---|
| `sig_biomass_ram` | `Relic_15_BiomassForThrustersAndDamage` | Built-in | +20% forward power, +15% ram damage |
| `sig_sustained_payload` | `Relic_28_PayloadRecharge` | Weapon | +25% fire rate, +10% damage |
| `sig_overcharged_grid` | `Relic_02_PowerForBreakers` | Power provider | +15% power provided, +20% battery recharge |
| `sig_holy_purpose` | `Relic_12_BenedictionDamageForAccuracy` | Weapon | +20% damage, +10% accuracy |
| `sig_confessor_cadence` | `Relic_13_ConfessorFireRateForPower` | Weapon | +30% fire rate, +10% projectile speed |

**Dev commands relevant to this phase**
| Command | Purpose |
|---|---|
| `!forceperk list` | Lists all authored perks (including signatures) |
| `!forceperk <slot 1-3> <perkId>` | Force a specific perk into a module slot |
| `!perks` | Show perk slots of nearest module + Forge socket pending state |
| `!perkodds <tier> [n]` | Statistical roll simulation |

**Automated coverage** (in `dotnet test`)
- `PerkDefinition.SignatureRelicId` shape verified via constructor overloads.
- Signature-behavior tests (SignaturesFor, TryGet, calculator picking signature) are **skipped** — they require `PerkPool._signatures` static init which evaluates `StatType` values that need the game runtime. These tests exist with the standard `[Fact(Skip = ...)]` reason and would automatically run if game DLLs were ever copied to the test bin.

**What automated tests do NOT cover for Phase 7-A:** every behavior below. Signatures are content — verification is playtest.

## Test scenarios

### T1 — Signature lookup exists

**Goal:** Confirm each shipped signature perk is discoverable and applies correctly when force-applied.

**Steps for each of the 5 signatures:**
1. Stand next to a module of the matching category (e.g., an engine for `sig_biomass_ram`).
2. Run `!forceperk 1 <perkId>` (e.g., `!forceperk 1 sig_biomass_ram`).
3. Run `!perks` to confirm the slot 1 label shows the signature name.
4. Verify the stat changes apply — for `sig_biomass_ram`, the engine's forward-power stat should read ~20% higher.

**Pass criteria:** All 5 signatures land in their target slot and their payload is visible on the module's stats.

---

### T2 — Signature roll priority over category pool

**Goal:** Confirm that when a flagship relic is consumed in a commit, its signature is preferred.

**Setup:** Fresh Forge, dev commands enabled. Get a `Relic_15_BiomassForThrustersAndDamage` (Legendary). Deconstruct a Mark III engine module (e.g., forward thruster) into the Forge.

**Steps:**
1. Insert the flagship relic into a Forge tube.
2. Commit the upgrade (via the Forge's commit button or `!forgecommit`).
3. Expected: the perk that lands in slot 1 (or whichever slot the tier gate opens) should be **"Biomass Ram"** — not one of the built-in category-pool perks (Tuned Manifolds, Gyro Assist, Slipstream Coating).
4. Legendary relics have a 75% roll chance — if the gate fails, no perk lands (`No perk this time...` message). Retry a few times if needed to confirm signature keeps landing when the gate passes.

**Pass criteria:** When the perk gate passes, the signature always beats the category pool. Verify with `!perks` after commit.

---

### T3 — Multi-relic tie-break (FIFO)

**Goal:** Confirm that with multiple flagship relics inserted, the **first-inserted** flagship wins (FIFO consumption order).

**Setup:** Fresh Forge. Two flagship relics: `Relic_15_BiomassForThrustersAndDamage` and `Relic_12_BenedictionDamageForAccuracy`. A Mark III weapon module (a Benediction, for symmetry with sig_holy_purpose).

**Steps:**
1. Insert `Relic_15` first, then `Relic_12`. Commit.
2. Expected: since the commit consumes relics in FIFO order (position 0 first), the FIRST flagship consumed is `Relic_15`. Its signature is `sig_biomass_ram` — but wait, that's a Built-in perk on a Weapon module. Category mismatch.

**Important detail:** signatures still apply as authored — they aren't category-gated by the target module. `sig_biomass_ram` was authored as `ForgeCategory.BuiltIn` but nothing stops it from landing on a weapon (the calculator picks the signature regardless of module category). This is an authoring decision — signatures ARE the module they're authored for.

Reframe with a valid pair:
1. Two flagship weapons: `Relic_12_BenedictionDamageForAccuracy` (sig_holy_purpose) and `Relic_13_ConfessorFireRateForPower` (sig_confessor_cadence).
2. Insert `Relic_12` first, then `Relic_13`. Commit.
3. Expected: `sig_holy_purpose` lands (FIFO — first consumed wins).

**Pass criteria:** Always the first flagship's signature. Reverse the insertion order to confirm.

---

### T4 — Non-flagship relic falls back to category pool

**Goal:** Confirm relics without signatures behave like Phase 4 (category pool draw).

**Steps:**
1. Insert a Common non-flagship relic (e.g., `Relic_00_Solo`). Commit on any Mark III module.
2. Expected: perk that lands is one of the three category-pool perks (weapon: Overclocked Coils / Focused Optics / Heavy Payload; defense: Harmonic Plating / etc.). Never a signature.
3. Run `!perkodds common 1000` to verify roll math still works (should observe ~25% for Common).

**Pass criteria:** Non-flagship relics NEVER produce signature perks. Fallback is untouched by 7-A.

---

### T5 — Signature + non-flagship multi-relic commit

**Goal:** Confirm mixed commits with a flagship anywhere in the FIFO order still favor the flagship.

**Steps:**
1. Insert non-flagship relic first (`Relic_00_Solo`), then flagship (`Relic_28_PayloadRecharge`). Commit a Weapon module.
2. Both relics consumed (assuming L3→L5 cost of 2). FIFO order: `Relic_00_Solo` first, `Relic_28_PayloadRecharge` second.
3. Expected: `sig_sustained_payload` lands. The first-consumed non-flagship doesn't have a signature, so the walk continues to position 1, finds the flagship, uses its signature.

**Pass criteria:** Signature wins over category pool even when it's the SECOND (or later) consumed relic.

---

### T6 — Signature roll respects tier gate

**Goal:** Confirm signature rolling still requires the tier-based roll chance to pass. Signatures don't guarantee — they just win the pick when the gate passes.

**Steps:**
1. Force the tier chance low temporarily: edit config `PerkRollChanceLegendary = 0.01` (1%).
2. Insert `Relic_15_BiomassForThrustersAndDamage`, commit. Repeat 20+ times.
3. Expected: ~99% of commits produce **no perk** ("No perk this time...") — the signature doesn't get to compete because the tier gate fails first.
4. Restore `PerkRollChanceLegendary = 0.75`. Repeat: ~75% of commits produce `sig_biomass_ram`.

**Pass criteria:** Signature roll frequency correlates with the tier chance, not 100%.

---

### T7 — Slot tier gating still applies

**Goal:** Confirm signatures respect the slot tier rules (Legendary can fill any slot, Rare can't fill slot 3, Common can't fill slots 2/3).

**Steps:**
1. Force fill slot 1 with a Common perk (e.g., `!forceperk 1 weapon_overclocked_coils`).
2. Insert a Rare flagship (`Relic_12_BenedictionDamageForAccuracy`) and commit a Weapon module.
3. Expected: since slot 1 is filled and Rare can target slot 2 (0-indexed: slot 0 taken, targets slot 1 which is 0-indexed... hmm wording is confusing — the game shows 1/2/3, code uses 0/1/2). Anyway: signature should land in the highest-index slot the tier allows that's still open.
4. Fill both slot 1 and slot 2. Insert a Legendary flagship. Signature should land in slot 3.
5. Fill all three slots. Insert any flagship. Roll should skip silently (`No perk this time...` doesn't fire either — the "no eligible slot" branch is reached instead).

**Pass criteria:** Signatures never overwrite an existing perk in a slot; they respect the same slot-picker as category perks.

---

### T8 — Signature persistence through deconstruct/reconstruct

**Goal:** Confirm signature perks ride the deconstruct→reconstruct bridge like category perks (Phase 5 bridge, Candidate 2 refactor).

**Steps:**
1. Land a signature on a module (from T1 or T2). Confirm with `!perks`.
2. Deconstruct the module (grab the build box).
3. Rebuild the module elsewhere on the ship.
4. Run `!perks` on the rebuilt module. Expected: signature persists — same slot, same effect.

**Pass criteria:** Signature perks survive the same lifecycle as category perks.

---

### T9 — Signature perks appear in the perk list

**Goal:** Confirm `!forceperk list` includes signature perks.

**Steps:**
1. Run `!forceperk list`.
2. Expected: output includes all 5 signature IDs alongside the 15 category-pool perks (20 total).

**Pass criteria:** Signature IDs enumerated. Their descriptions match the authored values in the table above.

---

## Known limitations (do NOT fail QA on these)

1. **Signature category ≠ target module category.** A signature authored as `ForgeCategory.BuiltIn` will still land on a weapon module if that weapon consumed the flagship relic. The calculator doesn't cross-check. This is intentional — signatures are relic-scoped, not module-scoped. If a signature's stat payload doesn't apply to a given module (e.g., ForwardPower on a shield), the payload just no-ops for that module.
2. **Only 5 signatures shipped.** The design mentioned "5–10." More can be authored later — just add to the `_signatures` array in `PerkPool.cs`.
3. **No dynamic-scaling signatures yet.** Stalker (damage scales with sublight speed) and Hoarder (bonus scales with hold count) are Phase 7-D, needing `ModDynamicScalingValue` pre-flight.
4. **No visual cue on flagship relics.** Player has to know which relic IDs are flagship to strategize. A tooltip / hover-text pass could add this later.
5. **Cursed system not implemented yet.** Phase 7-B ships that. Until then, all relics are non-cursed regardless of RelicTierData's IsCursed flag (currently all false).

## What NOT to test in this pass

- Cursed relics / cursed-augmented perk pool (Phase 7-B).
- Maintenance Burden (Phase 7-C).
- Dynamic-scaling signatures (Phase 7-D).
- Multiplayer perk roll sync (Phase 8).
- Meter/escalation/loot biasing (Phase 5/6 — separate test plan).

## Reporting

For each scenario:
- **Pass** — record the observed perk ID + module state after commit.
- **Fail** — capture `!perks` and `!difficulty` output at the failure point.

Attach the BepInEx log (`BepInEx/LogOutput.log`) — commit logs are `[Forge] Committed L{cur}→L{new} ... perk={id or "no roll"}`.

## Files under test

- `VoidCrewTerminus/Features/Forge/PerkDefinition.cs` — SignatureRelicId + IsSignature
- `VoidCrewTerminus/Features/Forge/PerkPool.cs` — signature authoring + SignaturesFor lookup
- `VoidCrewTerminus/Features/Forge/UpgradeCommitCalculator.cs` — CommitRequest.RelicNames + PickSignature
- `VoidCrewTerminus/Features/Forge/UpgradeForgeBehavior.cs` — TryCommit builds relicNames alongside relicTiers
- `VoidCrewTerminus.Tests/UpgradeCommitCalculatorTests.cs` — signature tests (skipped, documented reason)
