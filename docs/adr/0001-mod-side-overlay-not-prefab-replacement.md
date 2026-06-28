# ADR-0001: Mod-Side Overlay via ConditionalWeakTable, Not Prefab Replacement

**Status:** Accepted  
**Date:** 2026-06-27

## Context

Void Crew's vanilla module "upgrade" system is a BuildBox prefab swap: a handheld
`InteractiveCarryableModuleUpgrader` replaces a placed module with a different prefab.
There is no numeric `level` field on `CellModule` or any subclass. The only vanilla component
with a real numeric tier is the Fabricator (`FabricatorModule.currentTier`, capped at 3 by the
shipped ScriptableObject).

To extend modules from L3 to L10, two approaches were evaluated:

1. **Prefab replacement** — author 7 new prefabs per module type for L4–L10, slotted into
   the existing BuildBox swap chain alongside vanilla L1–L3 prefabs.

2. **Mod-side overlay** — track per-instance upgrade state in a
   `ConditionalWeakTable<CellModule, ForgeModuleState>` and apply stat modifications via
   Harmony postfixes on each module's stat-calculation methods.

## Decision

**Option 2 (mod-side overlay) was chosen.**

## Reasoning

- Prefab replacement requires authoring ~70+ new prefabs (7 levels × ~10 module categories
  minimum), extending the BuildBox data structures, and risks breaking vanilla L1–L3 transitions
  on any game update.
- The overlay approach leaves vanilla prefabs and BuildBox swap logic completely untouched.
  All new state lives in mod-side memory and resets cleanly on run end.
- Per-run-only semantics are enforced by clearing the `ConditionalWeakTable` on
  `VoidManager.Events.HostStartSession`, with no save format needed.

## Consequences

- The specific stat-getter/calculation methods to patch on each `CellModule` subclass must be
  identified via live `ilspycmd` decompile of the installed game DLL — the reference-only NuGet
  DLL has empty method bodies. This is recorded as a pre-flight requirement before Phase 2
  (Module Overlay + Stat Application).
- The overlay is transparent to vanilla code; players without the mod see unmodified behavior.
- Networking requires an explicit `ForgeStateSyncMessage` for late joiners, since overlay state
  is entirely mod-side and never touches Photon room properties.
