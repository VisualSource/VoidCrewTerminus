using System.Collections.Generic;
using CG.Objects;
using CG.Rendering;
using HarmonyLib;
using Knife.HDRPOutline.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace VoidCrewTerminus.Forge;

// Marks a rendering-only preview clone, so ForgeOutline's renderer sweep can skip
// it — that sweep runs once per module and is cached forever, so a ghost caught in
// it would be captured permanently and flicker with the module's hover highlight.
internal sealed class ForgeGhostMarker : MonoBehaviour { }

// The translucent blue placement preview, on the Forge's anchors.
//
// Vanilla's CG.Rendering.SocketOutlines does this for ship sockets, but drives off
// CarryablesSocket.OnSocketAdded and so never sees the Forge's plain anchors
// (ADR-0002). Reimplemented here against vanilla's own HologramMaterial, so the
// preview is the same blue rather than a lookalike that drifts on a game update.
//
// Two visual states, both from SocketOutlines.HighlightObjects.ShowHighlight:
// aimed elsewhere draws an outline-layer silhouette; aimed at the anchor draws the
// full translucent solid.
//
// Purely local and cosmetic — reads LocalPlayer, touches no physics, ownership or
// network state, so it needs no sync.
internal sealed class ForgeGhosts
{
    // SocketOutlines.OUTLINE_LAYER — renderers parked here draw in the outline pass
    // alone. SkipLayer (15) is the other layer it refuses to clone from.
    private const int OutlineLayer = 3;
    private const int SkipLayer = 15;

    private readonly Dictionary<Transform, Ghost> _ghosts = new();

    internal int Count => _ghosts.Count;

    internal void Show(Transform anchor, CarryableObject source, AnchorAlign align)
    {
        if (anchor == null || source == null) return;

        var sourceGo = source.gameObject;
        if (_ghosts.TryGetValue(anchor, out var existing))
        {
            // The common case — the refresh tick re-asserts every accepted anchor
            // several times a second.
            if (existing.Root != null && existing.Source == sourceGo) return;
            existing.Destroy();
            _ghosts.Remove(anchor);
        }

        var hologram = HologramMaterial;
        if (hologram == null) return;

        var ghost = Build(anchor, source, align, hologram);
        if (ghost != null) _ghosts[anchor] = ghost;
    }

    internal void Hide(Transform anchor)
    {
        if (anchor == null || !_ghosts.TryGetValue(anchor, out var ghost)) return;
        ghost.Destroy();
        _ghosts.Remove(anchor);
    }

    // Runs every frame, unlike the rebuild — hover feedback lagging by a refresh
    // interval would feel broken.
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

    private static Ghost Build(Transform anchor, CarryableObject source, AnchorAlign align, Material hologram)
    {
        var sourceTr = source.transform;
        var root = CloneGraphics(sourceTr.gameObject, hologram);
        if (root == null) return null;

        root.AddComponent<ForgeGhostMarker>();

        // Parented to the anchor so it rides the ship for free — no rigidbody, so it
        // needs none of AnchorDock's per-frame pinning.
        root.transform.SetParent(anchor, worldPositionStays: false);

        // Must match AnchorDock.PlaceAtAnchor's pivot choice, or the preview lands
        // somewhere the item won't — e.g. previewing a BuildBox with its BasePivot
        // pins the top to the module socket's center instead of centering the box.
        var pivot = align == AnchorAlign.Center ? source.CenterPivot : source.BasePivot;
        if (pivot == null) pivot = sourceTr;
        ForgeAnchors.ComputeDockedPose(sourceTr, pivot, anchor, out var pos, out var rot);
        root.transform.SetPositionAndRotation(pos, rot);

        // Without this the ghost inherits the anchor's scale outright, and the Forge's
        // anchors ride FBX nodes with tiny non-uniform scales.
        SetWorldScale(root.transform, sourceTr.lossyScale);

        var ghost = new Ghost(root, sourceTr.gameObject);
        BepinPlugin.Log.LogDebug(
            $"[Forge] Ghost of {sourceTr.gameObject.name} on anchor '{anchor.name}': " +
            $"{ghost.RendererCount} renderer(s).");
        return ghost;
    }

    // SocketOutlines.RecursiveInstantiateGraphics, reimplemented: bare GameObjects
    // carrying only meshes, no shadows, probes or colliders — nothing that could
    // interact with the world.
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

    // Resolved lazily and retried on failure — SocketOutlines lives on the ship and
    // is not present at plugin load.
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

        // Null until the first Apply, so the initial state is always written.
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
