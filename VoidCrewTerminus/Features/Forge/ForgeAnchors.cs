using UnityEngine;

namespace VoidCrewTerminus.Forge;

// The prefab authoring contract, and the only place its names are spelled.
//
// These anchors are deliberately NOT vanilla CarryablesSockets — see
// docs/adr/0002-anchor-dock-not-carryables-socket.md for why, and for what it
// costs. Bundle serialization is NOT the reason (AssetLoader.GraftModuleComponents
// already grafts game components at runtime); the reason is that CarryablesSocket
// is a networked CloneStar entity needing its own PhotonView per anchor.
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

    // Where an item's root transform ends up once docked on an anchor: the item's
    // BasePivot lands on the anchor's origin with its axes matching the anchor's.
    // Same intent as CarryablesSocket.PlaceCarryableOnSocket, but computed with
    // quaternions instead of the anchor's matrices — our anchors inherit rotated,
    // non-uniformly scaled FBX nodes whose matrices would skew an extracted
    // rotation, unlike vanilla's unit-scale store transforms.
    //
    // Shared by AnchorDock (which moves the real item) and ForgeGhosts (which poses
    // the translucent preview): a preview that landed anywhere else than the item
    // would be lying about the result.
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
