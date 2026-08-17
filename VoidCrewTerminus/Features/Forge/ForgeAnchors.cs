using UnityEngine;

namespace VoidCrewTerminus.Forge;

// The prefab authoring contract, and the only place its names are spelled.
//
// These anchors are deliberately NOT vanilla CarryablesSockets — see ADR-0002 for
// why and for what that costs. (Bundle serialization is not the reason.)
//
// Game components cannot be serialized into a metem bundle, so the shipped
// prefab carries only named anchor transforms — every optional embellishment
// an anchor can carry is a plain Unity object found by name at runtime:
//
//   ClickTarget — a Collider anywhere below the anchor becomes the click region,
//                 in place of the box UpgradeForgeBehavior would otherwise generate.
//   Highlight   — a disabled child shown while the player's raycast targets the
//                 anchor (ForgeInteractable.Highlighted).
//   Filled      — a disabled child shown while an item is docked there (AnchorDock).
//
// All three are optional — nothing here warns when a name goes unfound.
internal static class ForgeAnchors
{
    internal const string ClickTargetName = "ClickTarget";
    internal const string HighlightName = "Highlight";
    internal const string FilledName = "Filled";

    // Depth-first so authored helpers may sit anywhere below the anchor — e.g. a
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

    // Where an item's root ends up once docked: its BasePivot lands on the anchor's
    // origin, axes aligned. Same intent as CarryablesSocket.PlaceCarryableOnSocket,
    // but quaternion-based — our anchors inherit rotated, non-uniformly scaled FBX
    // nodes whose matrices would skew an extracted rotation.
    //
    // Shared by AnchorDock (the real item) and ForgeGhosts (the preview), so the
    // preview can't land somewhere the item won't.
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

    // Highlight / Filled helpers are visual-only; primitives authored in the editor
    // often keep their default colliders, which would collide with docked items and
    // block the interact ray. Strip them.
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
