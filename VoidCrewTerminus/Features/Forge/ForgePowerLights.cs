using System.Collections;
using System.Collections.Generic;
using CG.Client.Ship.Hull;
using CG.Game;
using CG.Ship.Modules;
using HarmonyLib;
using UnityEngine;
using VFX.Lights;

namespace VoidCrewTerminus.Forge;

// Vanilla modules dim on a blackout through PoweredLightSource, wired by
// AbstractModuleMediator; the Forge is a bundle prefab with no mediator, so it gets one here.
// The style is borrowed off a vanilla module rather than authored, so the ship's own turn-off
// curve and off-colour can't drift from ours when the game retunes them.
internal sealed class ForgePowerLights : MonoBehaviour
{
    // Its own child rather than the module root: PoweredLightSource.Awake force-sets
    // updateType = Once on whatever RoomPoint shares its GameObject.
    private const string HostName = "TerminusPoweredLight";

    // A module is parented into the hull a frame or two after instantiation. Worth waiting
    // out, because PoweredLightSource.GetAndRegisterController disables itself for good if
    // the controller isn't reachable on its one attempt.
    private const float ControllerWaitSeconds = 30f;

    private static readonly AccessTools.FieldRef<PoweredLightSource, Light[]> LightsRef =
        AccessTools.FieldRefAccess<PoweredLightSource, Light[]>("lights");
    private static readonly AccessTools.FieldRef<PoweredLightSource, Renderer[]> RenderersRef =
        AccessTools.FieldRefAccess<PoweredLightSource, Renderer[]>("renders");

    private static LightSourceSetting _donorSetting;

    private CellModule _module;

    // Idempotent: TryAttach runs from both BuildModule and OnPhotonInstantiate.
    internal static void Attach(CellModule module)
    {
        if (module == null) return;

        // A headless host has no LightSourceController to register with, and
        // PoweredLightSource disables itself there anyway.
        if (ClientGame.Current == null) return;

        if (module.GetComponent<ForgePowerLights>() != null) return;
        if (module.GetComponentInChildren<PoweredLightSource>(true) != null) return;

        module.gameObject.AddComponent<ForgePowerLights>();
    }

    private void Awake() => _module = GetComponent<CellModule>();

    private void Start() => StartCoroutine(WireWhenShipReady());

    private IEnumerator WireWhenShipReady()
    {
        float deadline = Time.time + ControllerWaitSeconds;
        while (GetComponentInParent<ILightSourceController>() == null)
        {
            if (Time.time > deadline)
            {
                BepinPlugin.Log.LogWarning(
                    $"[Forge] {name}: no ILightSourceController in parents after {ControllerWaitSeconds}s — " +
                    "its lights will stay lit through a blackout.");
                yield break;
            }
            yield return null;
        }
        Wire();
    }

    private void Wire()
    {
        var setting = ResolveSetting();
        if (setting == null)
        {
            BepinPlugin.Log.LogWarning(
                $"[Forge] {name}: found no vanilla LightSourceSetting to borrow — " +
                "its lights will stay lit through a blackout.");
            return;
        }

        var lights = CollectLights();
        var renderers = CollectEmissiveRenderers();
        if (lights.Length == 0 && renderers.Length == 0)
        {
            BepinPlugin.Log.LogDebug($"[Forge] {name}: no lights or emissive materials to power-gate.");
            return;
        }

        var host = new GameObject(HostName);
        host.transform.SetParent(transform, false);

        // Inactive first so Awake, and the controller registration it leads to, can't run
        // until the arrays and Setting are in.
        host.SetActive(false);
        var source = host.AddComponent<PoweredLightSource>();
        source.Setting = setting;
        LightsRef(source) = lights;
        RenderersRef(source) = renderers;
        host.SetActive(true);

        // Mirrors AbstractModuleMediator.InitializePoweredLightSources. Start calls Initialize
        // a second time, as vanilla does; registration is guarded internally.
        source.Initialize();
        if (_module != null && _module.PowerDrain != null)
            source.SetPowerDrain(_module.PowerDrain);

        BepinPlugin.Log.LogDebug(
            $"[Forge] Power-gated {lights.Length} light(s) and {renderers.Length} emissive renderer(s) " +
            $"on {name} using '{setting.name}'.");
    }

    // PoweredLightSource captures its references once, so transient ghost previews and docked
    // relics must be filtered out or it keeps dereferencing destroyed ones.
    private Light[] CollectLights()
    {
        var found = new List<Light>();
        foreach (var light in GetComponentsInChildren<Light>(true))
        {
            if (light.GetComponentInParent<ForgeGhostMarker>() != null) continue;
            found.Add(light);
        }
        return found.ToArray();
    }

    private Renderer[] CollectEmissiveRenderers()
    {
        var found = new List<Renderer>();
        foreach (var renderer in GetComponentsInChildren<Renderer>(true))
        {
            if (renderer.GetComponentInParent<ForgeGhostMarker>() != null) continue;
            if (!HasEmission(renderer)) continue;
            found.Add(renderer);
        }
        return found.ToArray();
    }

    // PoweredLightSource's own filter compares against Color.black including alpha, so an
    // _EmissiveColor of (0,0,0,0) reads as emissive there and the pipes and glass would light
    // up red on a blackout. RGB only, which is what actually reaches the shader.
    private static bool HasEmission(Renderer renderer)
    {
        foreach (var material in renderer.sharedMaterials)
        {
            if (material == null) continue;
            if (!material.HasProperty(LightSourceSetting.MATERIAL_EMISSION_ID)) continue;
            if (material.GetColor(LightSourceSetting.MATERIAL_EMISSION_ID).maxColorComponent > 0f)
                return true;
        }
        return false;
    }

    // Prefer a module's setting over a room's, so the Forge reads as a module.
    // FindObjectsOfTypeAll: a module whose lights are currently off still carries it.
    private static LightSourceSetting ResolveSetting()
    {
        if (_donorSetting != null) return _donorSetting;

        LightSourceSetting fallback = null;
        foreach (var source in Resources.FindObjectsOfTypeAll<PoweredLightSource>())
        {
            if (source.Setting == null) continue;
            if (source.GetComponentInParent<ForgePowerLights>() != null) continue;
            if (source.GetComponentInParent<CellModule>() != null)
                return _donorSetting = source.Setting;
            fallback ??= source.Setting;
        }
        return _donorSetting = fallback;
    }
}
