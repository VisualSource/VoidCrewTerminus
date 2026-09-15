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

// Attached at runtime to the UpgradeForgeModuleCell prefab from voidcrewterminus.metem.
// Commits persist through ForgeStateStore.SaveSnapshot so the level rides the BuildBox
// through reconstruction; ForgePersistPatch restores it. See docs/upgrade-forge-design.html.
public class UpgradeForgeBehavior : MonoBehaviour
{
    // Relic capacity is the Forge's progression level, raised by filling the meter.
    public static int Capacity => ForgeMeterController.Capacity;

    public const string PrefabName = "UpgradeForgeModuleCell";

    // AssetLoader tells the two prefabs apart by name: both are bare VoidCrewAsset GameObjects
    // with no surviving components, so the name is the only signal at load time.
    public const string BuildBoxPrefabName = "UpgradeForgeBuildBox";

    // Baked into the shipped prefab; renaming one silently unhooks it. Handle and
    // DeconstructTrigger must stay separate: Handle is cosmetic mesh, DeconstructTrigger an
    // authored Collider, and conflating them falls back to a generated click box that on odd
    // FBX import scales steals raycasts from neighboring modules.
    public const string RelicTubeAnchorName = "RelicTubeTarget";
    public const string InputAnchorName = "InputTarget";
    public const string CommitAnchorName = "CommitTarget";
    public const string AlloyAnchorName = "AlloyTarget";
    public const string DeconstructHandleName = "Handle";
    public const string DeconstructTriggerName = "DeconstructTrigger";

    // Scopes ForgeCommitInteractable's outline to the lever. Buried in the FBX hierarchy, so it
    // has no YAML block in the .prefab: grep won't find it, GetComponentsInChildren will.
    public const string CommitLeverBoxName = "LeverBox";

    // The lever's cosmetic moving part, animated on hold. Also buried in the FBX hierarchy.
    public const string CommitLevelName = "Lever";

    public const string AlloyTerminalScreenName = "AlloyTerminalScreen";

    // A VisualTreeAsset and a PanelSettings aren't VoidCrewAsset-tagged GameObjects, so
    // AssetLoader matches these by asset name instead of the usual VCA lookup.
    public const string ForgeScreenLayoutName = "ForgeScreenLayout";
    public const string ForgeScreenPanelSettingsName = "ForgeScreenPanelSettings";

    private BuildBox _moduleBox;
    private readonly List<GameObject> _relics = new();

    private readonly AnchorDock _dock = new();
    private readonly List<KeyValuePair<GameObject, Transform>> _grabbedScratch = new();
    private Transform[] _tubeAnchors = System.Array.Empty<Transform>();
    private Transform _inputAnchor;
    private bool _interactablesBuilt;

    private readonly ForgeGhosts _ghosts = new();
    private float _ghostRefreshCountdown;

    // Matches vanilla SocketOutlines' 0.2s cadence so previews pop with the ship's own.
    private const float GhostRefreshInterval = 0.2f;

    public bool HasModule => _moduleBox != null;
    public int RelicCount => _relics.Count;

    // Physical, not semantic: would deconstructing strand an item? Not HasModule/RelicCount.
    internal bool IsLoaded => _dock.Count > 0;
    public BuildBox ModuleBox => _moduleBox;
    public IReadOnlyList<GameObject> Relics => _relics;

    public int CurrentBoxLevel => LevelOfBox(_moduleBox);

    // Static so the host can compute a client-operated box's level by ViewID: docking is a
    // local interaction, so the host's own forge instance has no _moduleBox.
    internal static int LevelOfBox(BuildBox box)
    {
        if (box == null || box.photonView == null) return 0;

        // Only a module at its final vanilla mark may be forged; below that the chip path applies.
        int mark = GetBoxMark(box, out bool isFinalMark);
        if (!isFinalMark) return mark; // 1 or 2 → below MinLevel → InvalidModuleLevel on commit

        return ForgeStateStore.TryPeekSnapshot(box.photonView.ViewID, out var snap)
            ? snap.Level
            : ForgeCostCurve.MinLevel;
    }

    // Fail closed: anything unresolvable (no identity, table missing, guid in no chain) is
    // refused, so only modules provably at the end of an upgrade chain are forgeable.
    //
    // Composite weapon boxes are generic prefabs: moduleRef is unset and identity arrives as a
    // CompositeWeaponDataRef in instantiation data, so their chains key by that guid instead.
    private int GetBoxMark(out bool isFinalMark) => GetBoxMark(_moduleBox, out isFinalMark);

    // Cleared per run (ADR-0001's rule for mod-side static state) so the hint below appears
    // once per run rather than once per process.
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

        // Reached at 5 Hz from RefreshGhosts, not only on commit, so this stays once-per-guid
        // and Debug. The guid named here is what a legitimately single-form module whitelists.
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

    // Host/solo entry; a client operator is routed to RequestCommit instead. Consuming our own
    // relics stays local because ownership is what makes the networked destroy propagate.
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

    // Only the operator (the client holding the relics) consumes; ForgeNetSync applies the
    // snapshot itself.
    internal void OnNetworkCommitResult(int relicsConsumed)
    {
        if (_relics.Count == 0) return; // not the operator
        int before = _relics.Count;
        ConsumeOwnedRelics(relicsConsumed);
        Messaging.Notification(
            $"Upgrade committed by the host (consumed {ForgeLabels.Plural(before - _relics.Count, "relic")}). " +
            "Rebuild the module to apply.");
    }

    // Docking is a local interaction, so the operator announces it and everyone else mirrors.
    // Only originating paths broadcast; were the mirroring paths to, clients would echo forever.
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

    // -1 is the module socket; >= 0 indexes _tubeAnchors. Ordered by name so the index means
    // the same thing on every client running the same prefab.
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

        // Mirrored so RelicCount/HasModule read correctly for observers. Commits re-resolve
        // from ViewIDs on the host, so a mirrored list can't affect an outcome.
        var box = go.GetComponent<BuildBox>();
        if (box != null) _moduleBox ??= box;
        else if (!_relics.Contains(go)) _relics.Add(go);

        // -1 is the module socket; align by Center there to match the local LoadModule path.
        var align = anchorIndex < 0 ? AnchorAlign.Center : AnchorAlign.Base;
        _dock.Dock(go, anchor, align); // no BroadcastDock: mirroring, not originating

        BepinPlugin.Log.LogDebug($"[Net] ← applied dock item={itemViewId} anchor={anchorIndex} on forge={ForgeViewId}.");
    }

    internal void ApplyRemoteUndock(int itemViewId)
    {
        var pv = Photon.Pun.PhotonView.Find(itemViewId);
        if (pv == null) return;
        var go = pv.gameObject;
        if (go == null || !_dock.Undock(go)) return; // not docked here, nothing to mirror

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

    // Idempotent: ForgeInteractionPatch re-attaches after every module rebuild.
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
            // Oversized so loading is forgiving to aim, and still targetable while it holds a
            // box: an empty-handed click retrieves through the socket, which the hull would
            // block if the ray had to reach the box itself.
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
        // Nothing ticks these while disabled; don't leave a hologram floating.
        _ghosts.Clear();
    }

    private void OnForgeLevelChanged(int _) => RefreshTubeVisibility();

    // Previews ask ForgeInteractionPolicy the same question HandleInteraction does, so a
    // preview can never promise an insert the click would refuse.
    private void RefreshGhosts()
    {
        if (!_interactablesBuilt) return;

        var payload = LocalPlayer.Instance != null ? LocalPlayer.Instance.Payload : null;
        var carried = ClassifyPayload(payload);

        // Also keeps LevelOfBox's chain walk off the tick unless a module box is in hand.
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

        // Tubes above Capacity are deactivated by RefreshTubeVisibility; a locked tube
        // takes nothing, so it previews nothing.
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

    // Read from RaycastHandler.Current, as vanilla's CarryablesSocketActor does: a highlight
    // callback missed while an interactable was rebuilt would leave ForgeInteractable stale.
    private Transform AimedAnchor()
    {
        var player = LocalPlayer.Instance;
        if (player == null || player.RaycastHandler == null) return null;
        return player.RaycastHandler.Current is ForgeInteractable fi && fi.Forge == this
            ? fi.Anchor
            : null;
    }

    // Deactivating an anchor hides everything under it, so locked tubes are enforced
    // physically, not just by the count check. A tube holding a relic never hides,
    // because the level can drop via dev commands or a reset.
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

    // Held, not clicked: a different vanilla input pathway (EnvironmentInteract's Hold action),
    // so it can't share ForgeInteractable's base even though the click region is built the same.
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

    // `trigger` carries its own authored Collider so BuildAnchorClickRegion never takes the
    // generated-box fallback that steals clicks from neighbors. `handle` is cosmetic.
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

    // Prefab authoring contract: an authored Collider on the anchor or a "ClickTarget" child
    // replaces the generated box; disabled "Highlight"/"Filled" children are shown on hover
    // and while docked. See docs/upgrade-forge-prefab-authoring.html.
    private static GameObject BuildAnchorClickRegion(Transform anchor, string generatedName, Vector3 size, int layer)
    {
        GameObject go;
        var authored = anchor.GetComponent<Collider>();
        if (authored == null)
            authored = ForgeAnchors.FindDeep(anchor, ForgeAnchors.ClickTargetName)?.GetComponent<Collider>();

        if (authored != null)
        {
            // Click regions must not collide, however the collider was authored.
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

        // Forced at runtime: the editor project's layer table doesn't match the game's, so
        // authored layer indices can't be trusted.
        go.layer = layer;

        ForgeAnchors.StripHelperColliders(anchor, ForgeAnchors.HighlightName);
        ForgeAnchors.StripHelperColliders(anchor, ForgeAnchors.FilledName);

        var builtCollider = go.GetComponent<Collider>();
        BepinPlugin.Log.LogDebug(
            $"[Forge] Click region '{generatedName}' on anchor '{anchor.name}': " +
            $"{(authored != null ? "authored" : "generated")} collider, " +
            $"world bounds center={builtCollider.bounds.center} size={builtCollider.bounds.size}");

        return go;
    }

    // Runs only on the interacting player's client. The rules live in ForgeInteractionPolicy,
    // which stays Unity-free so it can be tested; this only reads facts and applies the answer.
    public void HandleInteraction(ForgeInteractableKind kind, Transform anchor, LocalPlayer player)
    {
        // Captured before any mutation: ReleaseCarryable clears player.Payload.
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
            // Strictly the dock's answer; see ForgeClick.TargetOccupied for why a null anchor
            // must not report as occupied.
            targetOccupied: IsAnchorOccupied(anchor));
    }

    private static ForgePayload ClassifyPayload(CarryableObject payload) =>
        payload == null ? ForgePayload.None
        : payload is BuildBox ? ForgePayload.ModuleBox
        : IsRelic(payload.gameObject) ? ForgePayload.Relic
        : ForgePayload.Other;

    // Nothing here re-checks a rule the policy applied, or decides what to say about a refusal.
    private void Apply(ForgeAction action, Transform anchor, LocalPlayer player, CarryableObject payload)
    {
        switch (action)
        {
            case ForgeAction.LoadModule:
                player.Carrier.ReleaseCarryable();
                TryTakeModule((BuildBox)payload);
                var socket = _inputAnchor != null ? _inputAnchor : transform;
                _dock.Dock(payload.gameObject, socket, AnchorAlign.Center);
                BroadcastDock(payload.gameObject, socket, docked: true);
                break;

            case ForgeAction.InsertRelic:
                // Without this guard TryInsertRelic claims the relic and ReleaseCarryable takes
                // it from the player, then Dock no-ops on the null anchor and leaves the relic
                // listed but unpinned.
                if (anchor == null)
                {
                    BepinPlugin.Log.LogWarning("[Forge] Insert on a missing anchor — ignored.");
                    break;
                }

                // The policy already cleared capacity and the tube, so a refusal here means the
                // two disagree. Drop it rather than reprint a message the policy owns.
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
                // Read back after the attempt: on success the box reports its new level.
                var outcome = TryCommit();
                foreach (var line in ForgeLabels.DescribeCommit(outcome, CurrentBoxLevel, RelicCount))
                    Messaging.Notification(line);
                break;

            case ForgeAction.RequestCommit:
                Net.ForgeNetSync.RequestCommit(_moduleBox.photonView.ViewID, RelicViewIds());
                break;

            case ForgeAction.FeedAlloy:
                if (ForgeMeterController.TrySpendAlloys(out var alloyError))
                {
                    Messaging.Notification(ForgeMeterController.Describe());
                    Net.ForgeNetSync.BroadcastState(); // host spent: propagate new meter/level
                }
                else
                    Messaging.Notification(alloyError);
                break;
        }
    }

    // Deliberately does NOT undock: Reconcile notices the Carrier next Update and runs the one
    // existing grab-back-out path. Routed through StartInteraction rather than
    // Carrier.TryInsertCarryable because the interaction lock, fetch lerp, hand IK and grab SFX
    // live in its private half; our CarryableInteract prefix only claims ForgeInteractable.
    private void RetrieveFrom(Transform anchor, LocalPlayer player)
    {
        if (!_dock.TryGetDockedAt(anchor, out var item))
        {
            // Policy and dock are read from the same object in sequence, so a disagreement
            // here is a state bug, not a race.
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

        // No effect on this path (the policy only reaches RetrieveItem with empty hands) and
        // no default to omit. True is what the branch would want: swap to the docked item.
        interact.StartInteraction(grabbable, ignorePlacingObjects: true);
    }

    // Hot-reload teardown: a reloaded assembly brings its own type, so this instance must leave
    // cleanly and restore held items' physics. The new assembly re-attaches on its own pass.
    public void TeardownForReload()
    {
        _ghosts.Clear();
        _dock.ReleaseAll();
        _relics.Clear();
        _moduleBox = null;
        Destroy(this);
    }

    public bool IsAnchorOccupied(Transform anchor) => _dock.IsOccupied(anchor);

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

    private void LateUpdate() => _dock.Pin();

    // Networked objects: destroy through the game's factory when we own them so the removal
    // replicates; plain Destroy otherwise.
    private static void DestroyRelic(GameObject relic)
    {
        if (relic == null) return;
        var co = relic.GetComponent<CarryableObject>();
        if (co != null && co.photonView != null && co.photonView.AmOwner)
            ObjectFactory.DestroyCloneStarObject(co);
        else
            Destroy(relic);
    }

    // The canonical relic CsTag is what the vanilla shrine filter resolves to and what
    // RuntimeCarryable stamps on modded relics; name matching covers untagged objects.
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
