using System.Collections.Generic;
using System.Linq;
using CG.Client.Ship.Interactions;
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
//   - BuildInteractables() spawns click targets on the prefab's named anchors.
//     Tubes/socket/alloy terminal are ForgeInteractable (a click, via the
//     CarryableInteract prefix); CommitTarget is a ForgeCommitInteractable, held
//     rather than clicked because committing is irreversible.
//   - Docked relics and the BuildBox stay live in the world — kinematic, pinned to
//     their anchor by LateUpdate, still grabbable. Update() reconciles when a player
//     grabs one back out or a commit destroys it.
public class UpgradeForgeBehavior : MonoBehaviour
{
    // Relic capacity is the Forge's progression level. Filling the meter — sector
    // jumps + alloys — is what unlocks the bigger upgrade steps.
    public static int Capacity => ForgeMeterController.Capacity;

    // Name of the shipped prefab inside voidcrewterminus.metem — used by
    // ForgeInteractionPatch to identify Forge modules as they build.
    public const string PrefabName = "UpgradeForgeModuleCell";

    // AssetLoader dispatches on this name vs. PrefabName to tell the two apart —
    // both are bare VoidCrewAsset-marked GameObjects with no surviving components,
    // so the name is the only signal available at load time.
    public const string BuildBoxPrefabName = "UpgradeForgeBuildBox";

    // Anchor names baked into the shipped prefab. CommitTarget is required for
    // in-world commits; AlloyTarget is the meter terminal (optional — !setmeter
    // covers testing until the prefab gains the anchor).
    //
    // Handle and DeconstructTrigger are deliberately separate: Handle is cosmetic
    // mesh, DeconstructTrigger a hand-sized authored Collider. Conflating them fell
    // back to BuildAnchorClickRegion's generated box, which on odd FBX import scales
    // balloons past its bounds and steals raycasts aimed at a neighboring module.
    public const string RelicTubeAnchorName = "RelicTubeTarget";
    public const string InputAnchorName = "InputTarget";
    public const string CommitAnchorName = "CommitTarget";
    public const string AlloyAnchorName = "AlloyTarget";
    public const string DeconstructHandleName = "Handle";
    public const string DeconstructTriggerName = "DeconstructTrigger";

    // Scopes ForgeCommitInteractable's outline to the lever instead of the whole
    // module. Buried in the FBX hierarchy like Handle, so it has no YAML block of
    // its own in the .prefab — grep won't find it, GetComponentsInChildren will.
    public const string CommitLeverBoxName = "LeverBox";

    // The lever's cosmetic moving part, animated on hold the same way Deconstruct's
    // Handle is — buried in the FBX hierarchy like LeverBox/Handle.
    public const string CommitLevelName = "Lever";

    // The screen mesh (its own FBX node, using Materials/ModuleScreen.mat) that
    // ForgeScreenDisplay renders the level/alloy readout onto.
    public const string AlloyTerminalScreenName = "AlloyTerminalScreen";

    // Names of the two Unity-authored assets ForgeScreenDisplay needs, captured
    // by AssetLoader.LoadBundle from the exported bundle (a VisualTreeAsset and a
    // PanelSettings ScriptableObject aren't VoidCrewAsset-tagged GameObjects, so
    // they're matched by asset name instead of the usual VCA lookup).
    public const string ForgeScreenLayoutName = "ForgeScreenLayout";
    public const string ForgeScreenPanelSettingsName = "ForgeScreenPanelSettings";

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

    private readonly ForgeGhosts _ghosts = new();
    private float _ghostRefreshCountdown;

    // Vanilla SocketOutlines re-decides on a 0.2s InvokeRepeating; matched so the
    // Forge's previews pop on the same cadence as the ship's own.
    private const float GhostRefreshInterval = 0.2f;

    public bool HasModule => _moduleBox != null;
    public int RelicCount => _relics.Count;

    // Asks the dock rather than HasModule/RelicCount because the question
    // ForgeDeconstructGuardPatch needs answered is physical — "would deconstructing
    // strand an item?" — not semantic.
    internal bool IsLoaded => _dock.Count > 0;
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

    // STRICT: only modules provably at the END of an upgrade chain are forgeable.
    // Anything unresolvable (no identity, table missing, guid in no chain) is
    // refused — the permissive alternative ("unknown = final") let MkI/MkII modules
    // slip through.
    //
    // Composite weapon boxes are GENERIC prefabs: moduleRef is unset, identity
    // arrives as a CompositeWeaponDataRef in instantiation data, and their chains
    // are keyed by that guid. Plain module boxes chain by moduleRef guid.
    private int GetBoxMark(out bool isFinalMark) => GetBoxMark(_moduleBox, out isFinalMark);

    // Guids already reported as unforgeable — see the log line at the end of
    // GetBoxMark for why it must not repeat. Cleared per run (ADR-0001's rule for
    // mod-side static state) so the whitelist hint is available once per run rather
    // than once per process lifetime.
    private static readonly HashSet<GUIDUnion> _unchainedWarned = new();

    internal static void ResetForRun() => _unchainedWarned.Clear();

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
        //
        // Once per guid, and Debug not Info: this is not only reached on a commit
        // attempt — RefreshGhosts calls LevelOfBox at 5 Hz for whatever box the
        // player is carrying, so an unforgeable box in hand (the Forge's own crate,
        // most of all) wrote hundreds of Info lines per playtest.
        if (_unchainedWarned.Add(guid))
            BepinPlugin.Log.LogDebug($"[Forge] Module {guid.AsHex()} not in any upgrade chain — refusing to forge (strict Mark III policy).");
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

    // Consumes only the relics actually spent; leftovers stay in the Forge.
    //
    // Local operator entry (host / solo) — a client operator is routed to
    // RequestCommit by the policy instead. ForgeCommit computes, persists and
    // broadcasts the outcome; consuming OUR relics stays here because we own them,
    // which is what makes the networked destroy propagate.
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

    // Docking is a LOCAL interaction — HandleInteraction runs only for the player
    // who clicked — so the operator announces each dock/undock and everyone else
    // mirrors it. Only the paths that ORIGINATE a dock announce it (the two Apply
    // arms and the Update reconcile); the mirroring paths below say nothing, which
    // is what stops two clients echoing each other forever.
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

        // -1 = the module socket (see AnchorIndexOf/AnchorFromIndex) — align by
        // Center there to match the local LoadModule path below.
        var align = anchorIndex < 0 ? AnchorAlign.Center : AnchorAlign.Base;
        _dock.Dock(go, anchor, align); // no BroadcastDock — mirroring, not originating

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
        var commitLevel = transforms.FirstOrDefault(t => t.name == CommitLevelName);
        var alloyAnchor = transforms.FirstOrDefault(t => t.name == AlloyAnchorName);
        var deconstructHandle = transforms.FirstOrDefault(t => t.name == DeconstructHandleName);
        var deconstructTrigger = transforms.FirstOrDefault(t => t.name == DeconstructTriggerName);
        var alloyScreen = transforms.FirstOrDefault(t => t.name == AlloyTerminalScreenName);

        int layer = LayerMask.NameToLayer("InteractiveObjects");
        if (layer < 0)
        {
            BepinPlugin.Log.LogWarning("[Forge] Layer 'InteractiveObjects' not found — interactables will not be raycast-targetable.");
            layer = gameObject.layer;
        }

        foreach (var tube in _tubeAnchors)
            CreateInteractable(tube, ForgeInteractableKind.RelicTube, new Vector3(0.35f, 0.35f, 0.35f), layer);
        if (_inputAnchor != null)
            // Oversized so loading is forgiving to aim. It stays raycast-targetable
            // while it holds a box — an empty-handed click retrieves the box through
            // the socket rather than needing a ray to reach the box itself, which the
            // hull would block (see ForgeInteractionPolicy.Decide).
            CreateInteractable(_inputAnchor, ForgeInteractableKind.ModuleSocket, new Vector3(1.2f, 1.2f, 1.2f), layer);
        if (commitAnchor != null)
        {
            CreateCommitInteractable(commitAnchor, commitLeverBox, commitLevel, new Vector3(0.3f, 0.3f, 0.3f), layer);
            if (commitLeverBox == null)
                BepinPlugin.Log.LogDebug("[Forge] Prefab has no LeverBox — Commit will outline the whole module instead of just the lever.");
            if (commitLevel == null)
                BepinPlugin.Log.LogDebug("[Forge] Prefab has no Level — Commit works but the lever won't animate.");
        }
        else
            BepinPlugin.Log.LogWarning("[Forge] Prefab has no CommitTarget anchor — in-world commits unavailable (use !forgecommit).");
        if (alloyAnchor != null)
            CreateInteractable(alloyAnchor, ForgeInteractableKind.AlloyTerminal, new Vector3(0.3f, 0.3f, 0.3f), layer);
        else
            BepinPlugin.Log.LogDebug("[Forge] Prefab has no AlloyTarget anchor — alloy feeding unavailable in-world (use !setmeter for testing).");
        if (deconstructTrigger != null)
        {
            CreateDeconstructInteractable(deconstructTrigger, deconstructHandle, layer);
            if (deconstructHandle == null)
                BepinPlugin.Log.LogDebug("[Forge] Prefab has no Handle — deconstruct works but the lever won't animate.");
        }
        else
            BepinPlugin.Log.LogDebug("[Forge] Prefab has no DeconstructTrigger — in-world deconstruct unavailable.");

        if (alloyScreen != null)
        {
            if (alloyScreen.GetComponent<ForgeScreenDisplay>() == null)
                alloyScreen.gameObject.AddComponent<ForgeScreenDisplay>();
        }
        else
            BepinPlugin.Log.LogDebug("[Forge] Prefab has no AlloyTerminalScreen — level/alloy readout unavailable.");

        if (_tubeAnchors.Length == 0 || _inputAnchor == null)
            BepinPlugin.Log.LogWarning(
                $"[Forge] Prefab anchors incomplete (tubes={_tubeAnchors.Length}, input={(_inputAnchor != null ? "ok" : "missing")}) — " +
                "check the metem bundle matches UpgradeForgeModuleCell.prefab.");
        else
            BepinPlugin.Log.LogDebug($"[Forge] Built interactables: {_tubeAnchors.Length} relic tubes, module socket{(commitAnchor != null ? ", commit button" : "")}.");

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

    // Which anchors should be previewing, and of what. Acceptance is asked of
    // ForgeInteractionPolicy — the same call HandleInteraction makes — so the preview
    // appears exactly when the click would be taken, rather than drifting into
    // promising an insert the Forge would then refuse.
    private void RefreshGhosts()
    {
        if (!_interactablesBuilt) return;

        var payload = LocalPlayer.Instance != null ? LocalPlayer.Instance.Payload : null;
        var carried = ClassifyPayload(payload);

        // Also keeps LevelOfBox's upgrade-chain walk off the tick unless a module box
        // is actually in hand.
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
        {
            var align = kind == ForgeInteractableKind.ModuleSocket ? AnchorAlign.Center : AnchorAlign.Base;
            _ghosts.Show(anchor, payload, align);
        }
        else
            _ghosts.Hide(anchor);
    }

    // Read from RaycastHandler.Current the way vanilla's
    // CarryablesSocketActor.IsInteractableHighlighted does, rather than from
    // ForgeInteractable.Highlighted — a highlight callback missed while an
    // interactable was rebuilt would leave the two out of step.
    private Transform AimedAnchor()
    {
        var player = LocalPlayer.Instance;
        if (player == null || player.RaycastHandler == null) return null;
        return player.RaycastHandler.Current is ForgeInteractable fi && fi.Forge == this
            ? fi.Anchor
            : null;
    }

    // Only the first Capacity tubes are active. Deactivating an anchor hides
    // everything under it — click target, Highlight/Filled helpers, tube mesh — so
    // locked tubes are enforced physically, not just by the insertion count check.
    // A tube holding a docked relic never hides (level can drop via dev/reset).
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

    // Held, not clicked — a different vanilla input pathway (EnvironmentInteract's
    // Hold action), so it can't share ForgeInteractable's base. Building the click
    // region is identical though. See ForgeCommitInteractable.
    private void CreateCommitInteractable(Transform anchor, Transform leverBox, Transform level, Vector3 size, int layer)
    {
        var go = BuildAnchorClickRegion(anchor, "ForgeInteractable_CommitButton", size, layer);

        var hc = go.GetComponent<ForgeCommitInteractable>();
        if (hc == null) hc = go.AddComponent<ForgeCommitInteractable>();
        hc.Forge = this;
        hc.Anchor = anchor;
        hc.OutlineTarget = leverBox;
        hc.VisualLevel = level;
        hc.ShowContextInfo = false;
        // Must be set before Start(): ClickerInteractable.SetClickable would
        // otherwise stomp the assignment below back to the null it captured in Awake.
        hc.DontSelfSetInteractionInfo = true;
        hc.InteractionInfo = ForgeInteractable.InfoFor(ForgeInteractableKind.CommitButton);
    }

    // Same hold-to-confirm mechanism as Commit. `trigger` carries its own authored
    // Collider so BuildAnchorClickRegion never takes the generated-box fallback —
    // that fallback is what let this region balloon and steal clicks meant for a
    // neighboring module. `handle` is cosmetic and optional (the pull animation).
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

    // Entry point for all Forge interactions; runs on the interacting player's
    // client. Tubes/socket/alloy arrive via ForgeInteractionPatch's CarryableInteract
    // prefix, CommitButton via ForgeCommitInteractable's hold-completion.
    //
    // The rules live in ForgeInteractionPolicy, which is Unity-free and therefore
    // testable. This reads the scene into facts, hands them over, and carries out the
    // answer — everything decidable is decided before the world is touched.
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
            // Strictly the dock's answer — see ForgeClick.TargetOccupied for why a
            // null anchor must NOT report as occupied here.
            targetOccupied: IsAnchorOccupied(anchor));
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
                // Center-pivot align: the module socket's generated trigger volume
                // is centered on the anchor, so the box's own center — not its
                // BasePivot — is what should land there (see AnchorAlign).
                _dock.Dock(payload.gameObject, socket, AnchorAlign.Center);
                BroadcastDock(payload.gameObject, socket, docked: true);
                break;

            case ForgeAction.InsertRelic:
                // The anchor is guarded here rather than folded into
                // ForgeClick.TargetOccupied: without it TryInsertRelic would claim the
                // relic and ReleaseCarryable would take it out of the player's hands,
                // then AnchorDock.Dock would no-op on the null anchor and leave the
                // relic listed but unpinned.
                if (anchor == null)
                {
                    BepinPlugin.Log.LogWarning("[Forge] Insert on a missing anchor — ignored.");
                    break;
                }

                // The policy already cleared capacity and the tube, so a refusal here
                // means the two disagree. Drop it rather than reprint a message the
                // policy owns.
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

            case ForgeAction.RetrieveItem:
                RetrieveFrom(anchor, player);
                break;

            case ForgeAction.Commit:
                // Levels and counts are read back AFTER the attempt — on success the
                // box reports its new level and the consumed relics are gone.
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

    // Hand the item docked on `anchor` back to the player.
    //
    // Deliberately does NOT undock here. Vanilla's own pickup is invoked and the
    // dock is left to notice on the next Update: CarryableInteract.StartInteraction
    // sets the item's Carrier, _dock.Reconcile() sees that and runs the single
    // grab-back-out path that already existed — one undock, one broadcast, one log
    // line — instead of a second, parallel release that would have to keep itself
    // in step with it.
    //
    // Routing through StartInteraction rather than Carrier.TryInsertCarryable
    // directly is what buys the rest of the vanilla grab: the interaction lock, the
    // fetch lerp that flies the item to the hand, hand IK, and the grab SFX all live
    // in that method's private half. Our CarryableInteract prefix re-entry is not a
    // concern — it only claims ForgeInteractable targets, and this passes a Grabbable.
    private void RetrieveFrom(Transform anchor, LocalPlayer player)
    {
        if (!_dock.TryGetDockedAt(anchor, out var item))
        {
            // Policy said occupied, the dock disagrees — the two are read one after
            // the other from the same object, so this is a state bug, not a race.
            BepinPlugin.Log.LogWarning(
                "[Forge] Retrieve approved by policy but the anchor holds nothing — state disagreement, ignored.");
            return;
        }

        var grabbable = item.GetComponent<Grabbable>();
        if (grabbable == null)
        {
            BepinPlugin.Log.LogWarning($"[Forge] {item.name} has no Grabbable — cannot hand it back.");
            return;
        }

        var interact = player.Locomotion != null
            ? player.Locomotion.GetAbility<CarryableInteract>()
            : null;
        if (interact == null)
        {
            BepinPlugin.Log.LogWarning("[Forge] CarryableInteract ability not found on the local player — cannot hand the item back.");
            return;
        }

        // ignorePlacingObjects has no effect on this path and no default to omit —
        // vanilla reads it only inside its IsHoldingCarryable branch, and the policy
        // only reaches RetrieveItem for ForgePayload.None. Passed true because that is
        // what the branch would want if our hands ever turned out not to be empty: a
        // swap to the docked item, rather than an interact with the held one.
        interact.StartInteraction(grabbable, ignorePlacingObjects: true);
    }

    // Hot-reload teardown (ScriptEngine): a reloaded assembly brings its OWN
    // UpgradeForgeBehavior type, so this instance must leave cleanly — restoring
    // held items' physics so nothing is left frozen mid-air. The reloaded assembly
    // re-attaches on its own patch pass.
    public void TeardownForReload()
    {
        _ghosts.Clear();
        _dock.ReleaseAll();
        _relics.Clear();
        _moduleBox = null;
        Destroy(this);
    }

    // Drives the insert-vs-retrieve arm of the policy, and the matching HUD prompt
    // on ForgeInteractable.
    public bool IsAnchorOccupied(Transform anchor) => _dock.IsOccupied(anchor);

    // Reconcile with the world: players grab docked items back out via the vanilla
    // Grabbable flow, and commits destroy consumed relics. The dock reports the ones
    // a player is now carrying; destroyed ones it reaps itself.
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
        // is applied every frame, so hover feedback isn't 200ms behind the crosshair.
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

    // Primary check is the game's canonical relic CsTag — the same one the vanilla
    // relic shrine filter resolves to, and what RuntimeCarryable stamps on modded
    // relics. Name matching is the fallback for objects that aren't tagged carryables.
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
