using UnityEngine;

namespace VoidCrewTerminus.Forge;

// The prefab authoring contract, and the only place its names are spelled. These anchors are
// deliberately not vanilla CarryablesSockets; see ADR-0002.
//
// Every embellishment is optional and found by name at runtime: a ClickTarget Collider
// replaces the generated click region, Highlight shows while the raycast targets the anchor,
// and Filled shows while an item is docked. Nothing warns when a name goes unfound.
internal static class ForgeAnchors
{
    internal const string ClickTargetName = "ClickTarget";
    internal const string HighlightName = "Highlight";
    internal const string FilledName = "Filled";

    // Depth-first so authored helpers may sit anywhere below the anchor, including inside a
    // wrapper node kept to preserve an FBX transform chain.
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

    // The chosen pivot lands on the anchor's origin, axes aligned. Quaternion-based rather
    // than matrix-based because the anchors inherit rotated, non-uniformly scaled FBX nodes
    // whose matrices would skew an extracted rotation. Shared by AnchorDock and ForgeGhosts,
    // so the preview can't land somewhere the item won't.
    internal static void ComputeDockedPose(Transform item, Transform pivot, Transform anchor,
        out Vector3 position, out Quaternion rotation)
    {
        rotation = anchor.rotation * Quaternion.Inverse(pivot.rotation) * item.rotation;
        var delta = rotation * Quaternion.Inverse(item.rotation);
        position = anchor.position - delta * (pivot.position - item.position);
    }

    internal static void SetFilled(Transform anchor, bool filled)
    {
        var indicator = FindDeep(anchor, FilledName);
        if (indicator != null) indicator.gameObject.SetActive(filled);
    }

    // Highlight/Filled helpers are visual-only, but editor primitives keep their default
    // colliders, which would collide with docked items and block the interact ray.
    internal static void StripHelperColliders(Transform anchor, string helperName)
    {
        var helper = FindDeep(anchor, helperName);
        if (helper == null) return;
        foreach (var col in helper.GetComponentsInChildren<Collider>(true))
        {
            BepinPlugin.Log.LogDebug($"[Forge] Removing stray collider from {helperName} helper under {anchor.name}.");
            Object.Destroy(col);
        }
    }
}
