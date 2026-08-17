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
// Responsibilities:
//   - Hold at most one BuildBox in the module socket (the target being upgraded).
//   - Hold up to Capacity relics in the relic slots.
//   - Enforce the cost curve on commit.
//   - Persist the new level via ForgeStateStore.SaveSnapshot so the level rides
//     the BuildBox through reconstruction — ForgePersistPatch does the restoration.
//
// In-world interaction model (see also ForgeInteractionPatch):
//   - BuildInteractables() spawns click targets on the prefab's named anchors:
//     RelicTubeTarget (×6), InputTarget (module socket), and an optional
//     CommitTarget. Tubes/socket/alloy terminal are ForgeInteractable, routed here
//     via the CarryableInteract prefix (a click). CommitTarget is a
//     ForgeCommitInteractable, routed here via its own hold-completion — committing
//     is irreversible, so it requires a deliberate hold rather than a click.
//   - Inserted relics / the loaded BuildBox stay live in the world: they are docked
//     kinematically to their anchor (LateUpdate keeps them pinned while the ship
//     moves) and remain grabbable. Update() reconciles state when a player grabs a
//     docked item back out or a commit destroys consumed relics.
public class UpgradeForgeBehavior : MonoBehaviour
{
    // Relic capacity is the Forge's progression level. Filling the meter — sector
    // jumps + alloys — is what unlocks the bigger upgrade steps.
    public static int Capacity => ForgeMeterController.Capacity;

    // Name of the shipped prefab inside voidcrewterminus.metem — used by
    // ForgeInteractionPatch to identify Forge modules as they build.
    public const string PrefabName = "UpgradeForgeModuleCell";

    // Name of the Forge's own dedicated BuildBox prefab, shipped in the same
    // bundle. AssetLoader dispatches on this name (vs. PrefabName) to tell the
    // two apart — both are bare VoidCrewAsset-marked GameObjects with no other
    // surviving components, so name is the only signal available at load time.
    public const string BuildBoxPrefabName = "UpgradeForgeBuildBox";

    // Anchor names baked into the shipped prefab. CommitTarget is required for
    // in-world commits; AlloyTarget is the meter terminal (optional — the
    // !setmeter dev command covers testing until the prefab gains the anchor).
    // Handle and DeconstructTrigger are two different objects on the prefab:
    // Handle is a visual-only part of the modeled mesh (purely cosmetic — the
    // rotating lever ForgeDeconstructInteractable animates while held) and
    // DeconstructTrigger is a dedicated, hand-authored-and-sized Collider (the
    // actual click/raycast target). Conflating them under one anchor name meant
    // the click region either had to reuse Handle's own (nonexistent) collider or
    // fall back to BuildAnchorClickRegion's generated box — sized by dividing a
    // world-space size by the anchor's lossyScale, which can balloon past the
    // intended bounds on odd FBX import scales and steal raycasts aimed at a
    // neighboring module's own deconstruct lever. Using DeconstructTrigger's own
    // authored Collider directly sidesteps the generated-fallback path entirely.
    public const string RelicTubeAnchorName = "RelicTubeTarget";
    public const string InputAnchorName = "InputTarget";
    public const string CommitAnchorName = "CommitTarget";
    public const string AlloyAnchorName = "AlloyTarget";
    public const string DeconstructHandleName = "Handle";
    public const string DeconstructTriggerName = "DeconstructTrigger";

    // The Commit lever's own visual mesh, buried in the FBX-imported hierarchy
    // like Handle (so it never shows up as its own YAML block when grepping the
    // .prefab — found fine at runtime via GetComponentsInChildren, same as
    // Handle). Scopes ForgeCommitInteractable's outline highlight to just this
    // part instead of the whole module (see ForgeOutline).
    public const string CommitLeverBoxName = "LeverBox";

    private BuildBox _moduleBox;
    private readonly List<GameObject> _relics = new();

    // The items physically held on this Forge's anchors. AnchorDock owns their
    // physics; this class owns what they MEAN — which are relics, which is the
    // module, and who has to be told when one leaves.
    private readonly AnchorDock _dock = new();
    private readonly List<KeyValuePair<GameObject, Transform>> _grabbedScratch = new();
    private Transform[] _tubeAnchors = System.Array.Empty<Transform>();
    private Transform _inputAnchor;
    private bool _interactablesBuilt;

    // The translucent blue "this fits here" preview. Local and cosmetic only —
    // see ForgeGhosts.
    private readonly ForgeGhosts _ghosts = new();
    private float _ghostRefreshCountdown;

    // Vanilla SocketOutlines re-decides which sockets preview on a 0.2s
    // InvokeRepeating; matched here so the Forge's previews pop in and out on the
    // same cadence as the ship's own.
    private const float GhostRefreshInterval = 0.2f;

    public bool HasModule => _moduleBox != null;
    public int RelicCount => _relics.Count;
    public BuildBox ModuleBox => _moduleBox;
    public IReadOnlyList<GameObject> Relics => _relics;

    public int CurrentBoxLevel => LevelOfBox(_moduleBox);

    // Static so the host can compute a client-operated box's level from the box
    // resolved by ViewID (the host's own forge instance has no _moduleBox when a
    // client docked — docking is a local interaction).
    internal static int LevelOfBox(BuildBox box)
    {
        if (box == null || box.photonView == null) return 0;

        // Only a module at its final vanilla mark may be forged; below that the
        // vanilla upgrade-chip path still applies.
        int mark = GetBoxMark(box, out bool isFinalMark);
        if (!isFinalMark) return mark; // 1 or 2 → below MinLevel → InvalidModuleLevel on commit

        return ForgeStateStore.TryPeekSnapshot(box.photonView.ViewID, out var snap)
            ? snap.Level
            : ForgeCostCurve.MinLevel;
    }

    // STRICT policy: only modules provably at the END of an upgrade chain are
    // forgeable — anything we can't resolve (no identity, table missing, guid in
    // no chain) is refused. The permissive alternative ("unknown = final") let
    // MkI/MkII modules with unresolvable identities slip through.
    //
    // Identity differs by box type: composite weapon boxes are GENERIC prefabs —
    // their moduleRef is unset and the weapon identity is a CompositeWeaponDataRef
    // delivered via instantiation data — and their upgrade chains are keyed by
    // that CompositeData guid (see ModuleUpgraderEffects). Plain module boxes
    // chain by their moduleRef guid.
    private int GetBoxMark(out bool isFinalMark) => GetBoxMark(_moduleBox, out isFinalMark);

    private static int GetBoxMark(BuildBox box, out bool isFinalMark)
    {
        isFinalMark = false;
        if (!TryGetBoxIdentity(box, out var guid)) return 1;

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

        // If a legitimately single-form module ever needs to forge, this log line
        // names the guid to whitelist.
        BepinPlugin.Log.LogInfo($"[Forge] Module {guid.AsHex()} not in any upgrade chain — refusing to forge (strict Mark III policy).");
        return 1;
    }

    private bool TryGetBoxIdentity(out GUIDUnion guid) => TryGetBoxIdentity(_moduleBox, out guid);

    private static bool TryGetBoxIdentity(BuildBox box, out GUIDUnion guid)
    {
        guid = GUIDUnion.Empty();
        if (box == null) return false;

        if (box is CompositeWeaponBuildBox weaponBox)
        {
            if (weaponBox.WeaponDataRef == null || weaponBox.WeaponDataRef.IsNull) return false;
            guid = weaponBox.WeaponDataRef.AssetGuid;
            return true;
        }

        var moduleRef = box.moduleRef;
        if (moduleRef == null || moduleRef.IsNull) return false;
        guid = moduleRef.AssetGuid;
        return true;
    }

    // !forgemark dev command: full dump of how the docked box's mark resolves.
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

    // Equal to CurrentBoxLevel when nothing is loaded, or when the next-level cost
    // exceeds the inserted relic count.
    public int ProjectedTargetLevel => ForgeCostCurve.MaxReachable(CurrentBoxLevel, _relics.Count);

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

    // Consumes only the relics actually spent; leftovers stay in the Forge. On
    // success the new pending state (level + any rolled perk) is written to
    // ForgeStateStore so reconstruction picks it up automatically.
    //
    // Local operator entry (host / solo): ForgeCommit computes, persists and
    // broadcasts the authoritative outcome; consuming OUR relics stays here
    // because we own them (which is what makes the networked destroy propagate).
    // A client operator never reaches this — the policy routes its click to
    // RequestCommit instead.
    public CommitOutcome TryCommit()
    {
        var outcome = ForgeCommit.Execute(_moduleBox, _relics);
        if (outcome.Status != CommitStatus.Ok) return outcome;
        ConsumeOwnedRelics(outcome.RelicsConsumed);
        return outcome;
    }

    private void ConsumeOwnedRelics(int count)
    {
        for (int i = 0; i < count && _relics.Count > 0; i++)
        {
            var relic = _relics[0];
            _relics.RemoveAt(0);
            DestroyRelic(relic);
        }
    }

    // ViewIDs of the relics currently docked here (for a client's commit request
    // to the host).
    internal int[] RelicViewIds()
    {
        var ids = new List<int>(_relics.Count);
        foreach (var r in _relics)
        {
            var pv = r != null ? r.GetComponent<Photon.Pun.PhotonView>() : null;
            if (pv != null && pv.ViewID > 0) ids.Add(pv.ViewID);
        }
        return ids.ToArray();
    }

    // The host's authoritative commit result arrived. If we're the operator (we
    // hold the relics), consume our share and notify; non-operators (empty
    // tubes) no-op. The snapshot itself is applied by ForgeNetSync.
    internal void OnNetworkCommitResult(int relicsConsumed)
    {
        if (_relics.Count == 0) return; // not the operator
        int before = _relics.Count;
        ConsumeOwnedRelics(relicsConsumed);
        Messaging.Notification(
            $"Upgrade committed by the host (consumed {ForgeLabels.Plural(before - _relics.Count, "relic")}). " +
            "Rebuild the module to apply.");
    }

    // Docking is a LOCAL interaction: HandleInteraction runs only for the player
    // who clicked, so before this every other player saw an empty Forge no matter
    // how many relics were loaded. The operator announces each dock/undock and
    // everyone else mirrors it.
    //
    // Only the paths that ORIGINATE a dock announce it — the two Apply arms and
    // the Update reconcile. The mirroring paths below deliberately say nothing,
    // which is what stops two clients echoing each other forever.

    // Find the forge behaviour operating a given module box (by its Photon
    // ViewID), across all installed forges.
    internal static UpgradeForgeBehavior FindByViewId(int forgeViewId)
    {
        var pv = Photon.Pun.PhotonView.Find(forgeViewId);
        return pv != null ? pv.GetComponent<UpgradeForgeBehavior>() : null;
    }

    internal int ForgeViewId
    {
        get
        {
            var module = GetComponent<CellModule>();
            return module != null && module.photonView != null ? module.photonView.ViewID : 0;
        }
    }

    // -1 = the module socket; >= 0 indexes _tubeAnchors. Anchors are ordered by
    // name (BuildInteractables sorts them), so the index means the same thing on
    // every client running the same prefab.
    private int AnchorIndexOf(Transform anchor)
    {
        if (anchor == null) return -1;
        if (anchor == _inputAnchor) return -1;
        for (int i = 0; i < _tubeAnchors.Length; i++)
            if (_tubeAnchors[i] == anchor) return i;
        return -1;
    }

    private Transform AnchorFromIndex(int index)
    {
        if (index < 0) return _inputAnchor != null ? _inputAnchor : transform;
        return index < _tubeAnchors.Length ? _tubeAnchors[index] : null;
    }

    internal void ApplyRemoteDock(int itemViewId, int anchorIndex)
    {
        var pv = Photon.Pun.PhotonView.Find(itemViewId);
        if (pv == null) return;
        var go = pv.gameObject;
        if (go == null || _dock.IsDocked(go)) return;

        var anchor = AnchorFromIndex(anchorIndex);
        if (anchor == null) return;

        // Mirror the bookkeeping so RelicCount / HasModule read correctly for
        // observers too. The commit itself stays host-authoritative and is
        // resolved from ViewIDs, so a mirrored list can't affect an outcome.
        var box = go.GetComponent<BuildBox>();
        if (box != null) _moduleBox ??= box;
        else if (!_relics.Contains(go)) _relics.Add(go);

        _dock.Dock(go, anchor); // no BroadcastDock — mirroring, not originating

        BepinPlugin.Log.LogDebug($"[Net] ← applied dock item={itemViewId} anchor={anchorIndex} on forge={ForgeViewId}.");
    }

    internal void ApplyRemoteUndock(int itemViewId)
    {
        var pv = Photon.Pun.PhotonView.Find(itemViewId);
        if (pv == null) return;
        var go = pv.gameObject;
        if (go == null || !_dock.Undock(go)) return; // not docked here — nothing to mirror

        var box = go.GetComponent<BuildBox>();
        if (box != null && box == _moduleBox) _moduleBox = null;
        else _relics.Remove(go);

        BepinPlugin.Log.LogDebug($"[Net] ← applied undock item={itemViewId} on forge={ForgeViewId}.");
    }

    private void BroadcastDock(GameObject item, Transform anchor, bool docked)
    {
        var pv = item != null ? item.GetComponent<Photon.Pun.PhotonView>() : null;
        if (pv == null) return;
        Net.ForgeNetSync.BroadcastDock(ForgeViewId, pv.ViewID, AnchorIndexOf(anchor), docked);
    }

    internal static UpgradeForgeBehavior FindByBoxViewId(int boxViewId)
    {
        foreach (var b in FindObjectsOfType<UpgradeForgeBehavior>())
            if (b._moduleBox != null && b._moduleBox.photonView != null && b._moduleBox.photonView.ViewID == boxViewId)
                return b;
        return null;
    }

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
        var commitLeverBox = transforms.FirstOrDefault(t => t.name == CommitLeverBoxName);
        var alloyAnchor = transforms.FirstOrDefault(t => t.name == AlloyAnchorName);
        var deconstructHandle = transforms.FirstOrDefault(t => t.name == DeconstructHandleName);
        var deconstructTrigger = transforms.FirstOrDefault(t => t.name == DeconstructTriggerName);

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
        {
            CreateCommitInteractable(commitAnchor, commitLeverBox, new Vector3(0.3f, 0.3f, 0.3f), layer);
            if (commitLeverBox == null)
                BepinPlugin.Log.LogInfo("[Forge] Prefab has no LeverBox — Commit will outline the whole module instead of just the lever.");
        }
        else
            BepinPlugin.Log.LogWarning("[Forge] Prefab has no CommitTarget anchor — in-world commits unavailable (use !forgecommit).");
        if (alloyAnchor != null)
            CreateInteractable(alloyAnchor, ForgeInteractableKind.AlloyTerminal, new Vector3(0.3f, 0.3f, 0.3f), layer);
        else
            BepinPlugin.Log.LogInfo("[Forge] Prefab has no AlloyTarget anchor — alloy feeding unavailable in-world (use !setmeter for testing).");
        if (deconstructTrigger != null)
        {
            CreateDeconstructInteractable(deconstructTrigger, deconstructHandle, layer);
            if (deconstructHandle == null)
                BepinPlugin.Log.LogInfo("[Forge] Prefab has no Handle — deconstruct works but the lever won't animate.");
        }
        else
            BepinPlugin.Log.LogInfo("[Forge] Prefab has no DeconstructTrigger — in-world deconstruct unavailable.");

        if (_tubeAnchors.Length == 0 || _inputAnchor == null)
            BepinPlugin.Log.LogWarning(
                $"[Forge] Prefab anchors incomplete (tubes={_tubeAnchors.Length}, input={(_inputAnchor != null ? "ok" : "missing")}) — " +
                "check the metem bundle matches UpgradeForgeModuleCell.prefab.");
        else
            BepinPlugin.Log.LogInfo($"[Forge] Built interactables: {_tubeAnchors.Length} relic tubes, module socket{(commitAnchor != null ? ", commit button" : "")}.");

        RefreshTubeVisibility();
    }

    private void OnEnable() => ForgeMeterController.LevelChanged += OnForgeLevelChanged;

    private void OnDisable()
    {
        ForgeMeterController.LevelChanged -= OnForgeLevelChanged;
        // Nothing else will tick these while we're off — don't leave a hologram
        // floating in a Forge that has stopped running.
        _ghosts.Clear();
    }

    private void OnForgeLevelChanged(int _) => RefreshTubeVisibility();

    // Which anchors should be showing a preview, and of what. Runs on the slow tick:
    // the answer only changes when the player picks something up, walks up to a
    // different Forge, or fills a tube.
    //
    // Acceptance is asked of ForgeInteractionPolicy — the same call HandleInteraction
    // makes — rather than re-derived from HasModule/RelicCount here. That is the whole
    // point: the preview appears exactly when the click would be taken, so it can't
    // drift into promising an insert the Forge would then refuse.
    private void RefreshGhosts()
    {
        if (!_interactablesBuilt) return;

        var payload = LocalPlayer.Instance != null ? LocalPlayer.Instance.Payload : null;
        var carried = ClassifyPayload(payload);

        // Empty-handed, or holding something the Forge has no anchor for. Bailing
        // here also keeps LevelOfBox's upgrade-chain walk off the tick entirely
        // unless a module box is actually in hand.
        if (carried != ForgePayload.ModuleBox && carried != ForgePayload.Relic)
        {
            _ghosts.Clear();
            return;
        }

        var view = SnapshotView();
        int carriedLevel = carried == ForgePayload.ModuleBox ? LevelOfBox((BuildBox)payload) : 0;

        PreviewAnchor(_inputAnchor, ForgeInteractableKind.ModuleSocket, view, carried, carriedLevel, payload);
        foreach (var tube in _tubeAnchors)
            PreviewAnchor(tube, ForgeInteractableKind.RelicTube, view, carried, carriedLevel, payload);
    }

    private void PreviewAnchor(Transform anchor, ForgeInteractableKind kind, in ForgeView view,
                               ForgePayload carried, int carriedLevel, CarryableObject payload)
    {
        if (anchor == null) return;

        // Tubes above the Forge's Capacity are deactivated wholesale by
        // RefreshTubeVisibility — a locked tube takes nothing, so it previews nothing.
        if (!anchor.gameObject.activeInHierarchy) { _ghosts.Hide(anchor); return; }

        var click = new ForgeClick(carried, carriedLevel, kind, IsAnchorOccupied(anchor));
        var action = ForgeInteractionPolicy.Decide(view, click).Action;

        if (action == ForgeAction.LoadModule || action == ForgeAction.InsertRelic)
            _ghosts.Show(anchor, payload);
        else
            _ghosts.Hide(anchor);
    }

    // The anchor the player's interact ray is currently on, or null. Read from
    // RaycastHandler.Current the same way vanilla's
    // CarryablesSocketActor.IsInteractableHighlighted does, rather than from
    // ForgeInteractable.Highlighted — the raycast is the authority on where the
    // player is looking, and reading it directly can't get out of step with a
    // highlight callback that was missed while an interactable was rebuilt.
    private Transform AimedAnchor()
    {
        var player = LocalPlayer.Instance;
        if (player == null || player.RaycastHandler == null) return null;
        return player.RaycastHandler.Current is ForgeInteractable fi && fi.Forge == this
            ? fi.Anchor
            : null;
    }

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

    private void CreateInteractable(Transform anchor, ForgeInteractableKind kind, Vector3 size, int layer)
    {
        var go = BuildAnchorClickRegion(anchor, $"ForgeInteractable_{kind}", size, layer);

        var fi = go.GetComponent<ForgeInteractable>();
        if (fi == null) fi = go.AddComponent<ForgeInteractable>();
        fi.Forge = this;
        fi.Kind = kind;
        fi.Anchor = anchor;
        fi.ShowContextInfo = false;
        fi.InteractionInfo = ForgeInteractable.InfoFor(kind);
    }

    // The Commit button is held, not clicked (see ForgeCommitInteractable) — a
    // different component, driven by a different vanilla input pathway
    // (EnvironmentInteract's Hold action, not CarryableInteract's fire1Action), so
    // it can't share ForgeInteractable's AbstractInteractable base. Everything about
    // *finding or building the click region itself* is identical, though.
    private void CreateCommitInteractable(Transform anchor, Transform leverBox, Vector3 size, int layer)
    {
        var go = BuildAnchorClickRegion(anchor, "ForgeInteractable_CommitButton", size, layer);

        var hc = go.GetComponent<ForgeCommitInteractable>();
        if (hc == null) hc = go.AddComponent<ForgeCommitInteractable>();
        hc.Forge = this;
        hc.Anchor = anchor;
        hc.OutlineTarget = leverBox;
        hc.ShowContextInfo = false;
        // Must be set before Unity calls Start() (ClickerInteractable.SetClickable
        // there would otherwise stomp the InteractionInfo assignment below back to
        // whatever it captured — null — in Awake). See ForgeCommitInteractable's
        // doc comment for the full lifecycle reasoning.
        hc.DontSelfSetInteractionInfo = true;
        hc.InteractionInfo = ForgeInteractable.InfoFor(ForgeInteractableKind.CommitButton);
    }

    // The deconstruct handle (ForgeDeconstructInteractable) — same hold-to-confirm
    // mechanism as Commit. Click detection and the visual lever are deliberately
    // separate objects: trigger carries its own hand-authored, hand-sized
    // Collider, so BuildAnchorClickRegion's "authored collider" branch always
    // fires for it — never the generated-box fallback (which, sized off a
    // possibly-tiny FBX anchor lossyScale, is what let the Forge's deconstruct
    // region balloon past its intended bounds and steal clicks meant for a
    // neighboring module). handle is purely cosmetic — the mesh part
    // ForgeDeconstructInteractable rotates for the pull animation — and is
    // optional; deconstruct still functions without it, just without the
    // visual feedback.
    private void CreateDeconstructInteractable(Transform trigger, Transform handle, int layer)
    {
        var go = BuildAnchorClickRegion(trigger, "ForgeInteractable_Deconstruct", new Vector3(0.2f, 0.2f, 0.2f), layer);

        var dc = go.GetComponent<ForgeDeconstructInteractable>();
        if (dc == null) dc = go.AddComponent<ForgeDeconstructInteractable>();
        dc.ShowContextInfo = false;
        dc.DontSelfSetInteractionInfo = true;
        dc.InteractionInfo = ForgeInteractable.DeconstructInfo();
        dc.VisualHandle = handle;
    }

    // Prefab authoring contract (all optional, plain Unity components so they survive
    // the metem bundle): a Collider on the anchor itself or on a child named
    // "ClickTarget" becomes the click region instead of the generated default box;
    // a disabled child named "Highlight" is shown while the player hovers; a disabled
    // child named "Filled" is shown while an item is docked on that anchor.
    private static GameObject BuildAnchorClickRegion(Transform anchor, string generatedName, Vector3 size, int layer)
    {
        GameObject go;
        var authored = anchor.GetComponent<Collider>();
        if (authored == null)
            authored = ForgeAnchors.FindDeep(anchor, ForgeAnchors.ClickTargetName)?.GetComponent<Collider>();

        if (authored != null)
        {
            // Click regions must not collide — enforce trigger regardless of how the
            // collider was authored.
            authored.isTrigger = true;
            go = authored.gameObject;
        }
        else
        {
            go = new GameObject(generatedName);
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

        ForgeAnchors.StripHelperColliders(anchor, ForgeAnchors.HighlightName);
        ForgeAnchors.StripHelperColliders(anchor, ForgeAnchors.FilledName);

        // Diagnostic: an oversized/mispositioned click region here would silently
        // steal raycasts aimed at a neighboring module's own interactables (see
        // the "deconstructing a different module hit the Forge instead" report).
        var builtCollider = go.GetComponent<Collider>();
        BepinPlugin.Log.LogDebug(
            $"[Forge] Click region '{generatedName}' on anchor '{anchor.name}': " +
            $"{(authored != null ? "authored" : "generated")} collider, " +
            $"world bounds center={builtCollider.bounds.center} size={builtCollider.bounds.size}");

        return go;
    }

    // Entry point for all Forge interactions. ForgeInteractable-backed kinds (tubes,
    // module socket, alloy terminal) arrive via the CarryableInteract prefix in
    // ForgeInteractionPatch; CommitButton arrives via ForgeCommitInteractable's own
    // hold-completion (a different vanilla input pathway — see its doc comment).
    // Runs on the interacting player's client.
    //
    // The rules themselves live in ForgeInteractionPolicy, which is Unity-free and
    // therefore testable; this reads the scene into facts, hands them over, and
    // carries out the answer. Everything decidable is decided before anything in
    // the world is touched.
    public void HandleInteraction(ForgeInteractableKind kind, Transform anchor, LocalPlayer player)
    {
        // Captured before any mutation — ReleaseCarryable clears player.Payload.
        var payload = player.Payload;
        var decision = ForgeInteractionPolicy.Decide(SnapshotView(), DescribeClick(kind, anchor, payload));

        Apply(decision.Action, anchor, player, payload);
        if (!string.IsNullOrEmpty(decision.Message))
            Messaging.Notification(decision.Message);
    }

    private ForgeView SnapshotView() => new(
        hasModule: HasModule,
        socketedBoxLevel: CurrentBoxLevel,
        socketedBoxHasViewId: _moduleBox != null && _moduleBox.photonView != null,
        relicCount: RelicCount,
        capacity: Capacity,
        isAuthority: Net.ForgeNetSync.IsAuthority);

    private ForgeClick DescribeClick(ForgeInteractableKind kind, Transform anchor, CarryableObject payload)
    {
        var box = payload as BuildBox;
        return new ForgeClick(
            payload: ClassifyPayload(payload),
            carriedBoxLevel: box != null ? LevelOfBox(box) : 0,
            target: kind,
            targetOccupied: anchor == null || IsAnchorOccupied(anchor));
    }

    private static ForgePayload ClassifyPayload(CarryableObject payload) =>
        payload == null ? ForgePayload.None
        : payload is BuildBox ? ForgePayload.ModuleBox
        : IsRelic(payload.gameObject) ? ForgePayload.Relic
        : ForgePayload.Other;

    // Carry out a decision. Nothing here re-checks a rule the policy already
    // applied, and nothing here decides what to say about a refusal.
    private void Apply(ForgeAction action, Transform anchor, LocalPlayer player, CarryableObject payload)
    {
        switch (action)
        {
            case ForgeAction.LoadModule:
                player.Carrier.ReleaseCarryable();
                TryTakeModule((BuildBox)payload);
                var socket = _inputAnchor != null ? _inputAnchor : transform;
                _dock.Dock(payload.gameObject, socket);
                BroadcastDock(payload.gameObject, socket, docked: true);
                break;

            case ForgeAction.InsertRelic:
                // Capacity and the tube were both cleared by the policy, so a
                // refusal here means the two disagree about the Forge's state.
                // Drop it rather than reprint a message the policy owns.
                if (!TryInsertRelic(payload.gameObject))
                {
                    BepinPlugin.Log.LogWarning(
                        "[Forge] Insert approved by policy but refused by the Forge — state disagreement, ignored.");
                    break;
                }
                player.Carrier.ReleaseCarryable();
                _dock.Dock(payload.gameObject, anchor);
                BroadcastDock(payload.gameObject, anchor, docked: true);
                break;

            case ForgeAction.Commit:
                // Levels and counts are read back AFTER the attempt: on success
                // the socketed box now reports its new level and the consumed
                // relics are gone, which is what DescribeCommit reports.
                var outcome = TryCommit();
                foreach (var line in ForgeLabels.DescribeCommit(outcome, CurrentBoxLevel, RelicCount))
                    Messaging.Notification(line);
                break;

            case ForgeAction.RequestCommit:
                // The client asks, the host rolls and broadcasts back.
                Net.ForgeNetSync.RequestCommit(_moduleBox.photonView.ViewID, RelicViewIds());
                break;

            case ForgeAction.FeedAlloy:
                if (ForgeMeterController.TrySpendAlloys(out var alloyError))
                {
                    Messaging.Notification(ForgeMeterController.Describe());
                    Net.ForgeNetSync.BroadcastState(); // host spent — propagate new meter/level
                }
                else
                    Messaging.Notification(alloyError);
                break;
        }
    }

    // Hot-reload teardown (ScriptEngine): a reloaded assembly brings its OWN
    // UpgradeForgeBehavior type, so this instance must leave cleanly — undocking
    // held items (restoring their physics so nothing is left frozen mid-air) and
    // removing itself. The reloaded assembly re-attaches on its own patch pass.
    public void TeardownForReload()
    {
        _ghosts.Clear();
        _dock.ReleaseAll();
        _relics.Clear();
        _moduleBox = null;
        Destroy(this);
    }

    // The physics itself lives in AnchorDock. What stays here is what a docked
    // item MEANS to the Forge — whether it is the module or a relic, and who has
    // to be told when it arrives or leaves.

    // Whether something is physically docked on the given anchor. Used by
    // ForgeInteractable to step aside so docked items can be grabbed directly.
    public bool IsAnchorOccupied(Transform anchor) => _dock.IsOccupied(anchor);

    // Reconcile forge state with the world: players grab docked items back out via
    // the vanilla Grabbable flow, and commits destroy consumed relics. The dock
    // reports the ones a player is now carrying — the destroyed ones it reaps
    // itself, and _relics has already dropped them on the line above.
    private void Update()
    {
        _relics.RemoveAll(r => r == null);
        if (!ReferenceEquals(_moduleBox, null) && _moduleBox == null) _moduleBox = null;

        _dock.Reconcile(_grabbedScratch);
        foreach (var kv in _grabbedScratch)
        {
            var go = kv.Key;

            // A player grabbed it: tell everyone else so their copy undocks too.
            BroadcastDock(go, kv.Value, docked: false);

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
        _grabbedScratch.Clear();

        // Which anchors preview is re-decided on the slow tick; which one is aimed at
        // is applied every frame, so hover feedback isn't up to 200ms behind the
        // crosshair. Both are cheap no-ops when nothing is being previewed.
        _ghostRefreshCountdown -= Time.deltaTime;
        if (_ghostRefreshCountdown <= 0f)
        {
            _ghostRefreshCountdown = GhostRefreshInterval;
            RefreshGhosts();
        }
        _ghosts.SetAimed(AimedAnchor());
    }

    // Keep docked items pinned to their anchors while the ship moves.
    private void LateUpdate() => _dock.Pin();

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

    // A GameObject is a relic when its carryable carries the game's canonical relic
    // CsTag (RuntimeAssetTable.RelicTag — the same check the vanilla relic shrine
    // filter resolves to, and the tag RuntimeCarryable stamps on modded relics).
    // Name matching against RelicTierData / the "Relic_" prefix remains as a fallback
    // for objects that aren't tagged carryables.
    public static bool IsRelic(GameObject go)
    {
        if (go == null) return false;

        var carryable = go.GetComponent<CarryableObject>();
        var relicTag = Utils.CsTagRegistry.Relic;
        if (carryable != null && relicTag != null && carryable.CsTags != null &&
            System.Array.IndexOf(carryable.CsTags, relicTag) >= 0)
            return true;

        if (Loot.RelicTierData.TryGet(go.name, out _)) return true;
        var normalized = Loot.RelicTierData.NormalizeName(go.name);
        return !string.IsNullOrEmpty(normalized) &&
               normalized.StartsWith("Relic_", System.StringComparison.Ordinal);
    }

    // Returns null if no Forge is currently installed on the ship.
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
