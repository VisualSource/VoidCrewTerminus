using System.Collections;
using DG.Tweening;
using HarmonyLib;
using ResourceAssets;
using UnityEngine;

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

// Manages a full-size ship hologram spawned in the hangar.
// Subscribes to HubShipManager.ShipSelectionChanged (already PUN-synced to all clients)
// so no additional networking is required.
internal class HangarShipController : MonoBehaviour
{
    // Offset in world space from HubShipManager's position into the hangar bay.
    // Tune these until the ship appears in the correct spot visible through the window.
    private static readonly Vector3 DestroyerHangarOffset = new Vector3(0, 0, -100);
    private static readonly Quaternion DestroyerHangerRot = Quaternion.identity;
    private static readonly Vector3 StrikerHangarOffset = new Vector3(0, -20, -75);
    private static readonly Quaternion StrikerHangerRot = Quaternion.identity;
    private static readonly Vector3 FrigateHangarOffset = new Vector3(0, -20, -75);
    private static readonly Quaternion FrigateHangerRot = Quaternion.Euler(new Vector3(0, 20, 0));

    // Uniform scale applied to the spawned hologram. Adjust if the prefab appears too small or large.
    private static readonly float HangarScale = 1f;

    private const float FadeDuration = 0.6f;

    private GameObject _hologram;
    private Coroutine _swapRoutine;

    void Start()
    {
        HubShipManager.Instance.ShipSelectionChanged += OnSelectionChanged;

        var initial = HubShipManager.Instance.CurrentShipSelected;
        if (initial is not null)
        {
            BepinPlugin.Log.LogInfo($"[HangarShip] Initial ship: '{initial.LoadoutContextInfo?.name}'");
            _swapRoutine = StartCoroutine(SwapRoutine(null, initial));
        }
    }

    void OnDestroy()
    {
        if (HubShipManager.Instance != null)
            HubShipManager.Instance.ShipSelectionChanged -= OnSelectionChanged;

        if (_hologram != null)
            Destroy(_hologram);
    }

    private void OnSelectionChanged(ShipLoadoutDataDef def)
    {
        BepinPlugin.Log.LogInfo($"[HangarShip] Ship selected: '{def?.LoadoutContextInfo?.name}'");
        if (_swapRoutine != null)
            StopCoroutine(_swapRoutine);

        var old = _hologram;
        _hologram = null;
        _swapRoutine = StartCoroutine(SwapRoutine(old, def));
    }

    private IEnumerator SwapRoutine(GameObject old, ShipLoadoutDataDef def)
    {
        var prefab = def?.LoadoutContextInfo?.ShipHologramPrefab;
        if (prefab == null) yield break;

        if (old != null)
        {
            yield return Fade(old, 0f);
            Destroy(old);
        }


        var name = def.LoadoutContextInfo.name;

        Vector3 offset;
        Quaternion rot;

        if (name.Contains("Frigate"))
        {
            offset = FrigateHangarOffset;
            rot = FrigateHangerRot;
        }
        else if (name.Contains("Striker"))
        {
            offset = StrikerHangarOffset;
            rot = StrikerHangerRot;
        }
        else //  name.Contains("Destroyer")
        {
            offset = DestroyerHangarOffset;
            rot = DestroyerHangerRot;
        }

        var go = Instantiate(prefab, transform.position + offset, rot);
        go.transform.localScale = Vector3.one * HangarScale;
        SetAlpha(go, 0f);
        _hologram = go;

        yield return Fade(go, 1f);
        _swapRoutine = null;
    }

    private static IEnumerator Fade(GameObject go, float target)
    {
        if (go == null) yield break;

        DOVirtual.Float(GetAlpha(go), target, FadeDuration, v =>
        {
            if (go != null) SetAlpha(go, v);
        });
        yield return new WaitForSeconds(FadeDuration);
    }

    private static float GetAlpha(GameObject go)
    {
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
        {
            var mat = r.material;
            if (mat.HasProperty("_Color")) return mat.GetColor("_Color").a;
            if (mat.HasProperty("_BaseColor")) return mat.GetColor("_BaseColor").a;
        }
        return 1f;
    }

    private static void SetAlpha(GameObject go, float alpha)
    {
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
        {
            foreach (var mat in r.materials)
            {
                if (mat.HasProperty("_Color"))
                {
                    var c = mat.GetColor("_Color");
                    c.a = alpha;
                    mat.SetColor("_Color", c);
                }
                if (mat.HasProperty("_BaseColor"))
                {
                    var c = mat.GetColor("_BaseColor");
                    c.a = alpha;
                    mat.SetColor("_BaseColor", c);
                }
            }
        }
    }
}
