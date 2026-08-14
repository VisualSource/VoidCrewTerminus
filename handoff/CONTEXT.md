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

### Aux Fighter feature

**Aux Fighter**:
A single-seat craft carried by the player ship, launched and recovered through a Fighter Bay. Flown by one crewmember; its weapons damage only enemy Drones.
_Avoid_: "Drone" (taken — the vanilla `Drone` class is the enemy fighter this thing shoots), "aux ship", "fighter" unqualified.

**Fighter Bay**:
The installable `CellModule` that carries, launches, recovers, sells, refuels and repairs an Aux Fighter. Has a control panel. The Aux Fighter clamps to it from **outside the hull** — the bay is a hardpoint with a hull pass-through, not an interior hangar. Nothing lands inside it.
_Avoid_: "Hangar" — implies an interior volume the Fighter flies into, which is wrong. "Airlock module" unqualified — vanilla has two unrelated airlocks (see below).

**Docking Collar**:
The sealed interface between a Latched Aux Fighter's cockpit and the Fighter Bay's hull pass-through. Aligned only while Latched. It is an alignment, not a passage: the pilot is seated by an interaction prompt exactly as they would take over a turret, and never traverses the hull. The Collar's job is to put the cockpit adjacent to the module so that prompt is offered at all, which gates boarding by geometry rather than by a rule. Modelled on `CarryablesAirlock`, the vanilla `CellModule` that passes carryables through the hull via input/output sockets.
_Avoid_: describing it as a passage, hatch or corridor — nothing walks through it.
_Avoid_: Confusing this with vanilla `Airlock`, which is a `MonoBehaviour` — the fixed crew EVA lock, not installable and unrelated.

**Grab State**:
A Fighter Bay that is powered with its hardpoint empty, reaching for an Aux Fighter. Modelled on `CarryableAttractor`, which already has the three zones this needs: pull begins at `MaxRange`, capture commits inside `CatchRadius`, and `BufferRange` provides hysteresis so a released object is not instantly re-grabbed.

**Capture Envelope**:
The three-zone geometry of a Grab State bay. Outside **MaxRange** nothing happens. Between MaxRange and **CatchRadius** the bay drags the Aux Fighter in and rotates it into the hardpoint's orientation — and the pilot can still **Break Away**. Inside CatchRadius the capture is committed, alignment completes, and the bay Latches. The bay performs the attitude match, so the pilot never has to fly a precise docking manoeuvre.

**Break Away**:
A pilot escaping an in-progress capture by out-thrusting the pull, possible only while outside CatchRadius. Past that point the Aux Fighter is committed and will Latch. `BufferRange` hysteresis keeps a break-away from being immediately re-captured while still inside MaxRange.

**Latched**:
The state of a Fighter Bay clamping an Aux Fighter to its exterior hardpoint under power, with the Docking Collar aligned. Losing power ends it.
_Avoid_: "Docked" when the mechanical state is meant — Docked is the fiction, Latched is the state that power maintains.

**Release Grace**:
The alarmed countdown that begins when a Latched bay loses power involuntarily (combat defect, power reroute, a RandomShutoff Maintenance Burden). Restoring power within the window re-latches; expiry releases the Aux Fighter.

**Sortie**:
One launch-to-recovery cycle. Bounded by alloy fuel, by the sector's jump, and by enemy fire.

**Signature Profile**:
The deliberately small `SignatureRadius` that governs how enemies attend to an Aux Fighter. Vanilla `TargetSelector` scores candidates by `distance − SignatureRadius`, so a small profile means large ships ignore the Fighter until it is close and the player ship is out of range.
_Avoid_: "Stealth", "cloak" — nothing is hidden; the Fighter is simply a less attractive target than the ship it came from.

**Fighter Cap**:
The hard limit of 2 Aux Fighters per run, regardless of how many Fighter Bays are installed. Same role as the Leech encounter's Concurrency Safety Rail.

**Coop-gated launch**:
The rule that undocking requires two crewmembers — one seated in the Aux Fighter, one cutting bay power. Recovery is symmetrical: someone must re-power the bay. A deliberate design constraint, not an oversight; the Aux Fighter is coop-only content.

### Leech Queen boss

**Leech Queen**:
A new Pilgrimage boss — a stationary capital-scale enemy that holds position and cycles
attacks at the player ship, in the manner of the vanilla solar-system bosses. Shares the
Leech's art language and origin fiction; **mechanically independent** of the Leech Carrier
encounter. Canonical noun for both code identifiers and the player-facing display name.
_Note_: "Queen" names her place in the hive fiction, not a hard dependency — she does not
require [[Leech]], [[Leech Missile]] or the Concurrency Safety Rail to function.
_Avoid_: `LeechEncounter*` prefixes on her types (implies the coupling that was rejected);
"Queen" unqualified; "Leech Boss".

**Queen Gate**:
The condition admitting the Leech Queen to a solar system's boss roll: Sector Escalation
must already be active. Before the gate she is absent from the roll entirely; after it she
competes with the vanilla boss rather than replacing it, at a configurable rate.
_Avoid_: "boss unlock" — the Queen is never guaranteed, only made eligible.

**Host Skeleton**:
The vanilla boss structure a Leech Queen is mounted on — colliders, hit points, destroyable
components, AI and network identity all stay vanilla. Only the visible geometry and the
attack set are the mod's. Chosen so a custom-modelled boss never requires hand-grafting a
networked NPC.
_Avoid_: "reskin" — the attack set is authored too, not just the mesh.

**Brood Path**:
The Leech Queen's optional parasite-launching attack, registered only when the Leech runtime
is present, and delegating all spawn arbitration to it — the Queen never counts parasites
herself. Its presence must be host-broadcast, never read from per-player config, because
ability registration is positional across the network stream.
_Avoid_: giving the Queen her own parasite cap — the Concurrency Safety Rail has one owner.

### Shared

**Per-run state**:
State that lives only for the duration of one ship-lifespan. Resets to baseline on ship death. Both the Upgrade Forge overlay and Leech encounter state are per-run.
_Avoid_: "Session state" (session can span many runs).

**Host-arbitrated**:
The multiplayer pattern used throughout this mod. The Photon master client owns canonical state and all RNG; other clients sync via VoidManager `ModMessage` and replay host decisions.
_Avoid_: "Authoritative" alone — say "host-arbitrated" for clarity, since "authoritative" is ambiguous in netcode contexts.

**Pre-flight (verification)**:
Decompile-based confirmation of vanilla game internals required before a Harmony patch can be finalized. Performed against a live install with `ilspycmd`, since the reference-only NuGet DLL has empty method bodies.
