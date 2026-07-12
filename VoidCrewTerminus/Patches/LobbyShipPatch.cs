using System.Collections;
using System.Collections.Generic;
using System.Linq;
using CG.Graphics;
using CG.Ship.Shield.Effects;
using HarmonyLib;
using ResourceAssets;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace VoidCrewTerminus.Patches;

[HarmonyPatch(typeof(HubShipManager), nameof(HubShipManager.Start))]
internal class HubShipManagerPatch
{
    static void Postfix(HubShipManager __instance)
    {
        // Start can run repeatedly across hub loads — without this guard every
        // pass stacked another controller, each redoing the full ship preload
        // (observed 19 duplicate loads per ship in the wild).
        if (__instance.gameObject.GetComponent<HangarShipController>() == null)
            __instance.gameObject.AddComponent<HangarShipController>();
    }
}

// Manages full-size ship models spawned in the hangar.
// All three ship prefabs are loaded asynchronously at scene start and parked below
// the level geometry. On selection the relevant model is teleported to its display
// position and faded in — no per-selection load spike.
internal class HangarShipController : MonoBehaviour
{
    private const float HangarScale = 1f;
    private const float ParkY = -5000f;
    private string _currentShipType;
    private Coroutine _swapRoutine;
    private readonly Dictionary<string, GameObject> _cache = [];
    private static readonly Dictionary<string, Shader> _shaderCache = [];

    private static Shader ResolveShader(string name)
    {
        if (!_shaderCache.TryGetValue(name, out var shader))
        {
            shader = Shader.Find(name);
            _shaderCache[name] = shader;
        }
        return shader;
    }

    void Start()
    {
        HubShipManager.Instance.ShipSelectionChanged += OnSelectionChanged;
        StartCoroutine(PreloadAll());
    }

    void OnDestroy()
    {
        if (HubShipManager.Instance != null)
            HubShipManager.Instance.ShipSelectionChanged -= OnSelectionChanged;
        foreach (var go in _cache.Values)
            if (go != null) Destroy(go);
    }

    private void OnSelectionChanged(ShipLoadoutDataDef def)
    {
        var shipType = GetShipType(def);
        if (shipType == null) return;
        BepinPlugin.Log.LogDebug($"[HangarShip] Selected: {shipType}");
        if (_swapRoutine != null) StopCoroutine(_swapRoutine);
        _swapRoutine = StartCoroutine(SwapRoutine(shipType));
    }

    private IEnumerator PreloadAll()
    {
        var container = ResourceAssetContainer<ShipLoadoutDataContainer, ShipLoadoutData, ShipLoadoutDataDef>.Instance;
        var pending = new List<(string shipType, ResourceRequest req)>();
        foreach (var item in container.GetAllItems())
        {
            if (item is not ShipLoadoutDataDef def) continue;
            var shipType = GetShipType(def);
            if (shipType == null) continue;
            var path = def.Asset?.ShipLoadout?.ReferenceShip?.Path;
            if (string.IsNullOrEmpty(path)) continue;
            BepinPlugin.Log.LogDebug($"[HangarShip] Async loading {shipType}: {path}");
            pending.Add((shipType, Resources.LoadAsync<GameObject>(path)));
        }
        foreach (var (shipType, req) in pending)
        {
            yield return req;
            yield return BuildAndCacheRoutine(shipType, req.asset as GameObject);
        }
        if (_currentShipType == null)
        {
            var initialType = GetShipType(HubShipManager.Instance.CurrentShipSelected);
            if (initialType != null)
                _swapRoutine = StartCoroutine(SwapRoutine(initialType));
        }
    }

    // Builds the visual clone across as many frames as needed, yielding whenever the
    // per-frame time budget is spent, so scene start doesn't hitch on three ship
    // builds landing in single frames.
    private IEnumerator BuildAndCacheRoutine(string shipType, GameObject prefab)
    {
        if (prefab == null) { BepinPlugin.Log.LogWarning($"[HangarShip] Load failed for {shipType}"); yield break; }

        float budgetMs = Mathf.Max(0.5f, TerminusConfig.LobbyShipBuildBudgetMs?.Value ?? 3f);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Pass 1 — LOD sets (same rules as before).
        HashSet<Renderer> lod0 = null;
        HashSet<Renderer> allLod = null;
        foreach (var lg in prefab.GetComponentsInChildren<LODGroup>(true))
        {
            var lods = lg.GetLODs();
            for (int i = 0; i < lods.Length; i++)
            {
                foreach (var r in lods[i].renderers)
                {
                    if (r == null) continue;
                    allLod ??= [];
                    allLod.Add(r);
                    if (i == 0) { lod0 ??= []; lod0.Add(r); }
                }
            }
            if (stopwatch.ElapsedMilliseconds >= budgetMs) { yield return null; stopwatch.Restart(); }
        }

        // Pass 2 — decide which renderers to keep, and mark every transform on the
        // path from each kept renderer up to the root. Subtrees with no kept
        // renderer are never cloned at all (the old copy recreated every empty
        // logic/collider node — typically the majority of a ship prefab).
        var needed = new HashSet<Transform>();
        int keptCount = 0;
        foreach (var r in prefab.GetComponentsInChildren<Renderer>(true))
        {
            bool keep = r switch
            {
                MeshRenderer mr => mr.GetComponent<MeshFilter>()?.sharedMesh != null && ShouldInclude(mr, lod0, allLod),
                SkinnedMeshRenderer smr => smr.sharedMesh != null && ShouldInclude(smr, lod0, allLod),
                _ => false,
            };
            if (!keep) continue;
            keptCount++;
            for (var t = r.transform; t != null && t != prefab.transform && needed.Add(t); t = t.parent) { }
            if (stopwatch.ElapsedMilliseconds >= budgetMs) { yield return null; stopwatch.Restart(); }
        }

        if (keptCount == 0) { BepinPlugin.Log.LogWarning($"[HangarShip] No mesh for {shipType}"); yield break; }

        // Pass 3 — clone the pruned hierarchy iteratively, converting materials to
        // transparent/alpha-0 as they're first seen (deduped per source material, so
        // the repeated hull materials are cloned and converted exactly once; this
        // also warms the transparent shader variants during preload).
        var (offset, rot) = GetPositionRot(shipType);
        var parked = new Vector3(transform.position.x + offset.x, ParkY, transform.position.z + offset.z);
        var root = new GameObject("ShipVisual");
        root.transform.SetPositionAndRotation(parked, rot);
        root.transform.localScale = Vector3.one * HangarScale;

        var matCache = new Dictionary<Material, Material>();
        var stack = new Stack<(Transform src, Transform dstParent)>();
        for (int i = prefab.transform.childCount - 1; i >= 0; i--)
            stack.Push((prefab.transform.GetChild(i), root.transform));

        while (stack.Count > 0)
        {
            var (src, dstParent) = stack.Pop();
            if (!needed.Contains(src)) continue;

            var go = new GameObject(src.name);
            go.transform.SetParent(dstParent, false);
            go.transform.localPosition = src.localPosition;
            go.transform.localRotation = src.localRotation;
            go.transform.localScale = src.localScale;

            Mesh mesh = null;
            Material[] mats = null;
            var mr = src.GetComponent<MeshRenderer>();
            var mf = src.GetComponent<MeshFilter>();
            if (mr != null && mf?.sharedMesh != null && ShouldInclude(mr, lod0, allLod))
            {
                mesh = mf.sharedMesh;
                mats = mr.sharedMaterials;
            }
            else
            {
                var smr = src.GetComponent<SkinnedMeshRenderer>();
                if (smr?.sharedMesh != null && ShouldInclude(smr, lod0, allLod))
                {
                    mesh = smr.sharedMesh;
                    mats = smr.sharedMaterials;
                }
            }
            if (mesh != null)
            {
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var converted = new Material[mats.Length];
                for (int i = 0; i < mats.Length; i++)
                    converted[i] = ConvertMaterial(mats[i], matCache);
                go.AddComponent<MeshRenderer>().sharedMaterials = converted;
            }

            for (int i = src.childCount - 1; i >= 0; i--)
                stack.Push((src.GetChild(i), go.transform));

            if (stopwatch.ElapsedMilliseconds >= budgetMs) { yield return null; stopwatch.Restart(); }
        }

        _cache[shipType] = root;
        BepinPlugin.Log.LogDebug($"[HangarShip] Cached {shipType} ({keptCount} renderers, {matCache.Count} materials)");
    }

    // Clone-once-per-source-material: shader remap + transparent + alpha 0.
    private static Material ConvertMaterial(Material src, Dictionary<Material, Material> cache)
    {
        if (src == null) return null;
        if (cache.TryGetValue(src, out var converted)) return converted;
        var mat = new Material(src);
        mat.shader = ResolveShader(mat.shader.name);
        MakeTransparent(mat);
        SetMaterialAlpha(mat, 0f);
        cache[src] = mat;
        return mat;
    }

    private IEnumerator SwapRoutine(string newType)
    {
        if (_currentShipType != null && _currentShipType != newType &&
            _cache.TryGetValue(_currentShipType, out var old))
        {
            yield return Fade(old, 0f);
            var (oldOff, _) = GetPositionRot(_currentShipType);
            old.transform.position = new Vector3(transform.position.x + oldOff.x, ParkY, transform.position.z + oldOff.z);
        }

        while (!_cache.ContainsKey(newType))
            yield return null;

        _currentShipType = newType;
        var go = _cache[newType];
        var (offset, rot) = GetPositionRot(newType);
        go.transform.SetPositionAndRotation(transform.position + offset, rot);
        yield return Fade(go, 1f);
        _swapRoutine = null;
    }

    private static string GetShipType(ShipLoadoutDataDef def)
    {
        var parts = def?.LoadoutContextInfo?.name?.Split("_");
        return parts?.Length >= 2 ? parts[1] : null;
    }

    private (Vector3 offset, Quaternion rot) GetPositionRot(string shipType) => shipType switch
    {
        "Frigate" => (TerminusConfig.FrigateLobbyHangerPosition.Value, Quaternion.Euler(TerminusConfig.FrigateLobbyHangerRot.Value)),
        "Striker" => (TerminusConfig.StrikerLobbyHangerPosition.Value, Quaternion.Euler(TerminusConfig.StrikerLobbyHangerRot.Value)),
        _ => (TerminusConfig.DestroyerLobbyHangerPosition.Value, Quaternion.Euler(TerminusConfig.DestroyerLobbyHangerRot.Value))
    };

    private static bool IsAdditive(Material m) =>
        m != null && m.HasProperty("_SrcBlend") && m.HasProperty("_DstBlend") &&
        (int)m.GetFloat("_SrcBlend") == 1 && (int)m.GetFloat("_DstBlend") == 1;

    private static bool ShouldInclude(Renderer r, HashSet<Renderer> lod0, HashSet<Renderer> allLod)
    {
        // Interior geometry is invisible from the hangar camera. The game's own
        // marker: interior groups sit under OcclusionNodes that hide when the local
        // player is in space; exterior hull nodes keep that flag false so they stay
        // visible from EVA. Checked before the LOD0 shortcut — interior props are
        // LOD-managed too.
        var occlusion = r.GetComponentInParent<OcclusionNode>(true);
        if (occlusion != null && occlusion.HideOnLocalPlayerIsInSpace) return false;

        if (lod0 == null) return true;                          // no LODGroups → include everything
        if (lod0.Contains(r)) return true;                      // hull mesh LOD0
        if (allLod != null && allLod.Contains(r)) return false; // lower-quality LOD level
        if (!r.gameObject.activeSelf) return false;             // inactive by default
        // Shield bubble: renderer sits under ShieldEmitterAnimatorEffect / ShieldSphereRotator parent
        if (r.GetComponentInParent<ShieldEmitterAnimatorEffect>(true) != null) return false;
        if (r.GetComponentInParent<ShieldSphereRotator>(true) != null) return false;
        // Thruster glow and other additive effects: every material is pure additive blend
        var mats = r.sharedMaterials;
        if (mats.Length > 0 && mats.All(IsAdditive)) return false;
        return true;
    }

    private static void MakeTransparent(Material mat)
    {
        if (mat.HasProperty("_SurfaceType"))
        {
            mat.SetFloat("_SurfaceType", 1f);
            mat.SetFloat("_BlendMode", 0f);
            mat.SetFloat("_ZWrite", 0f);
            mat.SetFloat("_TransparentZWrite", 0f);
            mat.renderQueue = 3000;
            HDMaterial.ValidateMaterial(mat);
            return;
        }
        if (mat.HasProperty("_Surface"))
        {
            mat.SetFloat("_Surface", 1f); mat.SetFloat("_Blend", 0f);
            mat.SetFloat("_ZWrite", 0f); mat.SetFloat("_SrcBlend", 5f); mat.SetFloat("_DstBlend", 10f);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = 3000;
        }
    }

    private static void MakeOpaque(Material mat)
    {
        if (mat.HasProperty("_SurfaceType"))
        {
            mat.SetFloat("_SurfaceType", 0f);
            mat.SetFloat("_ZWrite", 1f);
            mat.renderQueue = 2000;
            if (mat.HasProperty("_BaseColor")) { var c = mat.GetColor("_BaseColor"); c.a = 1f; mat.SetColor("_BaseColor", c); }
            HDMaterial.ValidateMaterial(mat);
            return;
        }
        if (mat.HasProperty("_Surface"))
        {
            mat.SetFloat("_Surface", 0f); mat.SetFloat("_ZWrite", 1f);
            mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = 2000;
        }
    }

    private static void ApplyPerMaterial(GameObject go, System.Action<Material> fn)
    {
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            foreach (var mat in r.sharedMaterials)
                if (mat != null) fn(mat);
    }

    private static void SetMaterialAlpha(Material mat, float alpha)
    {
        if (mat.HasProperty("_BaseColor"))
        {
            var c = mat.GetColor("_BaseColor"); c.a = alpha; mat.SetColor("_BaseColor", c);
        }
        if (mat.HasProperty("_Color"))
        {
            var c = mat.GetColor("_Color"); c.a = alpha; mat.SetColor("_Color", c);
        }
    }

    private IEnumerator Fade(GameObject go, float target)
    {
        if (go == null) yield break;
        float duration = TerminusConfig.LobbyShipFadeDuration?.Value ?? 0.6f;

        ApplyPerMaterial(go, MakeTransparent);

        if (duration <= 0f) { SetAlpha(go, target); }
        else
        {
            float start = GetAlpha(go);
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                SetAlpha(go, Mathf.Lerp(start, target, Mathf.Clamp01(elapsed / duration)));
                yield return null;
            }
            SetAlpha(go, target);
        }

        if (target >= 1f)
            ApplyPerMaterial(go, MakeOpaque);
    }

    private static float GetAlpha(GameObject go)
    {
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            foreach (var mat in r.sharedMaterials)
            {
                if (mat == null) continue;
                if (mat.HasProperty("_BaseColor")) return mat.GetColor("_BaseColor").a;
                if (mat.HasProperty("_Color")) return mat.GetColor("_Color").a;
            }
        return 1f;
    }

    private static void SetAlpha(GameObject go, float alpha)
    {
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            foreach (var mat in r.sharedMaterials)
            {
                if (mat == null) continue;
                SetMaterialAlpha(mat, alpha);
            }
    }
}
