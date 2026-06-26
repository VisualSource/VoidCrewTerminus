# Handoff — Leech Carrier Encounter implementation

**Project:** VoidCrewTerminus · branch `features/rfc-leach`
**Status:** Design complete, plan finalized, implementation **not yet authorized** by user.
**Date:** 2026-06-26

## What this is

A BepInEx Harmony mod for Void Crew (Unity, .NET 4.7.2) using VoidManager middleware. The session designed a new combat encounter — the **Leech Carrier** — where a vanilla enemy ship gets patched to fire a homing **Leech Missile** that, on impact with the player ship, deploys 1–3 small robotic parasites (**Leeches**) onto the hull. Players must EVA and use the existing multi-tool to remove them under a hold-to-remove interaction with containment prompts. The encounter is gated and scaled by the **`DifficultyScalar`** from the Upgrade Forge's `ForgeMeterController`. See [CLAUDE.md](../CLAUDE.md) for project layout.

## Read these first (do not re-derive)

| Artifact | Path (relative to repo root) | What it contains |
|---|---|---|
| **Design doc (HTML)** | [docs/leech-design.html](../docs/leech-design.html) | **The authoritative technical design.** Self-contained HTML page matching the style of `docs/upgrade-forge-design.html`. Contains glossary, mechanics, architecture, file map, patch surfaces, networking flow, **9-phase implementation plan**, pre-flight steps, per-phase test plans. |
| **RFC doc (HTML)** | [docs/leech-rfc.html](../docs/leech-rfc.html) | The proposal/intent-level companion. Use this to understand *why* — motivation, goals, sample encounter, rejected alternatives, future variants. |
| **Upgrade Forge design** | [docs/upgrade-forge-design.html](../docs/upgrade-forge-design.html) | **Prerequisite reading.** Defines `DifficultyScalar` on `ForgeMeterController`. The Leech encounter reads that scalar; Forge Phase 5 must be merged before Leech Phase 6 can be wired in. |
| **Upgrade Forge handoff** | [upgrade-forge-handoff.md](upgrade-forge-handoff.md) | Pattern reference for this handoff format; also describes shared pre-flight (decompile, multi-tool/multiplayer questions). |
| **TODO file** | [TODO](../TODO) | Seed wishlist. The Leech feature came from "lech — launched via missiles, attaches to ship and causes damage to ship until removed by a player like Star Wars buzz droids — small robotic spider." |
| **CLAUDE.md** | [CLAUDE.md](../CLAUDE.md) | Project-level guidance: build commands, key files, architecture, mod setup checklist. |

The design HTML is the source of truth. Do not re-design — verify, then execute the phased plan.

> Note: a plan-mode transcript also exists at the user's local Claude config dir (`~/.claude/plans/help-me-create-a-golden-twilight.md`), but its content has been lifted verbatim into the design HTML. The HTML is canonical.

## Hard constraints

1. **The game may not be installed on the implementing system.** When it isn't, only the reference-only `Assembly-CSharp.dll` from the NuGet global packages folder is available (under `voidcrew.gamelibs/1.3.0/lib/`). Method bodies are stubs. Signature-only patches are fine; anything requiring IL inspection must wait for a live install + `ilspycmd`. See the **Pre-Flight Verification** section in the design HTML for the full ilspycmd recipe.
2. **Implementation is gated on explicit user go-ahead.** Confirm explicitly before writing any code outside `docs/`, `handoff/CONTEXT.md`, and `handoff/`.
3. **The Leech feature depends on the Upgrade Forge.** Leech Phase 6 (Sector Escalation Scaling) cannot complete until **Upgrade Forge Phase 5 (Forge Progression: Meter + Sector Hook)** is merged. Leech Phases 0–5 can be implemented in parallel with Upgrade Forge work, provided `DifficultyScalar` is stubbed to a config-driven constant for early testing. Plan accordingly when sequencing the two features.
4. **This branch (`features/rfc-leach`) is separate from the Upgrade Forge branch (`features/module-lvls`).** Do not interleave commits between the two — merge the Forge work first, then rebase or merge that into the Leech branch before wiring Phase 6.

## Next-agent task list (in order)

1. **Read both HTML docs.** Start with [docs/leech-rfc.html](../docs/leech-rfc.html) for context, then [docs/leech-design.html](../docs/leech-design.html) for execution detail. Pay special attention to the **Phased Implementation Plan** and **Pre-Flight Verification** sections in the design doc.

2. **Verify the Upgrade Forge prerequisite.** Confirm with the user whether Upgrade Forge Phase 5 has been merged. If not, agree on whether to stub `DifficultyScalar` or wait. Do not start Leech Phase 6 without `ForgeMeterController.DifficultyScalar` being real.

3. **Pre-flight verification** (requires live game install — coordinate with user). Run the `ilspycmd` decompile commands listed in the design doc's Pre-Flight Verification section. Confirm the ten signatures listed there. Update the doc if reality differs. Leech Phases 1, 2, 3, 4, and 5 each need pre-flight info — doing one up-front decompile pass is most efficient.

4. **Wait for explicit user authorization before writing code.**

5. **Execute the phased plan, one phase at a time.** The design HTML defines 9 phases (0–8) plus a deferred Polish phase. Do not skip phases or interleave them. After each phase:
   - Run that phase's test plan (in the HTML).
   - Confirm with the user before starting the next phase.
   - `dotnet build -c Release` should remain green throughout.

   **The phases at a glance** (full detail in the HTML):
   - **Phase 0** — Foundation: config keys, `Leech/` directory, no runtime change.
   - **Phase 1** — Leech Missile projectile: homing, PD-interceptable. *Pre-flight needed (PD + projectile warning).*
   - **Phase 2** — Spawn on impact + Module-Biter (B-type only). *Pre-flight needed (module effectiveness getters — overlaps with Forge Phase 2).*
   - **Phase 3** — Hull-Biter (A-type) + variant assignment. *Pre-flight needed (hull section API + mesh raycasting).*
   - **Phase 4** — EVA Removal: multi-tool patch + hold state machine. *Pre-flight needed (multi-tool + EVA detection).*
   - **Phase 5** — Containment Failure + Flee. *Pre-flight needed (boot-panel UI reuse).*
   - **Phase 6** — Sector Escalation Scaling. *Depends on Upgrade Forge Phase 5.*
   - **Phase 7** — Multiplayer Sync + Edge Cases (all 7 ModMessage types).
   - **Phase 8** — Diegetic Audio + first-impact ship-wide sting.
   - **Phase: Polish** — deferred. Custom Leech model, IK rig (Animation Rigging package) for procedural spider-walk on fleeing leeches, custom missile model + VFX, bespoke audio.

   Phases are strictly sequential. Each builds on the previous.

## Session context that may not be obvious

- **The user spells it "lech" in their TODO and original prompt; the docs use "Leech" as canonical.** The proper-English spelling matches the parasitic-creature reference. If the user wants to standardize on "Lech" in code, that's a rename, not a redesign.
- **The user pushes back when a preset doesn't fit.** During the design grill, they overrode my recommendation in three places that materially improved the design: (1) added the "module destroyed → leech self-destructs" rule, (2) corrected my crew-awareness recommendation to use existing telescope/cockpit/turret external views instead of bespoke UI, (3) added the containment-failure-causes-escape mechanic with Unity IK locomotion (the IK piece is deferred to Polish; v1 uses a simple lerp). Default to offering options + a recommendation; accept their refinements.
- **The encounter is deliberately EVA-only.** Internal destruction was explicitly rejected for v1. Any future agent wondering "couldn't we let players shoot leeches from inside?" — that's a captured **future variant** (armored deep-sector leech subtype), not a v1 oversight.
- **The Module-Biter / Hull-Biter split is determined by position, not RNG.** Each leech anchor checks proximity to the nearest module. Within threshold → B-type. Outside → A-type. The user can angle the ship to land missiles on bare hull and earn Hull-Biters-only encounters as a flying skill expression.
- **Stacking rule for B-type debuffs is non-stacking, but module HP damage stacks.** Multiple B-types on one module apply the debuff once, but each ticks HP damage independently. This was an explicit design choice to keep effectiveness debuffs sane while preserving stacking-threat tension.
- **Concurrency safety rail is 8 concurrent leeches.** A missile impacting a saturated ship still plays VFX but spawns no leeches. The in-fiction excuse is "hull EM noise." This prevents runaway swarms from bricking a run.
- **One escape per leech.** A leech that has fled once cannot flee again on subsequent containment attempts. This guarantees encounter finite-ness.
- **All scaling values are starter values for playtest tuning.** The user explicitly noted they'll need to play the game to get them feeling right. Don't treat the scaling table as final.
- **The user prefers HTML docs in `docs/`** (matches existing convention: `upgrade-forge-rfc.html`, `upgrade-forge-design.html`, `gamelibs-guide.html`, etc.) and Markdown handoffs in `handoff/`.

## Shared pre-flight items with the Upgrade Forge

Several pre-flight decompile tasks overlap between the two features. Doing them once benefits both:

| Pre-flight item | Forge needs it for | Leech needs it for |
|---|---|---|
| Per-subclass module effectiveness / stat getters | Phase 2 (overlay stat application) | Phase 2 (B-type debuff multiplier) |
| `GameSessionSectorManager.OnSectorEntered` | Phase 5 (sector hook) | Phase 6 (read scalar via Forge) |
| VoidManager `ModMessage` transport semantics | Phase 8 (multiplayer) | Phase 7 (multiplayer) |
| `VoidManager.Events` host migration + late join | Phase 8 | Phase 7 |

Coordinate decompile work with whoever owns the Upgrade Forge implementation if it's a different agent.

## Suggested skills

- **grill-with-docs** — only if the user wants to re-open any design decision. The full grilling has already happened; resolved decisions are in the design doc.
- **diagnose** — when patches fail at runtime. Harmony will silently fail on signature mismatches against the reference DLL; the diagnose loop (reproduce → minimise → instrument) is the right tool.
- **tdd** — for the pure-logic pieces in Leech Phases 2, 3, 5, 6 (`LeechVariantAssigner`, `LeechEscalationScaling`, `LeechConcurrencyRail`, removal state machine). These are testable headlessly without the Unity runtime.
- **prototype** — only if the containment-prompt UX needs exploration before committing. The design says "reuse the boot-panel UI element" — if reuse looks wrong in the EVA HUD context, prototype both before authoring a bespoke one.
- **pr-helper** — when ready to open a PR off `features/rfc-leach`. Consider one PR per phase (or per logical group) so each can be reviewed and validated independently.
- **review** / **security-review** — before merge.

## Open items the user did not finalize

These are explicitly deferred to implementation and don't block kickoff:

- **Host enemy class identity.** Identified during pre-flight by surveying vanilla enemy ships available in scalar-2+ sectors.
- **Variant proximity threshold radius.** Starts at config default; tuned in playtest.
- **Boot-panel UI reuse vs bespoke containment prompts.** Decided in Phase 5 pre-flight.
- **First-impact sting trigger granularity.** Currently "per encounter." Might become "per missile" if it feels muddled in playtest.
- **Containment prompt input type.** Single-button tap by default; could escalate to directional or short sequence at deep scalar.
- **Self-destruct AOE damage.** Currently no AOE — graceful termination. Captured as a future variant.
- **Specific placeholder asset choices.** v1 uses runtime-generated primitives (capsule for missile, simple spider-box for leech). Polish phase replaces them.

## What NOT to do

- **Do not start implementation without user authorization.**
- **Do not modify game data files.** All overlay state lives in mod-side `MonoBehaviour`s and static dictionaries. No ScriptableObject edits, no asset-bundle changes to the game install.
- **Do not introduce internal destruction of leeches in v1.** Captured as a future variant; explicitly out of v1 scope. The encounter's identity depends on the EVA commitment.
- **Do not author a bespoke hull-status UI or alert popup.** Awareness composes from vanilla observation tools (telescope module, cockpit 3rd-person, gunner external view) and diegetic audio. The one global signal is the first-impact ship-wide hum — that's it.
- **Do not author a new equippable removal tool for v1.** Reuse the existing multi-tool. The dedicated tool is a future variant.
- **Do not make Leech Missile damage hull HP on impact.** That collapses the design into "another damage source." The payload is the leeches.
- **Do not stack B-type debuffs on the same module.** Non-stacking is a deliberate choice. Module HP damage does stack — that's the threat axis.
- **Do not implement custom IK locomotion in v1.** Fleeing leeches use a simple lerp. IK + Animation Rigging is Polish-phase work.
- **Do not start Leech Phase 6 before Upgrade Forge Phase 5 is merged** unless explicitly stubbing `DifficultyScalar`.
- **Do not amend the design doc to capture implementation findings** — those go in the per-phase commit messages or, if architectural, into a new ADR under `docs/adr/`.

## No sensitive info in this handoff

This document and all referenced artifacts contain no API keys, credentials, or PII.
