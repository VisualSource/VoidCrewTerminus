using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Knife.HDRPOutline.Core;
using UnityEngine;

namespace VoidCrewTerminus.Forge;

// ClickerInteractable's own highlight walks an outlineObjects[] that only the Unity
// Inspector populates, so it NREs on every Forge interactable (all built at runtime
// via AddComponent). This walks the module's renderers instead, adding an
// OutlineObject to each on first use — the same outline-shader mechanism vanilla
// modules use, with no Unity-side authoring required.
internal static class ForgeOutline
{
    private static readonly ConditionalWeakTable<Transform, OutlineObject[]> _cache = new();

    internal static void SetHighlighted(Transform moduleRoot, bool isHighlighted)
    {
        if (moduleRoot == null) return;
        var outlines = _cache.GetValue(moduleRoot, BuildOutlines);
        foreach (var outline in outlines)
            if (outline != null) outline.enabled = isHighlighted;
    }

    // ForgeGhosts parks render-only clones under the module's anchors, so they turn
    // up in this sweep — which runs once per module and is cached forever, capturing
    // a transient ghost permanently. They own their own outlines; skip them.
    private static OutlineObject[] BuildOutlines(Transform moduleRoot)
    {
        var renderers = moduleRoot.GetComponentsInChildren<Renderer>(true);
        var outlines = new List<OutlineObject>(renderers.Length);
        foreach (var renderer in renderers)
        {
            if (renderer.GetComponentInParent<ForgeGhostMarker>() != null) continue;
            var outline = renderer.GetComponent<OutlineObject>();
            if (outline == null) outline = renderer.gameObject.AddComponent<OutlineObject>();
            outline.enabled = false;
            outlines.Add(outline);
        }
        BepinPlugin.Log.LogDebug($"[Forge] Built {outlines.Count} outline object(s) for {moduleRoot.name}.");
        return outlines.ToArray();
    }
}
