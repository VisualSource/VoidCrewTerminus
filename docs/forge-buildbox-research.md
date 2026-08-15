# Forge BuildBox Research

Primary-source research feeding the "give the Upgrade Forge its own dedicated
BuildBox" implementation plan. Ground truth is the decompiled game source under
`.voidcrew/decompiled/`; repo paths are relative to the repo root.

---

## 1. Custom carryable → loot pool auto-registration

**The claim is correct but conditional, and it is vanilla game behavior, not a
VoidManager feature.**

Loot-pool inclusion happens automatically **only if** the carryable's source
prefab also carries a `LootTableItem` component with `DropTableEntries` (and/or
`SectorCompletionReward`) authored on it — and only when the asset is loaded
through the vanilla `RuntimeAssetsAPI` → `RuntimeAssetConverter` pipeline (the
`*.metem` bundle path). It is **not** a separate registration call the modder
makes; it is one branch inside the same conversion function that also
registers the carryable into `RuntimeAssetsRegister`.

- `RuntimeAssetConverter.ConvertAsset` (`.voidcrew/decompiled/Assembly-CSharp/RuntimeAssets/RuntimeAssetConverter.cs:25-59`):
  after registering a `CarryableBaseAsset`-flagged prefab into
  `RuntimeAssetsRegister.Instance.RegisterAsset` (line 32), it checks
  `((Component)vca).gameObject.TryGetComponent<LootTableItem>(...)` (line 40)
  and, if present, calls `AddAssetToDropTables` (line 42) and tags the asset
  `SessionModificationEffect.InLootTables` (line 43). No `LootTableItem` →
  no loot-table wiring, full stop.
- `LootTableItem` (`.voidcrew/decompiled/VoidCrewCommon/VC.Common/LootTableItem.cs:1-14`):
  `[RequireComponent(typeof(VoidCrewAsset))]`, exposes `SectorCompletionReward[]`
  and `DropTableEntries` (`DropTableEntry[]`) — this is the opt-in the modder
  authors in the Unity editor project.
- `AddAssetToDropTables` / `AddAssetToDropTable` (`RuntimeAssetConverter.cs:309-386`):
  walks the entries and pushes `LootTableEntry`/`DropBucketEntry` records into
  `DataTable<RuntimeAssetTable>.Instance.LootTablesByCategory[...]` and
  `EndlessChapterDropData` — vanilla data tables, no VoidManager involvement.
- Entry point: `RuntimeAssetsAPI.LoadAsset(VoidCrewAsset, ...)`
  (`.voidcrew/decompiled/Assembly-CSharp/RuntimeAssets/RuntimeAssetsAPI.cs:41-47`)
  calls straight into `RuntimeAssetConverter.ConvertAsset`. `LoadAssetBundle`
  (lines 9-21, 56-69) calls `LoadAsset` on every object the bundle contains.

**Docs confirmation:** [Carryables — Loot Table Item](https://hutlihutgames.github.io/void_crew_modding_documentation/Carryables/Carryables.html)
describes the Loot Table Item component as "what determines how your asset
will naturally appear in the game as loot" via Sector Completion Rewards and
Drop Table Entries. The page does not use the word "automatic" and never
mentions BuildBox — it documents the component's fields, not the underlying
`ConvertAsset` wiring. The behavior described above (same pass, no separate
call) is corroborated only by the decompiled source, not by the docs' prose.

**VoidManager is not involved.** A page-level check of
[github.com/Nihility-Shift/VoidManager](https://github.com/Nihility-Shift/VoidManager)
shows no carryable/loot/runtime-asset registration API — VoidManager's surface
is Harmony transpiler helpers, general utilities, recipe/unlock tweaks, and
networking events. Everything above lives in `Assembly-CSharp`
(`RuntimeAssets`, `RuntimeAssets.ReferenceAssets`, `VC.Common` namespaces),
i.e. vanilla game code, the same layer `AssetLoader.cs` already touches for
`RuntimeAssetsRegister`.

**Does the mod's existing `RuntimeAssetsRegister` path already give this?**
No — and it structurally can't, for two reasons:

1. `AssetLoader.LoadBundle` (`VoidCrewTerminus/AssetLoader.cs:101-136`)
   deliberately **skips** `RuntimeAssetConverter` for the Forge module
   prefab. It only calls `RuntimeAssetsAPI.LoadAsset(asset)` (line 134) for
   assets that are *not* the module-cell prefab; the module cell itself goes
   through `AssetLoader`'s own `RegisterModulePrefab`
   (`AssetLoader.cs:259-274`), which calls
   `RuntimeAssetsRegister.Instance.RegisterAsset` **directly**, bypassing
   `ConvertAsset` (and therefore bypassing the `LootTableItem` check) entirely.
   The comment at `AssetLoader.cs:33-37` explains why: `RuntimeAssetConverter`
   only understands two prefab shapes (`CarryableBaseAsset` and
   `PlayerShipVisuals`); the Forge's `UpgradeForgeModuleCell` prefab is
   neither, so it would fail conversion if routed through the normal path.
2. Even for genuine carryables the mod ships as real `*.metem`-compatible
   `CarryableBaseAsset` prefabs (none currently — see §2), the loot hookup
   still requires authoring `LootTableItem` on that prefab; it is not implied
   by `CarryableBaseAsset`/`RuntimeCarryable` alone.

**Consequence for a dedicated Forge BuildBox:** `ConvertCarryableObjectAsset`
(`RuntimeAssetConverter.cs:205-221`) does not preserve the source prefab's own
component types — it clones a fixed vanilla template
(`DataTable<RuntimeAssetTable>.Instance.CarryableObject` or `.CarryableModObject`,
line 209) and only reskins it via `RuntimeCarryable.ApplyChanges`
(collider/renderer/audio overrides — `.voidcrew/decompiled/Assembly-CSharp/RuntimeAssets.ReferenceAssets/RuntimeCarryable.cs:23-113`).
Neither template is a `BuildBox`. **A custom `BuildBox` cannot be produced by
pushing a `CarryableBaseAsset` prefab through the standard `*.metem` pipeline** —
that pipeline can only ever yield a plain `CarryableObject`/`CarryableModObject`,
never a `BuildBox` subclass with `moduleRef`/`BuildModule`. If a dedicated
Forge BuildBox is wanted, it must be registered the same way the module cell
already is — a bundle-owned prefab (`AssetLoader`'s `_modulePrefabs`-style
path) grafted with the needed components and registered straight into
`RuntimeAssetsRegister`, not routed through `RuntimeAssetConverter`. That also
means automatic loot-pool inclusion (the `LootTableItem` branch) would **not**
apply to it for free; if loot-table presence is desired for the box itself,
the `AddAssetToDropTables`/`AddAssetToDropTable` calls would need to be
invoked manually after registration (own decision — a BuildBox appearing as
random sector loot is unusual anyway; vanilla BuildBoxes are constructed by
crew action, not looted).

---

## 2. How the mod currently spawns the Forge module (baseline)

**Correction to the task's premise:** the `!forgespawn` command and the
donor-box trick live in **`VoidCrewTerminus/Commands/ForgeCommitCommand.cs`**,
not `ForgeDevCommands.cs` (that file only has `!setlevel`/`!getlevel`/
`!dumptags`/`!resetoverlay`, no spawn logic).

The exact "donor box, re-pointed `moduleRef`" trick, with the doc comment
explaining it, is `ForgeSpawnCommand` in
`VoidCrewTerminus/Commands/ForgeCommitCommand.cs:255-353`:

- **Header comment** (lines 255-267) states plainly this is a *"Phase-3
  test-only path for installing the Forge before shipping a bespoke BuildBox
  prefab"* — i.e. the repo already flags this as a placeholder to be replaced,
  which lines up with the current ask.
- `Execute` (lines 275-306):
  1. Requires host (`PhotonNetwork.IsMasterClient`, line 281) because the
     mutation below is client-local.
  2. `TryFindForgeModuleGuid` (lines 311-329) walks
     `RuntimeAssetsRegister.Instance.GetAllIds()` looking for the asset whose
     `.name == UpgradeForgeBehavior.PrefabName` — this is how it finds the
     Forge module's own GUID (registered by `AssetLoader.RegisterModulePrefab`).
  3. `TryFindDonorBuildBoxGuid` (lines 334-352) — see §Side item below; picks
     any vanilla plain (non-composite) module's `BuildBoxRef` GUID.
  4. `ObjectFactory.InstantiateSpaceObjectByGUID<BuildBox>(donorGuid, ...)`
     (line 291) spawns a **real, fully-networked vanilla `BuildBox`** using
     the donor module's own BuildBox prefab/GUID — so it inherits all of
     `BuildBox`'s/`CarryableObject`'s carry/socket/construction plumbing for
     free.
  5. `box.moduleRef.AssetGuid = forgeGuid; box.moduleRef.IsRuntime = true;`
     (lines 294-300) — the actual "re-point" step. `moduleRef` is a
     `CloneStarObjectRef` field on `BuildBox`
     (`.voidcrew/decompiled/Assembly-CSharp/CG.Ship.Object/BuildBox.cs:14-15`).
     Mutating it in place means the spawned instance still *looks and
     animates* like the donor module's box (its mesh is baked into the
     prefab, not read from `moduleRef`), but when construction completes it
     builds the Forge instead of the donor's real module.
- **How the re-point actually resolves at build time:** vanilla
  `BuildBox.BuildModule` (`.voidcrew/decompiled/Assembly-CSharp/CG.Ship.Object/BuildBox.cs:27-37`)
  calls `ObjectFactory.InstantiateSpaceObjectByGUID(moduleRef.AssetGuid, ...)`
  directly against whatever GUID is currently on the field — it has no idea
  the GUID was swapped. But this only works for **non-runtime** module refs;
  for the Forge (a `RuntimeAssetsRegister`-backed asset,
  `moduleRef.IsRuntime == true`), the vanilla path would NRE trying to
  dereference a null def. `ForgeBuildBoxRuntimeModulePatch`
  (`VoidCrewTerminus/Patches/ForgeInteractionPatch.cs:36-52`) is the Harmony
  prefix that intercepts this: if `moduleRef.IsRuntime` and the GUID is in
  `RuntimeAssetsRegister`, it calls `ObjectFactory.InstantiateRuntimeObject`
  instead and skips vanilla (`return false`); otherwise it falls through to
  vanilla unchanged. This patch is what actually makes the re-pointed
  `moduleRef` buildable — the donor-box trick alone is not sufficient.
- **Documented caveats** (lines 261-267): donor mesh shows while carrying
  (correct Forge mesh only appears post-build); the `moduleRef` mutation is
  client-local (must be the host who completes construction); deconstructing
  the built Forge afterwards throws `ArgumentNullException` because the Forge
  module prefab has no `BuildBoxRef` set (see §3 — this is the same gap that
  blocks vanilla deconstruct today).

**What "give it a dedicated BuildBox" has to do, precisely:** replace steps 3-5
above. Instead of borrowing a donor's live `BuildBox` instance and mutating
its `moduleRef`, ship a Forge-owned prefab carrying a `BuildBox` component
(pre-pointed at the Forge's own GUID, no runtime mutation needed), register it
via the same direct `RuntimeAssetsRegister.RegisterAsset` path
`AssetLoader.RegisterModulePrefab` already uses for the module cell
(`AssetLoader.cs:259-274`), and set the Forge module prefab's own
`BuildBoxRef` (`CellModule.BuildBoxRef`, referenced at
`ForgeCommitCommand.cs:343`) to point back at it — closing the
deconstruct-NRE gap simultaneously since `Deconstruct` dereferences
`BuildBoxRef` (per the existing header-comment caveat).

---

## 3. Vanilla deconstruct action wiring

**Confirmed and expanded: the deconstruct-with-lever behavior is prefab-authored
per module, on two independent axes, neither of which the Forge currently has.**

### Axis A — a Mediator component must exist on the module GameObject

`AbstractModuleMediator<T>` (`.voidcrew/decompiled/Assembly-CSharp/CG.Client.Ship.Modules.Mediators/AbstractModuleMediator.cs:15-52`)
is a `MonoBehaviour` (not something attached automatically by tag/category).
Its `Awake` (lines 39-52) does `Module = ((Component)this).GetComponent<T>()`
(line 41) — it must sit on the **same GameObject** as the `CellModule`. If
`Module.photonView.InstantiationData != null`, it defers deconstruct-button
wiring into the `Module.OnModuleInitialized` callback (lines 47-51), which
calls `InitializeDeconstructButton()` (line 49).

`InitializeDeconstructButton` (lines 64-73):
```
ModuleDeconstructButton componentInChildren = ((Component)this).GetComponentInChildren<ModuleDeconstructButton>();
if (Object.op_Implicit((Object)(object)componentInChildren))
{
    ((Component)componentInChildren).GetComponent<ExtruderLever>().LeverThresholdTriggerEvent.AddListener(new UnityAction(OnClickedDeconstructButton));
}
```
Confirms the earlier investigation exactly: it finds a `ModuleDeconstructButton`
anywhere in children (line 68), then requires `ExtruderLever` as a **sibling
component on that same GameObject** (`GetComponent`, not `GetComponentInChildren`,
line 71) — if either component or the wiring is missing, the whole block is a
silent no-op (no error, no button).

`OnClickedDeconstructButton` (lines 101-112) is what actually fires
`Deconstruct.CanRemoveModule` / `BuildProcessController.Instance.TryDeconstructModule(Module)`
— i.e. this Mediator is the sole vanilla entry point into deconstruction via
the in-world lever prompt; there is no other trigger path.

**There is no CellModule-generic Mediator attached automatically.** Every
concrete Mediator is a hand-authored subclass added as a prefab component:
`BaseModuleMediator : AbstractModuleMediator<CellModule>`
(`.voidcrew/decompiled/Assembly-CSharp/CG.Client.Ship.Modules.Mediators/BaseModuleMediator.cs`,
whole file — an empty subclass, i.e. the generic fallback for plain
`CellModule`-typed modules with no bespoke behavior) plus ~13 module-specific
ones found by grep (`JammerModuleMediator`, `ShieldModuleMediator`,
`LifeSupportModuleMediator`, `TerminalModuleMediator`,
`CentralShipComputerMediator`, `GravityScoopMediator`, `HelmMediator`,
`TacticalScannerMediator`, `KineticPointDefenseMediator`,
`CloningFacilityMediator`, `CarryablesAirlockMediator`,
`CarryablesShelfMediator`, `LegacyShieldModuleMediator`,
`CompositeWeaponMediator`). Since `BaseModuleMediator`'s generic parameter is
plain `CellModule`, it is the correct choice for a Utility-category module
like the Forge — but it still has to be **added as a component to the Forge's
prefab GameObject**; nothing infers it from the `Utility` CsTag or from being
a registered `CellModule`.

### Axis B — the lever prop itself is a modeled child object, not data

`ModuleDeconstructButton` (`.voidcrew/decompiled/Assembly-CSharp/CG.Client.Ship.Interactions/ModuleDeconstructButton.cs:1-8`)
is a literally-empty marker class (`public class ModuleDeconstructButton : MonoBehaviour {}`)
— pure tag, no fields, no logic.

`ExtruderLever : Lever` (`.voidcrew/decompiled/Assembly-CSharp/CG.Client.Ship.Interactions/ExtruderLever.cs:1-39`)
carries `maxExtrusion`/`minExtrusion`/`extrusionAxis`/`extrusionCurve` (lines
7-17) and animates a `leverTransform` (inherited from `Lever`,
`.voidcrew/decompiled/Assembly-CSharp/CG.Client.Ship.Interactions/Lever.cs:51`)
by lerping its local position along one axis (`UpdateVisualLeverRotation`,
lines 29-38) — this is a visual prop transform the lever geometry actually
moves along, driven by player-click threshold events
(`LeverThresholdTriggerEvent`, `Lever.cs:103`, inherited from
`ClickerInteractable`). This is unavoidably prefab/hierarchy authoring: a mesh
+ collider + serialized curve, not a value that can be grafted onto a bundle
asset the way `AssetLoader.GraftModuleComponents` grafts `CellModule`/
`PowerDrain` fields today.

### Current Forge state: neither axis is present

`AssetLoader.GraftModuleComponents` (`VoidCrewTerminus/AssetLoader.cs:172-251`)
grafts exactly `CellModule`, `PowerDrain`, `OcclusionNode`(s), and `PhotonView`
onto the bundle-loaded module prefab — **no Mediator, no `ModuleDeconstructButton`,
no `ExtruderLever`**. `ForgeAttachHelper.TryAttach`
(`VoidCrewTerminus/Patches/ForgeInteractionPatch.cs:95-131`), which runs on
every built Forge instance, likewise only ever adds `UpgradeForgeBehavior`
(lines 116-121) — no vanilla Mediator is attached there either. Combined with
`ForgeSpawnCommand`'s own caveat that deconstructing throws
`ArgumentNullException` because the Forge prefab has no `BuildBoxRef`
(`ForgeCommitCommand.cs:266-267`), the Forge today has **zero** of the vanilla
deconstruct plumbing, on both the button-wiring axis and the
`BuildBoxRef`-resolution axis Deconstruct itself needs.

**Minimal requirement to get vanilla deconstruct-with-lever, precisely:**

1. Add a `BaseModuleMediator` component to the Forge module's root GameObject
   (in the bundle prefab, `VoidCrewUnityEditor/` project — or graft it at
   runtime the same way `PowerDrain`/`OcclusionNode` are grafted today, since
   it's a plain no-field subclass with no serialized state of its own; either
   is mechanically possible, but graft-vs-author is a design choice for the
   implementation plan).
2. Author a child GameObject somewhere under the module root carrying both
   `ModuleDeconstructButton` and `ExtruderLever` (sibling on the same
   GameObject as each other), with `ExtruderLever`'s `leverTransform` and
   curve fields wired to an actual lever mesh — this **must** be built in the
   Unity editor project / asset bundle; it cannot be runtime-grafted the way
   flat-data components are, because it needs real geometry and a configured
   `AnimationCurve`.
3. Set the Forge module's `BuildBoxRef` (on `CellModule`) to point at
   whichever BuildBox asset is authoritative for it — required independently
   of deconstruct-button wiring, since `Deconstruct` itself dereferences
   `BuildBoxRef` and currently NREs without it (per §2's caveat). This is the
   same asset produced by §2's "dedicated BuildBox" work, so the two efforts
   converge on one prefab reference.

No amount of CsTag stamping (`ForgeAttachHelper.EnsureTag`,
`ForgeInteractionPatch.cs:133-139`) substitutes for either axis — Mediator
wiring is purely `GetComponent`/hierarchy-based, not tag-based.

---

## Side item: `!forgespawn` lag spike — likely cause

**Root cause candidate:** `ForgeSpawnCommand.TryFindDonorBuildBoxGuid`
(`VoidCrewTerminus/Commands/ForgeCommitCommand.cs:334-352`), called once per
`!forgespawn` invocation (`Execute`, line 287).

```csharp
foreach (var def in ResourceAssetContainer<ModuleContainer, CellModule, ModuleDef>.Instance.AssetDescriptions)
{
    if (def == null) continue;
    var module = def.Asset;                 // line 341
    if (module == null) continue;
    var boxRef = module.BuildBoxRef;
    if (boxRef == null || boxRef.IsNull) continue;
    var boxAsset = boxRef.Asset;             // line 345
    if (boxAsset == null || boxAsset is CompositeWeaponBuildBox) continue;
    guid = boxRef.AssetGuid;
    moduleName = module.name;
    return true;
}
```

This is a **linear scan of the game's entire module registry** (every
`ModuleDef` in `ModuleContainer.AssetDescriptions`, one entry per module type
in the game), and each iteration can trigger up to **two synchronous
`Resources.Load` calls**:

- `def.Asset` (line 341) is `ResourceAssetDef<CellModule>.Asset`
  (`.voidcrew/decompiled/ResourceAssets/ResourceAssets/ResourceAssetDef.cs`:
  `public T Asset => Ref.DefinedAssetInstance<T>();`) → for a non-runtime ref,
  `DefinedAssetInstance<T>` (`.voidcrew/decompiled/ResourceAssets/ResourceAssets/ResourceAssetRef.cs:196-208`)
  falls through to `LoadAsset<T>()` (lines 219-225), which does
  `Resources.Load<T>(Path)` (line 223) whenever the ref's cache is empty or
  stale.
- `boxRef.Asset` (line 345) is the same pattern one level down
  (`ResourceAssetRef<U,T,V>.Asset`, `ResourceAssetRef.cs:26-45`), another
  potential `Resources.Load` for the BuildBox asset itself.

`Resources.Load` is a synchronous main-thread disk/asset-catalog read; doing
it in a tight loop over the full module list (and the function returns on the
**first** qualifying module — how far it has to scan before finding one with
a non-null, non-composite `BuildBoxRef` is nondeterministic and depends on
`AssetDescriptions` iteration order) is a classic frame-hitch pattern.
Whether these particular `Resources.Load` calls hit a warm cache after the
first `!forgespawn` (each `ResourceAssetRef` does cache its own
`ResourceAsset` field, `ResourceAssetRef.cs:213-217`/`221-225`) or cold-load
every time depends on whether anything else in normal play has already forced
those specific module/BuildBox assets to resolve — worth instrumenting rather
than assuming, but this loop is the clear candidate: it is the only
per-invocation code in the `!forgespawn` path that iterates an
unbounded/whole-game-content collection and touches `Resources.Load`-backed
properties. Not fixed here per the task scope — flagging for the
implementation plan (e.g. cache the donor GUID after first resolution, or
skip the donor search entirely once a dedicated Forge BuildBox from §2 exists).

---

## File index for the implementation plan

| Concern | File | Lines |
|---|---|---|
| Donor-box spawn command | `VoidCrewTerminus/Commands/ForgeCommitCommand.cs` | 255-353 |
| Runtime-module BuildBox patch (makes re-pointed `moduleRef` buildable) | `VoidCrewTerminus/Patches/ForgeInteractionPatch.cs` | 36-52 |
| Forge module attach/tag helper | `VoidCrewTerminus/Patches/ForgeInteractionPatch.cs` | 95-152 |
| Bundle loading / module prefab registration | `VoidCrewTerminus/AssetLoader.cs` | 101-136, 172-251, 259-274 |
| Vanilla `BuildBox` | `.voidcrew/decompiled/Assembly-CSharp/CG.Ship.Object/BuildBox.cs` | 1-69 |
| `CarryableBaseAsset` (metem carryable marker) | `.voidcrew/decompiled/VoidCrewCommon/VC.Common.Carryables/CarryableBaseAsset.cs` | 1-28 |
| `LootTableItem` | `.voidcrew/decompiled/VoidCrewCommon/VC.Common/LootTableItem.cs` | 1-14 |
| `RuntimeAssetConverter` (carryable conversion + loot wiring) | `.voidcrew/decompiled/Assembly-CSharp/RuntimeAssets/RuntimeAssetConverter.cs` | 25-221, 309-386 |
| `RuntimeAssetsAPI` (bundle/asset load entry points) | `.voidcrew/decompiled/Assembly-CSharp/RuntimeAssets/RuntimeAssetsAPI.cs` | 1-78 |
| `AbstractModuleMediator<T>` (deconstruct button wiring) | `.voidcrew/decompiled/Assembly-CSharp/CG.Client.Ship.Modules.Mediators/AbstractModuleMediator.cs` | 15-112 |
| `BaseModuleMediator` (generic CellModule mediator) | `.voidcrew/decompiled/Assembly-CSharp/CG.Client.Ship.Modules.Mediators/BaseModuleMediator.cs` | 1-7 |
| `ModuleDeconstructButton` (marker) | `.voidcrew/decompiled/Assembly-CSharp/CG.Client.Ship.Interactions/ModuleDeconstructButton.cs` | 1-8 |
| `ExtruderLever` / `Lever` | `.voidcrew/decompiled/Assembly-CSharp/CG.Client.Ship.Interactions/ExtruderLever.cs`, `Lever.cs` | 1-39, 51/103 |
| Donor-scan lag candidate | `VoidCrewTerminus/Commands/ForgeCommitCommand.cs` | 334-352 |
| `ResourceAssetRef`/`ResourceAssetDef` (`Resources.Load` cost) | `.voidcrew/decompiled/ResourceAssets/ResourceAssets/ResourceAssetRef.cs`, `ResourceAssetDef.cs` | 26-45, 196-225 |

**Docs pages consulted:**
[Carryables overview](https://hutlihutgames.github.io/void_crew_modding_documentation/Carryables/Carryables.html) ·
[docs index](https://hutlihutgames.github.io/void_crew_modding_documentation/) (confirms no dedicated BuildBox/module page exists) ·
[VoidManager source](https://github.com/Nihility-Shift/VoidManager) (confirms no carryable/loot/runtime-asset API of its own)
