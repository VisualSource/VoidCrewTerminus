using System.Runtime.CompilerServices;
using Knife.HDRPOutline.Core;
using UnityEngine;

namespace VoidCrewTerminus.Forge;

// ClickerInteractable's own highlight mechanism (an outlineObjects[] array toggling
// Knife.HDRPOutline.Core.OutlineObject components) is [SerializeField], populated
// only by the Unity Inspector — and every Forge interactable is built at runtime via
// AddComponent with no Inspector data behind it, so calling the base implementation
// NREs on the null array. This walks the module's own renderers instead, adding an
// OutlineObject on each on first use and toggling them together — same outline-shader
// mechanism vanilla modules/BuildBoxes use, with no Unity-side authoring required.
// Whichever Forge interactable is hovered outlines the whole module, matching how
// vanilla hover highlights as a single unit rather than a specific sub-mesh.
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

    private static OutlineObject[] BuildOutlines(Transform moduleRoot)
    {
        var renderers = moduleRoot.GetComponentsInChildren<Renderer>(true);
        var outlines = new OutlineObject[renderers.Length];
        for (int i = 0; i < renderers.Length; i++)
        {
            var outline = renderers[i].GetComponent<OutlineObject>();
            if (outline == null) outline = renderers[i].gameObject.AddComponent<OutlineObject>();
            outline.enabled = false;
            outlines[i] = outline;
        }
        BepinPlugin.Log.LogDebug($"[Forge] Built {outlines.Length} outline object(s) for {moduleRoot.name}.");
        return outlines;
    }
}
