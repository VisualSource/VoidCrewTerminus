using System.Collections.Generic;
using CG.Objects;
using CG.Rendering;
using HarmonyLib;
using Knife.HDRPOutline.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace VoidCrewTerminus.Forge;

// Marks a rendering-only preview clone. Its only job is to be findable from a
// renderer via GetComponentInParent — ForgeOutline sweeps every Renderer under the
// module to build its hover outline set, and a ghost caught in that sweep would be
// cached there permanently (the sweep runs once per module) and then flicker along
// with the module's own hover highlight.
internal sealed class ForgeGhostMarker : MonoBehaviour { }

// The translucent blue placement preview, on the Forge's anchors.
//
// Vanilla shows one on every ship socket that would accept what you're carrying —
// CG.Rendering.SocketOutlines: it clones the carried object's mesh hierarchy into a
// render-only copy, swaps every material for a single shared HologramMaterial, and
// parks it on the socket. That system is driven off CarryablesSocket registrations
// (CarryablesSocket.OnSocketAdded), and the Forge's anchors are plain named
// transforms with our own ForgeInteractable on them, not CarryablesSockets — so it
// never sees them and the Forge's only "you can insert this" signal was the HUD
// prompt. This reproduces the same effect on our anchors, borrowing vanilla's own
// material so the preview is literally the same blue rather than a lookalike.
//
// Two visual states, both taken from SocketOutlines.HighlightObjects.ShowHighlight:
//   outline only  — carrying something this anchor accepts, but aimed elsewhere.
//                   Renderers are moved to the outline layer (3), so the outline
//                   shader draws a silhouette and nothing else does.
//   hologram      — aimed straight at this anchor. Renderers return to their own
//                   layer and the full translucent solid draws.
//
// Purely local and purely cosmetic: it reads LocalPlayer only and touches no
// physics, no ownership and no network state, so it needs no sync — every client
// draws its own preview for whatever its own player is holding.
internal sealed class ForgeGhosts
{
    // SocketOutlines.OUTLINE_LAYER. Renderers parked here are drawn by the outline
    // pass alone.
    private const int OutlineLayer = 3;

    // Layers SocketOutlines refuses to clone from: 15 (never rendered in a preview)
    // and the outline layer itself (already outline-only geometry).
    private const int SkipLayer = 15;

    private readonly Dictionary<Transform, Ghost> _ghosts = new();

    internal int Count => _ghosts.Count;

    // Show a preview of `source` on `anchor`, building it if the anchor is empty or
    // is currently previewing something else.
    internal void Show(Transform anchor, CarryableObject source)
    {
        if (anchor == null || source == null) return;

        var sourceGo = source.gameObject;
        if (_ghosts.TryGetValue(anchor, out var existing))
        {
            // Same object, still alive — leave it be. This is the common case: the
            // refresh tick re-asserts every accepted anchor several times a second.
            if (existing.Root != null && existing.Source == sourceGo) return;
            existing.Destroy();
            _ghosts.Remove(anchor);
        }

        var hologram = HologramMaterial;
        if (hologram == null) return;

        var ghost = Build(anchor, source, hologram);
        if (ghost != null) _ghosts[anchor] = ghost;
    }

    internal void Hide(Transform anchor)
    {
        if (anchor == null || !_ghosts.TryGetValue(anchor, out var ghost)) return;
        ghost.Destroy();
        _ghosts.Remove(anchor);
    }

    // Which anchor the player is actually looking at, or null. Cheap enough to run
    // every frame — the previews themselves are only rebuilt on the slower refresh
    // tick, but hover feedback that lagged by a refresh interval would feel broken.
    internal void SetAimed(Transform aimed)
    {
        if (_ghosts.Count == 0) return;
        foreach (var kv in _ghosts)
            kv.Value.Apply(showOutline: true, showHologram: kv.Key == aimed);
    }

    internal void Clear()
    {
        if (_ghosts.Count == 0) return;
        foreach (var kv in _ghosts) kv.Value.Destroy();
        _ghosts.Clear();
    }

    private static Ghost Build(Transform anchor, CarryableObject source, Material hologram)
    {
        var sourceTr = source.transform;
        var root = CloneGraphics(sourceTr.gameObject, hologram);
        if (root == null) return null;

        root.AddComponent<ForgeGhostMarker>();

        // Parented to the anchor so it rides the ship for free — the real docked
        // items need AnchorDock's per-frame pin only because they keep their own
        // rigidbodies, which a render-only clone does not have.
        root.transform.SetParent(anchor, worldPositionStays: false);

        var pivot = source.BasePivot != null ? source.BasePivot : sourceTr;
        ForgeAnchors.ComputeDockedPose(sourceTr, pivot, anchor, out var pos, out var rot);
        root.transform.SetPositionAndRotation(pos, rot);

        // CloneGraphics copies child local scales but gives the root identity scale,
        // so the ghost would otherwise inherit the anchor's scale outright — and the
        // Forge's anchors ride FBX nodes with tiny non-uniform scales (the same
        // hazard BuildAnchorClickRegion divides out for its generated colliders).
        SetWorldScale(root.transform, sourceTr.lossyScale);

        var ghost = new Ghost(root, sourceTr.gameObject);
        BepinPlugin.Log.LogDebug(
            $"[Forge] Ghost of {sourceTr.gameObject.name} on anchor '{anchor.name}': " +
            $"{ghost.RendererCount} renderer(s).");
        return ghost;
    }

    // SocketOutlines.RecursiveInstantiateGraphics, reimplemented: a hierarchy of
    // bare GameObjects carrying only the source's meshes, every submesh slot bound
    // to the one shared hologram material, no shadows, no probes, no colliders —
    // nothing that could interact with the world.
    private static GameObject CloneGraphics(GameObject reference, Material hologram)
    {
        if (!reference.activeSelf) return null;
        if (reference.layer == SkipLayer || reference.layer == OutlineLayer) return null;

        var go = new GameObject(reference.name + " (Forge Ghost)");

        var filter = reference.GetComponent<MeshFilter>();
        var sourceRenderer = reference.GetComponent<MeshRenderer>();
        if (filter != null && sourceRenderer != null)
        {
            go.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;

            var renderer = go.AddComponent<MeshRenderer>();
            int slots = Mathf.Max(sourceRenderer.sharedMaterials.Length, 1);
            var materials = new Material[slots];
            for (int i = 0; i < slots; i++) materials[i] = hologram;
            renderer.sharedMaterials = materials;

            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

            go.AddComponent<OutlineObject>().enabled = true;
        }

        foreach (Transform child in reference.transform)
        {
            if (!child.gameObject.activeSelf || !child.gameObject.activeInHierarchy) continue;
            var clone = CloneGraphics(child.gameObject, hologram);
            if (clone == null) continue;
            clone.transform.SetParent(go.transform, worldPositionStays: false);
            clone.transform.localPosition = child.localPosition;
            clone.transform.localRotation = child.localRotation;
            clone.transform.localScale = child.localScale;
        }

        return go;
    }

    private static void SetWorldScale(Transform tr, Vector3 worldScale)
    {
        var parent = tr.parent;
        if (parent == null) { tr.localScale = worldScale; return; }

        var inherited = parent.lossyScale;
        tr.localScale = new Vector3(
            worldScale.x / Mathf.Max(Mathf.Abs(inherited.x), 1e-4f),
            worldScale.y / Mathf.Max(Mathf.Abs(inherited.y), 1e-4f),
            worldScale.z / Mathf.Max(Mathf.Abs(inherited.z), 1e-4f));
    }

    // Borrowed from whatever SocketOutlines the scene already has rather than
    // authored in our own bundle: it is an HDRP material referenced by a private
    // [SerializeField], so shipping a copy would mean shipping a shader we cannot
    // build against — and a lookalike would drift from vanilla the moment the game
    // retunes the effect. Resolved lazily and retried, because SocketOutlines lives
    // on the ship and is not present at plugin load.
    private static Material _hologram;
    private static bool _loggedMissing;

    private static Material HologramMaterial
    {
        get
        {
            if (_hologram != null) return _hologram;

            var outlines = Object.FindObjectOfType<SocketOutlines>();
            if (outlines != null)
                _hologram = AccessTools.Field(typeof(SocketOutlines), "HologramMaterial")
                    ?.GetValue(outlines) as Material;

            if (_hologram != null)
            {
                BepinPlugin.Log.LogDebug($"[Forge] Borrowed hologram material '{_hologram.name}' from SocketOutlines.");
                return _hologram;
            }

            if (!_loggedMissing)
            {
                _loggedMissing = true;
                BepinPlugin.Log.LogWarning(
                    "[Forge] No SocketOutlines hologram material found — insertion previews unavailable " +
                    "(the HUD prompt still works). Will keep retrying.");
            }
            return null;
        }
    }

    private sealed class Ghost
    {
        internal GameObject Root;
        internal readonly GameObject Source;

        private readonly MeshRenderer[] _renderers;
        private readonly int[] _layers;
        private readonly OutlineObject[] _outlines;

        // Null until the first Apply, so the initial state is always written out
        // rather than assumed.
        private bool? _hologramShown;

        internal Ghost(GameObject root, GameObject source)
        {
            Root = root;
            Source = source;
            _renderers = root.GetComponentsInChildren<MeshRenderer>(true);
            _outlines = root.GetComponentsInChildren<OutlineObject>(true);
            _layers = new int[_renderers.Length];
            for (int i = 0; i < _renderers.Length; i++)
                _layers[i] = _renderers[i].gameObject.layer;
        }

        internal int RendererCount => _renderers.Length;

        internal void Apply(bool showOutline, bool showHologram)
        {
            if (Root == null) return;
            if (_hologramShown == showHologram) return;
            _hologramShown = showHologram;

            foreach (var outline in _outlines)
                if (outline != null) outline.enabled = showOutline;

            for (int i = 0; i < _renderers.Length; i++)
            {
                var renderer = _renderers[i];
                if (renderer == null) continue;
                renderer.enabled = showHologram || showOutline;
                renderer.gameObject.layer = showOutline && !showHologram ? OutlineLayer : _layers[i];
            }
        }

        internal void Destroy()
        {
            if (Root != null) Object.Destroy(Root);
            Root = null;
        }
    }
}
