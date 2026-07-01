using System.Collections.Generic;
using CG.Ship.Object;
using UnityEngine;

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
// Physical socket wiring (colliders / trigger volumes on the prefab that route
// relic drops and BuildBox loads into TryInsertRelic / TryTakeModule) lives in
// ForgeInteractionPatch. That side depends on live-decompile signatures and is
// scaffolded rather than implemented — dev commands drive the state machine
// end-to-end until then.
public class UpgradeForgeBehavior : MonoBehaviour
{
    // Hardcoded per the Phase 3 plan. Phase 5 replaces this with a value driven by
    // ForgeMeterController (capacity == current Forge level).
    public const int Capacity = 4;

    // Name of the shipped prefab inside voidcrewterminus.metem — used by
    // ForgeInteractionPatch to identify Forge modules as they build.
    public const string PrefabName = "UpgradeForgeModuleCell";

    private BuildBox _moduleBox;
    private readonly List<GameObject> _relics = new();

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
            if (_moduleBox == null || _moduleBox.photonView == null) return ForgeCostCurve.MinLevel;
            return ForgeOverlayTable.TryPeekPendingLevel(_moduleBox.photonView.ViewID, out int level)
                ? level
                : ForgeCostCurve.MinLevel;
        }
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
        InsufficientRelics,
        MissingViewId,
    }

    // Attempts to upgrade the socketed box using as many inserted relics as the cost
    // curve permits. Consumes only the relics actually spent; leftovers stay in the
    // Forge. On success the new pending level is written to ForgeOverlayTable so
    // reconstruction picks it up automatically.
    public CommitResult TryCommit(out int newLevel, out int relicsConsumed)
    {
        newLevel = 0;
        relicsConsumed = 0;

        if (_moduleBox == null) return CommitResult.NoModule;
        if (_moduleBox.photonView == null) return CommitResult.MissingViewId;
        if (_relics.Count == 0) return CommitResult.NoRelics;

        int currentLevel = CurrentBoxLevel;
        if (currentLevel >= ForgeCostCurve.MaxLevel) return CommitResult.AlreadyAtMax;

        int nextStepCost = ForgeCostCurve.CostForNextLevel(currentLevel);
        if (_relics.Count < nextStepCost) return CommitResult.InsufficientRelics;

        int achievedLevel = ForgeCostCurve.MaxReachable(currentLevel, _relics.Count);
        relicsConsumed = ForgeCostCurve.RelicsRequired(currentLevel, achievedLevel);
        newLevel = achievedLevel;

        ForgeOverlayTable.SavePendingLevel(_moduleBox.photonView.ViewID, newLevel);

        for (int i = 0; i < relicsConsumed && _relics.Count > 0; i++)
        {
            var relic = _relics[0];
            _relics.RemoveAt(0);
            if (relic != null) Destroy(relic);
        }

        BepinPlugin.Log.LogInfo(
            $"[Forge] Committed L{currentLevel}→L{newLevel} on ViewID={_moduleBox.photonView.ViewID} " +
            $"(consumed {relicsConsumed} relic{(relicsConsumed == 1 ? "" : "s")}, {_relics.Count} remain)");

        return CommitResult.Ok;
    }

    // ---- Helpers ------------------------------------------------------

    // A GameObject is considered a relic if RelicTierData recognises its normalized
    // prefab name, OR its name starts with "Relic_" (so mod-added or unmapped relics
    // still work — they fall through to RelicTierData's default tier).
    public static bool IsRelic(GameObject go)
    {
        if (go == null) return false;
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
