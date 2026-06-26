# Handoff — Upgrade Forge implementation

**Project:** VoidCrewTerminus · branch `features/module-lvls`
**Status:** Design complete, plan finalized, implementation **not yet authorized** by user.
**Date:** 2026-06-26

## What this is

A BepInEx Harmony mod for Void Crew (Unity, .NET 4.7.2) using VoidManager middleware. The session designed an "Upgrade Forge" system that extends ship module progression from L3 to L10 via a mod-side overlay, with roguelike mechanics (tiered relics, cursed perks, sector escalation). See [CLAUDE.md](../CLAUDE.md) for project layout.

## Read these first (do not re-derive)

| Artifact | Path (relative to repo root) | What it contains |
|---|---|---|
| Design doc (HTML) | [docs/upgrade-forge-design.html](../docs/upgrade-forge-design.html) | **The authoritative design.** Self-contained HTML page matching the style of existing `docs/*.html` guides. Contains glossary, system summary, cost curve, file map, networking flow, **9-phase implementation plan**, pre-flight steps, per-phase test plans. |
| TODO file | [TODO](../TODO) | Seed wishlist that originally prompted the feature ("levels 4-10 use relics to upgrade"). |
| CLAUDE.md | [CLAUDE.md](../CLAUDE.md) | Project-level guidance: build commands, key files, architecture, mod setup checklist. |

The design HTML is the source of truth. Do not re-design — verify, then execute the phased plan.

> Note: a plan-mode transcript also exists on the originating machine under the user's local Claude config dir (`~/.claude/plans/`), but its content has been lifted verbatim into the design HTML. The HTML is canonical; the plan file is not required to start work.

## Hard constraints

1. **The game may not be installed on the implementing system.** When it isn't, only the reference-only `Assembly-CSharp.dll` from the NuGet global packages folder is available (under `voidcrew.gamelibs/1.3.0/lib/` — the NuGet cache root is platform-dependent: `~/.nuget/packages` on Linux/macOS, `%USERPROFILE%\.nuget\packages` on Windows). Method bodies are stubs. Signature-only patches are fine; anything requiring IL inspection must wait for a live install + `ilspycmd`.
2. **Implementation is gated on explicit user go-ahead.** The user paused at the end of plan mode and said "hold off on starting implementation." Confirm explicitly before writing any code outside `docs/`, `CONTEXT.md`, and `docs/adr/`.
3. **Plan mode was active during the design session.** It is now exited (the design HTML and this handoff were written outside the plan file). When implementation begins, the user may or may not re-enter plan mode — follow whatever mode is active.

## Next-agent task list (in order)

1. **Read the design HTML.** Open `docs/upgrade-forge-design.html` and read the full document, paying special attention to the **Phased Implementation Plan** section. Sanity-check the architecture: mod-side overlay via `ConditionalWeakTable<CellModule, ForgeModuleState>`, host-arbitrated rolls via VoidManager `ModMessage`, Photon PUN under the hood. If anything looks off, raise it with the user before proceeding.

2. **Pre-flight verification** (requires live game install — coordinate with user). Run the `ilspycmd` decompile listed in the "Pre-Flight Verification" section of the design doc. Confirm the six signatures listed there. Update the doc if reality differs. Phases 2, 5, 6, and 8 each need pre-flight info — doing one up-front decompile pass is most efficient.

3. **Wait for explicit user authorization before writing code.**

4. **Execute the phased plan, one phase at a time.** The design HTML defines 9 phases (0–8). Do not skip phases or interleave them. After each phase:
   - Run that phase's test plan (in the HTML).
   - Confirm with the user before starting the next phase.
   - `dotnet build -c Release` should remain green throughout.

   **The phases at a glance** (full detail in the HTML):
   - **Phase 0** — Foundation: `CONTEXT.md`, ADR-0001, config scaffolding. No runtime change.
   - **Phase 1** — Relic Tiering: `Forge/RelicTierData.cs` + `!relictier` dev command.
   - **Phase 2** — Module Overlay + Stat Application: `Forge/ForgeModuleState.cs`, `ForgeOverlayTable.cs`, `Patches/ModuleStatOverlayPatch.cs`. *Pre-flight needed.*
   - **Phase 3** — Upgrade Forge MVP: Forge prefab + interaction + cost curve enforcement.
   - **Phase 4** — Perks: roll math, slot tier-gating, category pools.
   - **Phase 5** — Forge Progression: Meter + Sector Hook + Alloy Terminal. *Pre-flight needed.*
   - **Phase 6** — Sector Escalation: enemy + loot scaling. *Pre-flight needed.*
   - **Phase 7** — Content Pass: signatures, cursed flags, Maintenance Burden.
   - **Phase 8** — Multiplayer Sync + Edge Cases. *Pre-flight needed.*

   Phases 1 and 2 are parallel-safe (no inter-dependency). Everything else is strictly sequential.

## Session context that may not be obvious

- The user explored ~15 design questions via the `grill-with-docs` skill. Their answers consistently chose nuanced options over presets — they push back when a preset doesn't fit. Default to offering options + a recommendation, accept their refinements.
- The user reframed "module fragility" from "stat damage" to "operational quirks (random shutoffs, maintenance burdens)" — this is captured as **Maintenance Burden** in the glossary. Important nuance: cursed perks do NOT take stats away, they just make the module annoying to operate.
- The user wants the Forge to be **investment-bound**: passive sector progression alone won't get it to L10 — alloy spending is required. This is encoded as the **Forge Meter** with two parallel fill sources.
- The user prefers HTML docs in `docs/` (matches existing convention: `gamelibs-guide.html`, `harmony-patch-guide.html`, etc.) rather than Markdown.
- The session repeatedly skipped TodoWrite reminders because the plan file was the canonical tracker. If you find TodoWrite useful for the implementation work, use it — but it's not required.

## Suggested skills

- **grill-with-docs** — only if the user wants to re-open any design decision. The full grilling has already happened; reuse the resolved decisions in the design doc.
- **diagnose** — when patches fail at runtime (Harmony will silently fail on signature mismatches against the reference DLL; the diagnose skill's reproduce → minimise → instrument loop is the right tool).
- **tdd** — for the pure-logic pieces in Phases 1, 2, and 4 (`ForgeModuleState`, `PerkPool`, roll math). These are testable headlessly without the Unity runtime.
- **prototype** — only if the Forge UI design (relic insertion UX, alloy terminal) needs exploration before committing. Likely overkill; the design doc already specifies a Fabricator-style terminal pattern.
- **pr-helper** — when ready to open a PR off `features/module-lvls`. Consider one PR per phase (or per logical group of phases) so each can be reviewed and validated independently.
- **review** / **security-review** — before merge.

## Open items the user did not finalize

These are explicitly deferred to implementation and don't block kickoff:

- Specific perk roster (~15 category-pool + ~5–10 signatures). Author during content pass.
- Forge prefab visual design and footprint.
- Specific stat-mod target methods per module subclass — requires live-decompile.
- Maintenance Burden event taxonomy (the seed is "random shutoffs"; specifics come with cursed perk authoring).
- Whether the Forge surfaces a UI panel or stays purely diegetic for relic insertion.

## What NOT to do

- Do not start implementation without user authorization.
- Do not modify game data files (ScriptableObjects, AssetBundles in the game install). The overlay is mod-side only.
- Do not introduce new in-game resources beyond relics and alloys. The plan deliberately uses existing currencies.
- Do not extend the Fabricator's tier system to 10 — that was a rejected approach in favor of the overlay. (The Plan agent suggested it; user explicitly chose overlay.)
- Do not add cross-run persistence. The user picked per-run-only progression; no save format, no meta-progression.

## No sensitive info in this handoff

This document and all referenced artifacts contain no API keys, credentials, or PII.
