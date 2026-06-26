# VoidCrewTerminus

A BepInEx + Harmony mod for the Unity game Void Crew, using VoidManager as middleware. This glossary captures terms specific to the mod's design language — the systems it introduces and the in-fiction concepts it leans on.

## Language

### Upgrade Forge feature

**Upgrade Forge**:
A new installable ship module added by this mod. Holds relics in-world, performs upgrades on other modules, has its own level.
_Avoid_: "Workbench", "Forge" (without "Upgrade") in code identifiers — the noun is taken in too many other contexts.

**Forge Meter**:
A progression bar on the Upgrade Forge. Fills from sector progression (passive) and alloy spending at the Forge terminal (active). When full, the Forge gains a level.

**Forge Capacity**:
The number of relics the Upgrade Forge can hold simultaneously. Equal to its current level. Gates the maximum module-upgrade level the crew can attempt.

**Module Level**:
A per-`CellModule` integer in mod-side overlay state, range L3 (vanilla baseline) → L10. Resets on run end.
_Avoid_: "Tier" (reserved for relics).

**Relic Tier**:
The quality property added to existing game relics — Common / Rare / Legendary. A quality modifier, not a gate.
_Avoid_: "Rarity" (Tier is the canonical word in this codebase).

**Upgrade Quality**:
The per-level outcome modified by the relic tier used during an upgrade. Higher tier → bigger stat bump and higher perk-roll chance.

**Perk**:
A special modifier rolled at upgrade time. Authored content, drawn from category pools or signatures.

**Perk Slot**:
One of three positions on a module that can hold a Perk. Tier-gated: Slot 1 (any tier), Slot 2 (Rare+), Slot 3 (Legendary only).

**Category Pool**:
Default perk list per module category (weapons / reactors / shields / engines / scanners). Any relic of suitable tier can roll from it.

**Signature Perk**:
A perk tied to a specific relic identity. Only rolls when that exact relic is inserted.

**Cursed Relic**:
A relic whose perk pool includes downside-bearing perks (~30% of relics).

**Maintenance Burden**:
The operational quirk attached to a module by a cursed perk — random shutoffs, increased maintenance interactions, heat ticks. Not a stat penalty.
_Avoid_: "Debuff", "Penalty" — Burden is operational, not statistical.

**Sector Escalation**:
Per-jump scaling driven by `DifficultyScalar`. Each completed sector jump bumps enemy threat and shifts relic drop tiers toward higher rarities. Also gates and scales other mod features (e.g. the Leech Carrier encounter).

**DifficultyScalar**:
The integer counter on `ForgeMeterController`. Increments by +1 per successful sector jump. Used by Sector Escalation, the Leech Carrier encounter, and any future scalar-driven mod features.

### Leech Carrier encounter

**Leech Carrier (Host)**:
A vanilla enemy ship class patched to carry the Leech Missile weapon once `DifficultyScalar >= 2`. The specific class is identified pre-flight.
_Avoid_: "Carrier ship", "Mothership" — Leech Carrier is the canonical noun.

**Leech Missile**:
The deploying projectile fired by a Leech Carrier. Homes with a finite turn rate, killable by any weapon fire, engaged by vanilla point-defense with per-scalar HP resistance. Deals no hull damage on impact — the payload is the Leeches.

**Leech**:
A small robotic parasite that attaches to the player ship's hull when a Leech Missile impacts. Two deterministic variants by anchor location.
_Avoid_: "Lech" (used informally in TODO seed — Leech is the canonical English spelling in this codebase), "Bug", "Droid".

**Module-Biter** (B-type):
The default Leech variant. Anchors near a module, applies a non-stacking effectiveness debuff while attached, deals stacking module HP damage on a tick. Self-destructs if its targeted module is destroyed.

**Hull-Biter** (A-type):
The rarer Leech variant. Anchors on bare hull, deals damage to the hull section it's on via a visible "chomp" bite cadence. Vanilla breach mechanics handle consequences.

**Containment Failure**:
The event during EVA removal where the player misses a containment input prompt and the Leech escapes to a new hull location. Capped at one per Leech.
_Avoid_: "Escape" alone — Containment Failure is the named event; escape is its consequence.

**Hold-to-Remove**:
The 3–5 second EVA interaction where a crewmember aims the multi-tool at a Leech and holds activation. Periodic Containment Prompts test the hold.

**Containment Prompt**:
A brief input cue (Void Crew's existing boot-panel-style mini-prompt) shown during Hold-to-Remove. Player must respond within a window (1.0–1.5s) or trigger Containment Failure.

**Concurrency Safety Rail**:
The hard cap of 8 concurrent Leeches on the player ship. When hit, subsequent missile impacts trigger VFX but spawn no Leeches.
_Avoid_: "Leech cap" — the design name is Concurrency Safety Rail.

**Diegetic Awareness**:
The design principle that crew learn of Leeches through the game world (vanilla telescope, cockpit 3rd-person, gunner turret external views; 3D-positional audio through the hull) rather than via bespoke alert popups or hull-status UI.

### Shared

**Per-run state**:
State that lives only for the duration of one ship-lifespan. Resets to baseline on ship death. Both the Upgrade Forge overlay and Leech encounter state are per-run.
_Avoid_: "Session state" (session can span many runs).

**Host-arbitrated**:
The multiplayer pattern used throughout this mod. The Photon master client owns canonical state and all RNG; other clients sync via VoidManager `ModMessage` and replay host decisions.
_Avoid_: "Authoritative" alone — say "host-arbitrated" for clarity, since "authoritative" is ambiguous in netcode contexts.

**Pre-flight (verification)**:
Decompile-based confirmation of vanilla game internals required before a Harmony patch can be finalized. Performed against a live install with `ilspycmd`, since the reference-only NuGet DLL has empty method bodies.
