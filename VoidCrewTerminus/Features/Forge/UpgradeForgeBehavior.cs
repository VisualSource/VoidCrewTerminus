using System.Collections.Generic;
using System.Linq;
using CG.Game.Player;
using CG.Network;
using CG.Objects;
using CG.Ship.Modules;
using CG.Ship.Object;
using Gameplay.Tags;
using UnityEngine;
using VoidManager.Utilities;

namespace VoidCrewTerminus.Forge;

// MonoBehaviour attached at runtime to the Upgrade Forge prefab
// (Assets/voidcrewterminus.metem → UpgradeForgeModuleCell.prefab).
//
// Phase 3 responsibilities:
//   - Hold at most one BuildBox in the module socket (the target being upgraded).
//   - Hold up to Capacity relics in the relic slots (Capacity = 4 hardcoded; Phase 5
//     ties this to the Forge Meter).
//   - Enforce the cost curve on commit.
//   - Persist the new level via ForgeOverlayTable.SavePendingLevel so the level rides
//     the BuildBox through reconstruction — ForgePersistPatch does the restoration.
//
// In-world interaction model (see also ForgeInteractionPatch):
//   - BuildInteractables() spawns ForgeInteractable colliders on the prefab's named
//     anchors: RelicTubeTarget (×6), InputTarget (module socket), and an optional
//     CommitTarget. Clicks are routed here via the CarryableInteract prefix.
//   - Inserted relics / the loaded BuildBox stay live in the world: they are docked
//     kinematically to their anchor (LateUpdate keeps them pinned while the ship
//     moves) and remain grabbable. Update() reconciles state when a player grabs a
//     docked item back out or a commit destroys consumed relics.
public class UpgradeForgeBehavior : MonoBehaviour
{
    // Phase 5: relic capacity is the Forge's progression level (1..4). Filling the
    // meter — sector jumps + alloys — is what unlocks the bigger upgrade steps.
    public static int Capacity => ForgeMeterController.Capacity;

    // Name of the shipped prefab inside voidcrewterminus.metem — used by
    // ForgeInteractionPatch to identify Forge modules as they build.
    public const string PrefabName = "UpgradeForgeModuleCell";

    // Anchor names baked into the shipped prefab. CommitTarget is required for
    // in-world commits; AlloyTarget is the Phase 5 meter terminal (optional — the
    // !setmeter dev command covers testing until the prefab gains the anchor).
    public const string RelicTubeAnchorName = "RelicTubeTarget";
    public const string InputAnchorName = "InputTarget";
    public const string CommitAnchorName = "CommitTarget";
    public const string AlloyAnchorName = "AlloyTarget";

    private BuildBox _moduleBox;
    private readonly List<GameObject> _relics = new();

    // Physically docked items (relics + BuildBox) → the anchor they sit on.
    private readonly Dictionary<GameObject, Transform> _docked = new();
    private readonly List<KeyValuePair<GameObject, Transform>> _undockScratch = new();
    private Transform[] _tubeAnchors = System.Array.Empty<Transform>();
    private Transform _inputAnchor;
    private bool _interactablesBuilt;

    public bool HasModule => _moduleBox != null;
    public int RelicCount => _relics.Count;
    public BuildBox ModuleBox => _moduleBox;
    public IReadOnlyList<GameObject> Relics => _relics;

    // Current effective level of the box in the module socket. Reads any pending level
    // stashed by prior upgrades / deconstructions; falls back to vanilla L3.
    public int CurrentBoxLevel
    {
        get
        {
            if (_moduleBox == null || _moduleBox.photonView == null) return 0;

            // Only a module at its final vanilla mark may be forged; below that the
            // vanilla upgrade-chip path still applies. The mark comes from the
            // game's UpgradableAssetDataTable chain (the same data the vanilla
            // chips walk) — NOT from tags: the BuildBox carryable carries no mark
            // CsTags, and Component.CompareTag compares Unity GameObject tags,
            // never CsTags.
            int mark = GetBoxMark(out bool isFinalMark);
            if (!isFinalMark) return mark; // 1 or 2 → below MinLevel → InvalidModuleLevel on commit

            return ForgeOverlayTable.TryPeekPendingLevel(_moduleBox.photonView.ViewID, out int level)
                ? level
                : ForgeCostCurve.MinLevel;
        }
    }

    // Vanilla mark (1-based position in the module's upgrade chain) of the module
    // this box builds. STRICT policy: only modules provably at the END of an
    // upgrade chain are forgeable — anything we can't resolve (no identity, table
    // missing, guid in no chain) is refused. The permissive alternative ("unknown
    // = final") let MkI/MkII modules with unresolvable identities slip through.
    //
    // Identity differs by box type: composite weapon boxes are GENERIC prefabs —
    // their moduleRef is unset and the weapon identity is a CompositeWeaponDataRef
    // delivered via instantiation data — and their upgrade chains are keyed by
    // that CompositeData guid (see ModuleUpgraderEffects). Plain module boxes
    // chain by their moduleRef guid.
    private int GetBoxMark(out bool isFinalMark)
    {
        isFinalMark = false;
        if (!TryGetBoxIdentity(out var guid)) return 1;

        var table = DataTable<UpgradableAssetDataTable>.Instance;
        if (table?.UpgradableAssets == null)
        {
            BepinPlugin.Log.LogWarning("[Forge] UpgradableAssetDataTable unavailable — refusing to forge.");
            return 1;
        }

        foreach (var chain in table.UpgradableAssets)
        {
            var assets = chain.Assets;
            if (assets == null) continue;
            for (int j = 0; j < assets.Length; j++)
            {
                if (assets[j].AssetGuid == guid)
                {
                    isFinalMark = j == assets.Length - 1;
                    return j + 1;
                }
            }
        }

        // Not in any chain. If a legitimately single-form module ever needs to
        // forge, this log line names the guid to whitelist.
        BepinPlugin.Log.LogInfo($"[Forge] Module {guid.AsHex()} not in any upgrade chain — refusing to forge (strict Mark III policy).");
        return 1;
    }

    // The guid the upgrade chains key this box's module by.
    private bool TryGetBoxIdentity(out GUIDUnion guid)
    {
        guid = GUIDUnion.Empty();
        if (_moduleBox == null) return false;

        if (_moduleBox is CompositeWeaponBuildBox weaponBox)
        {
            if (weaponBox.WeaponDataRef == null || weaponBox.WeaponDataRef.IsNull) return false;
            guid = weaponBox.WeaponDataRef.AssetGuid;
            return true;
        }

        var moduleRef = _moduleBox.moduleRef;
        if (moduleRef == null || moduleRef.IsNull) return false;
        guid = moduleRef.AssetGuid;
        return true;
    }

    // Dev diagnostic (!forgemark): full dump of how the docked box's mark resolves.
    public string DescribeBoxMark()
    {
        if (_moduleBox == null) return "No box docked in the Forge.";

        var sb = new System.Text.StringBuilder();
        sb.Append($"box={_moduleBox.name} ({_moduleBox.GetType().Name})");
        sb.Append(TryGetBoxIdentity(out var guid)
            ? $", identity={guid.AsHex()}"
            : ", identity=NONE (no moduleRef / WeaponDataRef)");

        var table = DataTable<UpgradableAssetDataTable>.Instance;
        if (table == null) sb.Append(" | table=NULL");
        else if (table.UpgradableAssets == null) sb.Append(" | table.chains=NULL");
        else
        {
            sb.Append($" | table.chains={table.UpgradableAssets.Length}");
            int mark = GetBoxMark(out bool isFinal);
            sb.Append($" | resolved mark={mark}, final={isFinal}");
        }
        return sb.ToString();
    }

    // How far the currently-loaded relics would push the socketed module if committed now.
    // Equal to CurrentBoxLevel when nothing is loaded, or when the next-level cost exceeds
    // the inserted relic count.
    public int ProjectedTargetLevel => ForgeCostCurve.MaxReachable(CurrentBoxLevel, _relics.Count);

    // ---- Module socket ------------------------------------------------

    public bool TryTakeModule(BuildBox box)
    {
        if (box == null || _moduleBox != null) return false;
        _moduleBox = box;
        return true;
    }

    public bool TryReleaseModule(out BuildBox released)
    {
        released = _moduleBox;
        _moduleBox = null;
        return released != null;
    }

    // ---- Relic slots --------------------------------------------------

    public bool TryInsertRelic(GameObject relic)
    {
        if (relic == null || _relics.Count >= Capacity) return false;
        if (!IsRelic(relic)) return false;
        _relics.Add(relic);
        return true;
    }

    public bool TryEjectRelic(int index, out GameObject released)
    {
        released = null;
        if (index < 0 || index >= _relics.Count) return false;
        released = _relics[index];
        _relics.RemoveAt(index);
        return true;
    }

    // ---- Commit -------------------------------------------------------

    public enum CommitResult
    {
        Ok,
        NoModule,
        NoRelics,
        AlreadyAtMax,
        InvalidModuleLevel,
        InsufficientRelics,
        MissingViewId,
    }

    // Attempts to upgrade the socketed box using as many inserted relics as the cost
    // curve permits. Consumes only the relics actually spent; leftovers stay in the
    // Forge. On success the new pending state (level + any rolled perk) is written
    // to ForgeOverlayTable so reconstruction picks it up automatically.
    // perkResult carries the human-readable gamble outcome (null when no roll was
    // attempted — e.g. every eligible slot already filled).
    public CommitResult TryCommit(out int newLevel, out int relicsConsumed, out string perkResult)
    {
        newLevel = 0;
        relicsConsumed = 0;
        perkResult = null;

        if (_moduleBox == null) return CommitResult.NoModule;
        if (_moduleBox.photonView == null) return CommitResult.MissingViewId;
        if (_relics.Count == 0) return CommitResult.NoRelics;

        int currentLevel = CurrentBoxLevel;
        if (currentLevel < ForgeCostCurve.MinLevel) return CommitResult.InvalidModuleLevel;
        if (currentLevel >= ForgeCostCurve.MaxLevel) return CommitResult.AlreadyAtMax;

        int nextStepCost = ForgeCostCurve.CostForNextLevel(currentLevel);
        if (_relics.Count < nextStepCost) return CommitResult.InsufficientRelics;

        int achievedLevel = ForgeCostCurve.MaxReachable(currentLevel, _relics.Count);
        relicsConsumed = ForgeCostCurve.RelicsRequired(currentLevel, achievedLevel);
        newLevel = achievedLevel;

        // Best tier among the relics actually consumed drives the perk gamble
        // (multi-relic commits roll once, at the quality of their best relic).
        var bestTier = RelicTier.Common;
        for (int i = 0; i < relicsConsumed && i < _relics.Count; i++)
        {
            if (_relics[i] == null) continue;
            var tier = RelicTierData.Get(_relics[i].name).Tier;
            if (tier > bestTier) bestTier = tier;
        }

        var pending = ForgeOverlayTable.GetOrCreatePending(_moduleBox.photonView.ViewID);
        pending.Level = newLevel;
        perkResult = RollPerkInto(pending, bestTier);

        for (int i = 0; i < relicsConsumed && _relics.Count > 0; i++)
        {
            var relic = _relics[0];
            _relics.RemoveAt(0);
            DestroyRelic(relic);
        }

        BepinPlugin.Log.LogInfo(
            $"[Forge] Committed L{currentLevel}→L{newLevel} on ViewID={_moduleBox.photonView.ViewID} " +
            $"(consumed {relicsConsumed} relic{(relicsConsumed == 1 ? "" : "s")}, {_relics.Count} remain, " +
            $"tier={bestTier}, perk={(perkResult ?? "no roll")})");

        return CommitResult.Ok;
    }

    // The Phase 4 gamble: one roll per commit against the lowest empty slot the
    // relic tier can fill. Local RNG for now — Phase 8 moves the roll host-side.
    private string RollPerkInto(ForgeOverlayTable.PendingState pending, RelicTier tier)
    {
        int slot = PerkPool.TargetSlot(pending.PerkSlots, tier);
        if (slot < 0) return null; // every eligible slot filled — roll skips silently

        float chance = PerkPool.RollChance(tier);
        if (UnityEngine.Random.value >= chance)
            return $"No perk this time ({tier} · {chance:P0} chance).";

        var category = PerkPool.CategoryOf(_moduleBox != null ? _moduleBox.moduleRef?.Asset as CellModule : null);
        var perk = PerkPool.RollPerk(category, tier, slot);
        if (perk == null)
        {
            BepinPlugin.Log.LogWarning($"[Forge] Perk roll succeeded but category '{category}' has no authored perks.");
            return null;
        }

        pending.PerkSlots[slot] = perk.Id;
        return $"Perk gained in slot {slot + 1}: {perk.Name} — {perk.Description}!";
    }

    // ---- In-world interactables ----------------------------------------

    // Spawns ForgeInteractable click targets on the prefab's named anchors.
    // Idempotent — called every time ForgeInteractionPatch re-attaches after a
    // module rebuild.
    public void BuildInteractables()
    {
        if (_interactablesBuilt) return;
        _interactablesBuilt = true;

        var transforms = GetComponentsInChildren<Transform>(true);
        // Tubes may be named "RelicTubeTarget" or numbered ("RelicTubeTarget_01" …);
        // ordering by name makes numbered tubes fill deterministically.
        _tubeAnchors = transforms
            .Where(t => t.name.StartsWith(RelicTubeAnchorName, System.StringComparison.Ordinal))
            .OrderBy(t => t.name, System.StringComparer.Ordinal)
            .ToArray();
        _inputAnchor = transforms.FirstOrDefault(t => t.name == InputAnchorName);
        var commitAnchor = transforms.FirstOrDefault(t => t.name == CommitAnchorName);
        var alloyAnchor = transforms.FirstOrDefault(t => t.name == AlloyAnchorName);

        int layer = LayerMask.NameToLayer("InteractiveObjects");
        if (layer < 0)
        {
            BepinPlugin.Log.LogWarning("[Forge] Layer 'InteractiveObjects' not found — interactables will not be raycast-targetable.");
            layer = gameObject.layer;
        }

        foreach (var tube in _tubeAnchors)
            CreateInteractable(tube, ForgeInteractableKind.RelicTube, new Vector3(0.35f, 0.35f, 0.35f), layer);
        if (_inputAnchor != null)
            // Oversized relative to a docked BuildBox so loading is forgiving to
            // aim; while a box is docked and the player is empty-handed the
            // interactable steps aside (ForgeInteractable.IsInteractive) so the
            // box itself can be grabbed back out.
            CreateInteractable(_inputAnchor, ForgeInteractableKind.ModuleSocket, new Vector3(1.2f, 1.2f, 1.2f), layer);
        if (commitAnchor != null)
            CreateInteractable(commitAnchor, ForgeInteractableKind.CommitButton, new Vector3(0.3f, 0.3f, 0.3f), layer);
        else
            BepinPlugin.Log.LogWarning("[Forge] Prefab has no CommitTarget anchor — in-world commits unavailable (use !forgecommit).");
        if (alloyAnchor != null)
            CreateInteractable(alloyAnchor, ForgeInteractableKind.AlloyTerminal, new Vector3(0.3f, 0.3f, 0.3f), layer);
        else
            BepinPlugin.Log.LogInfo("[Forge] Prefab has no AlloyTarget anchor — alloy feeding unavailable in-world (use !setmeter for testing).");

        if (_tubeAnchors.Length == 0 || _inputAnchor == null)
            BepinPlugin.Log.LogWarning(
                $"[Forge] Prefab anchors incomplete (tubes={_tubeAnchors.Length}, input={(_inputAnchor != null ? "ok" : "missing")}) — " +
                "check the metem bundle matches UpgradeForgeModuleCell.prefab.");
        else
            BepinPlugin.Log.LogInfo($"[Forge] Built interactables: {_tubeAnchors.Length} relic tubes, module socket{(commitAnchor != null ? ", commit button" : "")}.");

        RefreshTubeVisibility();
    }

    private void OnEnable() => ForgeMeterController.LevelChanged += OnForgeLevelChanged;
    private void OnDisable() => ForgeMeterController.LevelChanged -= OnForgeLevelChanged;
    private void OnForgeLevelChanged(int _) => RefreshTubeVisibility();

    // The model reflects Forge progression: only the first Capacity tubes are
    // active. Deactivating a tube anchor hides everything under it — the click
    // target (inactive collider = unclickable), Highlight/Filled helpers, and the
    // tube's mesh when the prefab parents it under the anchor — so locked tubes
    // are enforced physically as well as by the insertion count check. A tube
    // holding a docked relic never hides (level can drop via dev/reset).
    private void RefreshTubeVisibility()
    {
        for (int i = 0; i < _tubeAnchors.Length; i++)
        {
            var tube = _tubeAnchors[i];
            if (tube == null) continue;
            bool active = i < Capacity || IsAnchorOccupied(tube);
            if (tube.gameObject.activeSelf != active)
                tube.gameObject.SetActive(active);
        }
    }

    // Prefab authoring contract (all optional, plain Unity components so they survive
    // the metem bundle): a Collider on the anchor itself or on a child named
    // "ClickTarget" becomes the click region instead of the generated default box;
    // a disabled child named "Highlight" is shown while the player hovers; a disabled
    // child named "Filled" is shown while an item is docked on that anchor.
    private void CreateInteractable(Transform anchor, ForgeInteractableKind kind, Vector3 size, int layer)
    {
        GameObject go;
        var authored = anchor.GetComponent<Collider>();
        if (authored == null)
            authored = FindDeep(anchor, "ClickTarget")?.GetComponent<Collider>();

        if (authored != null)
        {
            // Click regions must not collide — enforce trigger regardless of how the
            // collider was authored.
            authored.isTrigger = true;
            go = authored.gameObject;
        }
        else
        {
            go = new GameObject($"ForgeInteractable_{kind}");
            go.transform.SetParent(anchor, false);
            var col = go.AddComponent<BoxCollider>();
            col.isTrigger = true;
            // The anchors ride under FBX nodes with tiny non-uniform scales, so the
            // requested world-space size must be divided out of the inherited scale.
            var lossy = anchor.lossyScale;
            col.size = new Vector3(
                size.x / Mathf.Max(Mathf.Abs(lossy.x), 1e-4f),
                size.y / Mathf.Max(Mathf.Abs(lossy.y), 1e-4f),
                size.z / Mathf.Max(Mathf.Abs(lossy.z), 1e-4f));
        }

        // Layer is always forced at runtime — the editor project's layer table does
        // not match the game's, so authored layer indices can't be trusted.
        go.layer = layer;

        // Highlight / Filled helpers are visual-only; primitives authored in the
        // editor often keep their default colliders, which would collide with docked
        // items and block the interact ray. Strip them.
        StripHelperColliders(anchor, "Highlight");
        StripHelperColliders(anchor, "Filled");

        var fi = go.GetComponent<ForgeInteractable>();
        if (fi == null) fi = go.AddComponent<ForgeInteractable>();
        fi.Forge = this;
        fi.Kind = kind;
        fi.Anchor = anchor;
        fi.ShowContextInfo = false;
        fi.InteractionInfo = ForgeInteractable.InfoFor(kind);
    }

    // Entry point for all Forge clicks, invoked by the CarryableInteract prefix in
    // ForgeInteractionPatch. Runs on the interacting player's client.
    public void HandleInteraction(ForgeInteractable target, LocalPlayer player)
    {
        var payload = player.Payload;
        if (payload != null)
        {
            if (payload is BuildBox box)
            {
                if (target.Kind != ForgeInteractableKind.ModuleSocket)
                { Messaging.Notification("Place module boxes on the Forge's module socket."); return; }
                if (HasModule)
                { Messaging.Notification("The Forge already holds a module box."); return; }

                player.Carrier.ReleaseCarryable();
                TryTakeModule(box);
                Dock(box.gameObject, _inputAnchor != null ? _inputAnchor : transform);
                Messaging.Notification($"Module loaded (L{CurrentBoxLevel}). Insert relics and commit to upgrade.");
            }
            else if (IsRelic(payload.gameObject))
            {
                // Relics go into the specific tube the player clicked.
                if (target.Kind != ForgeInteractableKind.RelicTube)
                { Messaging.Notification("Insert relics into the relic tubes."); return; }
                if (target.Anchor == null || IsAnchorOccupied(target.Anchor))
                { Messaging.Notification("That tube is occupied — pick an empty one."); return; }

                if (!TryInsertRelic(payload.gameObject))
                { Messaging.Notification($"The Forge is full ({RelicCount}/{Capacity} relics)."); return; }

                player.Carrier.ReleaseCarryable();
                Dock(payload.gameObject, target.Anchor);
                Messaging.Notification($"Relic inserted ({RelicCount}/{Capacity}). Projected level: L{ProjectedTargetLevel}.");
            }
            else
            {
                Messaging.Notification("The Forge only accepts relics and module boxes.");
            }
            return;
        }

        // Empty-handed: commit lives on the dedicated commit button. Docked relics
        // and the docked box are retrieved by grabbing them directly — the module
        // socket's IsInteractive steps aside while it holds a box, so an empty-hand
        // click only reaches it when the socket is empty.
        switch (target.Kind)
        {
            case ForgeInteractableKind.CommitButton:
                DoCommit();
                break;
            case ForgeInteractableKind.AlloyTerminal:
                if (ForgeMeterController.TrySpendAlloys(out var alloyError))
                    Messaging.Notification(ForgeMeterController.Describe());
                else
                    Messaging.Notification(alloyError);
                break;
            case ForgeInteractableKind.ModuleSocket:
                Messaging.Notification("Deconstruct a module and place its build box here to upgrade it.");
                break;
            case ForgeInteractableKind.RelicTube:
                Messaging.Notification(HasModule
                    ? $"Forge: L{CurrentBoxLevel} module loaded, {RelicCount}/{Capacity} relics, projected L{ProjectedTargetLevel}."
                    : $"Forge: no module loaded, {RelicCount}/{Capacity} relics.");
                break;
        }
    }

    private void DoCommit()
    {
        switch (TryCommit(out int newLevel, out int consumed, out string perkResult))
        {
            case CommitResult.Ok:
                Messaging.Notification($"Upgrade committed: L{newLevel} (consumed {consumed} relic{(consumed == 1 ? "" : "s")}). Rebuild the module to apply.");
                if (perkResult != null) Messaging.Notification(perkResult);
                break;
            case CommitResult.NoModule:
                Messaging.Notification("Load a deconstructed module box into the Forge first.");
                break;
            case CommitResult.NoRelics:
                Messaging.Notification("Insert relics into the tubes before committing.");
                break;
            case CommitResult.AlreadyAtMax:
                Messaging.Notification("This module is already at L10.");
                break;
            case CommitResult.InsufficientRelics:
                Messaging.Notification($"Next level requires {ForgeCostCurve.CostForNextLevel(CurrentBoxLevel)} relics; the Forge holds {RelicCount}.");
                break;
            case CommitResult.MissingViewId:
                Messaging.Notification("Forge error: module box has no network identity.");
                break;
            case CommitResult.InvalidModuleLevel:
                Messaging.Notification("Only Mark III modules can be forged — upgrade it with module chips first.");
                break;
        }
    }

    // ---- Physical docking ------------------------------------------------

    private void Dock(GameObject item, Transform anchor)
    {
        if (item == null || anchor == null) return;
        _docked[item] = anchor;
        var co = item.GetComponent<CarryableObject>();
        var rb = co != null ? co.MainRigidbody : item.GetComponent<Rigidbody>();
        if (rb != null) rb.isKinematic = true;
        PlaceAtAnchor(item, co, anchor);
        SetAnchorFilled(anchor, true);
    }

    // Optional prefab-authored fill indicator: a disabled child named "Filled" under
    // the anchor is toggled while something is docked there.
    private static void SetAnchorFilled(Transform anchor, bool filled)
    {
        var indicator = FindDeep(anchor, "Filled");
        if (indicator != null) indicator.gameObject.SetActive(filled);
    }

    private static void StripHelperColliders(Transform anchor, string helperName)
    {
        var helper = FindDeep(anchor, helperName);
        if (helper == null) return;
        foreach (var col in helper.GetComponentsInChildren<Collider>(true))
        {
            BepinPlugin.Log.LogDebug($"[Forge] Removing stray collider from {helperName} helper under {anchor.name}.");
            Destroy(col);
        }
    }

    // Depth-first name search through an anchor's subtree, so authored helper objects
    // (ClickTarget / Highlight / Filled) may sit anywhere below the anchor — e.g. a
    // duplicated FBX node kept inside a wrapper to preserve its transform chain.
    internal static Transform FindDeep(Transform root, string name)
    {
        if (root == null) return null;
        var direct = root.Find(name);
        if (direct != null) return direct;
        foreach (Transform child in root)
        {
            var hit = FindDeep(child, name);
            if (hit != null) return hit;
        }
        return null;
    }

    // Base-pivot alignment: the item's BasePivot lands on the anchor's origin with
    // its axes matching the anchor's axes (anchor green/Y = item up, blue/Z = item
    // facing). Same intent as CarryablesSocket.PlaceCarryableOnSocket, but computed
    // with quaternions instead of the anchor's matrices — vanilla store transforms
    // are unit-scale, while our anchors inherit rotated, non-uniformly scaled FBX
    // nodes whose matrices skew a rotation extracted from them.
    private static void PlaceAtAnchor(GameObject item, CarryableObject co, Transform anchor)
    {
        var itemTr = item.transform;
        var pivot = co != null ? co.BasePivot : itemTr;
        var finalRot = anchor.rotation * Quaternion.Inverse(pivot.rotation) * itemTr.rotation;
        var delta = finalRot * Quaternion.Inverse(itemTr.rotation);
        var finalPos = anchor.position - delta * (pivot.position - itemTr.position);
        itemTr.SetPositionAndRotation(finalPos, finalRot);
    }

    // Whether something is physically docked on the given anchor. Used by
    // ForgeInteractable to step aside so docked items can be grabbed directly.
    public bool IsAnchorOccupied(Transform anchor) =>
        anchor != null && _docked.ContainsValue(anchor);

    // Reconcile forge state with the world: players grab docked items back out via
    // the vanilla Grabbable flow, and commits destroy consumed relics. Both surface
    // here as docked entries whose item is gone or carried again.
    private void Update()
    {
        _relics.RemoveAll(r => r == null);
        if (!ReferenceEquals(_moduleBox, null) && _moduleBox == null) _moduleBox = null;
        if (_docked.Count == 0) return;

        foreach (var kv in _docked)
        {
            var go = kv.Key;
            if (go == null) { _undockScratch.Add(kv); continue; }
            var co = go.GetComponent<CarryableObject>();
            if (co != null && co.Carrier != null) _undockScratch.Add(kv);
        }

        foreach (var kv in _undockScratch)
        {
            var go = kv.Key;
            _docked.Remove(go);
            SetAnchorFilled(kv.Value, false);
            if (go == null) continue;

            var co = go.GetComponent<CarryableObject>();
            var rb = co != null ? co.MainRigidbody : null;
            if (rb != null) rb.isKinematic = false;

            var box = go.GetComponent<BuildBox>();
            if (box != null && box == _moduleBox)
            {
                TryReleaseModule(out _);
                BepinPlugin.Log.LogInfo($"[Forge] Module box {go.name} retrieved from socket.");
            }
            else if (_relics.Remove(go))
            {
                BepinPlugin.Log.LogInfo($"[Forge] Relic {go.name} retrieved ({RelicCount}/{Capacity} remain).");
            }
        }
        _undockScratch.Clear();
    }

    // Keep docked items pinned to their anchors while the ship moves.
    private void LateUpdate()
    {
        if (_docked.Count == 0) return;
        foreach (var kv in _docked)
        {
            if (kv.Key == null || kv.Value == null) continue;
            PlaceAtAnchor(kv.Key, kv.Key.GetComponent<CarryableObject>(), kv.Value);
        }
    }

    // Consumed relics are networked objects — destroy through the game's factory
    // when we own them so the removal replicates; plain Destroy otherwise.
    private static void DestroyRelic(GameObject relic)
    {
        if (relic == null) return;
        var co = relic.GetComponent<CarryableObject>();
        if (co != null && co.photonView != null && co.photonView.AmOwner)
            ObjectFactory.DestroyCloneStarObject(co);
        else
            Destroy(relic);
    }

    // ---- Helpers ------------------------------------------------------

    // A GameObject is a relic when its carryable carries the game's canonical relic
    // CsTag (RuntimeAssetTable.RelicTag — the same check the vanilla relic shrine
    // filter resolves to, and the tag RuntimeCarryable stamps on modded relics).
    // Name matching against RelicTierData / the "Relic_" prefix remains as a fallback
    // for objects that aren't tagged carryables.
    public static bool IsRelic(GameObject go)
    {
        if (go == null) return false;

        var carryable = go.GetComponent<CarryableObject>();
        var relicTag = CsTagRegistry.Relic;
        if (carryable != null && relicTag != null && carryable.CsTags != null &&
            System.Array.IndexOf(carryable.CsTags, relicTag) >= 0)
            return true;

        if (RelicTierData.TryGet(go.name, out _)) return true;
        var normalized = RelicTierData.NormalizeName(go.name);
        return !string.IsNullOrEmpty(normalized) &&
               normalized.StartsWith("Relic_", System.StringComparison.Ordinal);
    }

    // Locate the closest Forge instance to a world position. Nullable — returns null
    // if no Forge is currently installed on the ship.
    public static UpgradeForgeBehavior FindNearest(Vector3 worldPosition)
    {
        UpgradeForgeBehavior nearest = null;
        float bestSqr = float.PositiveInfinity;
        foreach (var forge in UnityEngine.Object.FindObjectsOfType<UpgradeForgeBehavior>())
        {
            float d = (forge.transform.position - worldPosition).sqrMagnitude;
            if (d < bestSqr) { bestSqr = d; nearest = forge; }
        }
        return nearest;
    }
}
