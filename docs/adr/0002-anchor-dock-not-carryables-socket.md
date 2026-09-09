# ADR-0002: Forge Anchors as Plain Transforms + AnchorDock, Not CarryablesSocket

**Status:** Accepted
**Date:** 2026-08-17

## Context

The Upgrade Forge holds items in the world: up to six relics in its tubes and one BuildBox
in its module socket. Vanilla's mechanism for exactly this is `CG.Ship.Hull.CarryablesSocket`
— what the fabricator, ammo racks and every other "put a thing in the module" site uses.

The Forge instead ships named anchor transforms (`RelicTubeTarget`, `InputTarget`, …) in the
metem bundle, finds them by name at runtime, and holds items on them with a mod-side
`AnchorDock` that freezes their rigidbodies and pins them per-frame.

This decision was made implicitly during Forge implementation and never recorded. The rationale
in `ForgeAnchors`' doc comment — game components can't be serialized into a metem bundle — is
true but **does not justify the choice**: `ModulePrefabGrafter.Graft` already adds
`CellModule`, `PowerDrain` and a `PhotonView` to bundle prefabs at load time, so bundle
serialization was never the blocker. This ADR records the reason that actually holds.

Two approaches:

1. **Graft `CarryablesSocket` per anchor** — seven real vanilla sockets on the Forge, registered
   into `CellModule.ConnectedSockets`.

2. **Plain anchors + mod-side `AnchorDock`** — transforms found by name, physics owned by the mod.

## Decision

**Option 2 (plain anchors + `AnchorDock`) was chosen.**

## Reasoning

`CarryablesSocket` is not a component. It is
`CarryablesSocket : OrbitObject : AbstractCloneStarObject` — a networked CloneStar entity that
expects to be born through `ObjectFactory` / `PhotonNetwork.Instantiate` with instantiation data,
not through `AddComponent`. Grafting one requires, **per anchor** (seven per Forge):

- **A PhotonView with a live, cross-client-agreed ViewID.** `CarryablesSocket.OnEnable` calls
  `photonView.AddCallbackTarget(this)`, and `OrbitObject` implements
  `IPunInstantiateMagicCallback` and `ISelfReferencingResourceAsset`.
- **A sibling `Carrier`.** `Awake` runs `_carrier.Owner = this` with no null check.
- **A `SocketTransformProvider`**, whose `payloadStoreTransform` is a private `[SerializeField]`.
- **A `CarryablesSocketActor`**, whose own `Awake` calls `socketInteractable.SetSocketActor(this)`
  on a serialized field, plus three `InteractionInfo` assets.
- **All of `OrbitObject`'s surface**: stat-collection registration, damage/hit receivers, sector
  RPCs. Every relic tube becomes a damageable, taggable networked entity.

The decisive factor is the network identity. Allocating seven synchronized ViewIDs for
runtime-added components on a bundle-instantiated prefab lands directly on the PUN
initialization hazard documented in `CLAUDE.md` — the failure that silently broke matchmaking
and cost a bisect to find (`bd4c6f2`). `AnchorDock` has zero network surface by comparison:
it is local physics, and the only thing replicated is a small mod-side dock/undock message.

## Consequences

**Accepted cost: vanilla systems keyed on `CarryablesSocket` do not see the Forge**, and each
must be reimplemented by hand. Three so far:

| Vanilla system | Keys on | Mod-side replacement |
|---|---|---|
| `CG.Rendering.SocketOutlines` (translucent placement preview) | `CarryablesSocket.OnSocketAdded` | `ForgeGhosts` |
| `Deconstruct.CanStartDeconstruct` (`BlockedByFullSockets`) | `module.ConnectedSockets` | `ForgeDeconstructGuardPatch` |
| Carry/ownership replication | `ICarrier` on the socket | `ForgeNetSync.BroadcastDock` + `ApplyRemoteDock` |

The deconstruct guard is the cautionary case: the gap was invisible for the Forge's whole
lifetime because docked items *usually* tripped `ConstructUtil.AnyObjectObstructsVolume` instead,
producing a plausible-looking refusal for the wrong reason. It surfaced only from play. Assume
future gaps of this shape exist and will surface the same way.

Two mitigations that keep the cost bounded:

- Mod-side replacements should borrow vanilla's own assets and result types rather than
  inventing lookalikes — `ForgeGhosts` reads the real `HologramMaterial` off the scene's
  `SocketOutlines`, and the deconstruct guard returns vanilla's `ConstructResult.BlockedByFullSockets`
  so vanilla's own localized warning renders. This keeps the seam invisible to players and
  self-correcting across game updates.
- Prefer patching the shared vanilla chokepoint over the mod's own call site.
  `ForgeDeconstructGuardPatch` postfixes `Deconstruct.CanStartDeconstruct`, which covers the
  Forge's handle, vanilla's mediator button, the upgrade paths, and
  `DeconstructionProcess.RunWaiting`'s per-tick re-validation — one patch instead of four.

**Revisit if** the Forge needs genuine carry-ownership semantics (transferring a docked item's
Photon ownership, or surviving host migration with items docked), or if a third mod-side
reimplementation of a socket-keyed system becomes load-bearing enough that the seven ViewIDs
look cheap. At that point the migration is well-defined — graft the four components above and
delete `AnchorDock` — but it is not worth doing speculatively.
