using System.Collections;
using System.Collections.Generic;
using System.Linq;
using CG.Ship.Shield.Effects;
using HarmonyLib;
using ResourceAssets;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace VoidCrewTerminus.Patches;

// Attaches HangarShipController to HubShipManager's GameObject when the hub scene loads.
[HarmonyPatch(typeof(HubShipManager), nameof(HubShipManager.Start))]
internal class HubShipManagerPatch
{
    static void Postfix(HubShipManager __instance)
    {
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
        foreach (var item in container.GetAllItems())
        {
            if (item is not ShipLoadoutDataDef def) continue;
            yield return StartCoroutine(BuildAndCache(def));
        }
        if (_currentShipType == null)
        {
            var initialType = GetShipType(HubShipManager.Instance.CurrentShipSelected);
            if (initialType != null)
                _swapRoutine = StartCoroutine(SwapRoutine(initialType));
        }
    }

    private IEnumerator BuildAndCache(ShipLoadoutDataDef def)
    {
        var shipType = GetShipType(def);
        if (shipType == null) yield break;

        var path = def.Asset?.ShipLoadout?.ReferenceShip?.Path;
        if (string.IsNullOrEmpty(path)) yield break;

        BepinPlugin.Log.LogDebug($"[HangarShip] Async loading {shipType}: {path}");
        var req = Resources.LoadAsync<GameObject>(path);
        yield return req;

        var prefab = req.asset as GameObject;
        if (prefab == null) { BepinPlugin.Log.LogWarning($"[HangarShip] Load failed: {path}"); yield break; }

        var (offset, rot) = GetPositionRot(shipType);
        var parked = new Vector3(transform.position.x + offset.x, ParkY, transform.position.z + offset.z);
        var go = BuildVisualMesh(prefab, parked, rot);
        if (go == null) { BepinPlugin.Log.LogWarning($"[HangarShip] No mesh for {shipType}"); yield break; }

        go.transform.localScale = Vector3.one * HangarScale;
        ApplyPerMaterial(go, MakeTransparent); // warms transparent shader variant during preload
        SetAlpha(go, 0f);
        _cache[shipType] = go;
        BepinPlugin.Log.LogDebug($"[HangarShip] Cached {shipType}");
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

    private static GameObject BuildVisualMesh(GameObject prefab, Vector3 position, Quaternion rotation)
    {
        // Build two sets from every LODGroup in the prefab:
        //   lod0    — renderers at LOD0 (highest detail hull mesh) → always include
        //   allLod  — renderers at any LOD level → used to detect LOD-managed objects
        // Renderers NOT in allLod are "non-LOD" (glass, details, effects, shields).
        // We include those only when they are active in the prefab (activeSelf),
        // which filters out shields and effects that are off by default.
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
        }

        var root = new GameObject("ShipVisual");
        root.transform.SetPositionAndRotation(position, rotation);
        if (CopyHierarchy(prefab.transform, root.transform, lod0, allLod) == 0)
        {
            Destroy(root);
            return null;
        }

        return root;
    }

    private static bool IsAdditive(Material m) =>
        m != null && m.HasProperty("_SrcBlend") && m.HasProperty("_DstBlend") &&
        (int)m.GetFloat("_SrcBlend") == 1 && (int)m.GetFloat("_DstBlend") == 1;

    private static bool ShouldInclude(Renderer r, HashSet<Renderer> lod0, HashSet<Renderer> allLod)
    {
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

    private static int CopyHierarchy(Transform src, Transform dst, HashSet<Renderer> lod0, HashSet<Renderer> allLod)
    {
        int count = 0;
        foreach (Transform child in src)
        {
            var go = new GameObject(child.name);
            go.transform.SetParent(dst, false);
            go.transform.localPosition = child.localPosition;
            go.transform.localRotation = child.localRotation;
            go.transform.localScale = child.localScale;

            var mr = child.GetComponent<MeshRenderer>();
            var mf = child.GetComponent<MeshFilter>();
            if (mr != null && mf?.sharedMesh != null && ShouldInclude(mr, lod0, allLod))
            {
                go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                CopyMaterials(go.AddComponent<MeshRenderer>(), mr.sharedMaterials);
                count++;
            }

            var smr = child.GetComponent<SkinnedMeshRenderer>();
            if (smr?.sharedMesh != null && ShouldInclude(smr, lod0, allLod))
            {
                go.AddComponent<MeshFilter>().sharedMesh = smr.sharedMesh;
                CopyMaterials(go.AddComponent<MeshRenderer>(), smr.sharedMaterials);
                count++;
            }

            count += CopyHierarchy(child, go.transform, lod0, allLod);
        }
        return count;
    }

    private static void CopyMaterials(MeshRenderer dst, Material[] src)
    {
        var mats = new Material[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            var mat = new Material(src[i]);
            mat.shader = Shader.Find(mat.shader.name);
            mats[i] = mat;
        }
        dst.materials = mats;
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
            foreach (var mat in r.materials)
                fn(mat);
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
            foreach (var mat in r.materials)
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
    }
}
