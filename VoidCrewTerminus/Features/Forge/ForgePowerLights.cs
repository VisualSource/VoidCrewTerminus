using System.Collections;
using System.Collections.Generic;
using CG.Client.Ship.Hull;
using CG.Game;
using CG.Ship.Modules;
using HarmonyLib;
using UnityEngine;
using VFX.Lights;

namespace VoidCrewTerminus.Forge;

// Makes the Forge go dark with the rest of the ship.
//
// Vanilla modules dim and redden on a blackout through PoweredLightSource:
// AbstractModuleMediator.Start hands every one under the module its
// CellModule.PowerDrain, and PoweredLightSource registers itself with the ship's
// LightSourceController so it also hears the global LightEvents — PowerOff,
// PowerFailure, the alarm cycle, silent mode, impulse. The Forge is a bundle-loaded
// prefab with no mediator, so nobody ever did either for it, and its interior Point
// Light burned full white through a blackout while every neighbouring module went out.
//
// The style is BORROWED off a vanilla module rather than authored here: the ship's own
// turn-off curve, transition time and off-colour are the whole point, and a hand-picked
// approximation would drift the first time the game retunes them.
//
// Nothing needs to be authored in the prefab for this — a light or an emissive material
// is found and gated if present, and the graft no-ops if neither is.
internal sealed class ForgePowerLights : MonoBehaviour
{
    // Its own child rather than the module root: PoweredLightSource.Awake force-sets
    // updateType = Once on whatever RoomPoint shares its GameObject, and the root's is
    // fair game for anything else that samples the room.
    private const string HostName = "TerminusPoweredLight";

    // A module is instantiated at the socket's world position and only parented into the
    // hull a frame or two later — the same race RegisterShipPlatformCollision guards.
    // Worth waiting out, because PoweredLightSource.GetAndRegisterController disables
    // itself for good if the controller isn't reachable on its one attempt.
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

        // Inactive first so Awake doesn't run — and so the controller registration it
        // leads to can't reach Setting.GetStyle — until the arrays and Setting are in.
        host.SetActive(false);
        var source = host.AddComponent<PoweredLightSource>();
        source.Setting = setting;
        LightsRef(source) = lights;
        RenderersRef(source) = renderers;
        host.SetActive(true);

        // Mirrors AbstractModuleMediator.InitializePoweredLightSources. PoweredLightSource.Start
        // calls Initialize a second time — vanilla double-initializes the same way, and
        // registration is guarded internally, so it only re-reads the captured originals.
        source.Initialize();
        if (_module != null && _module.PowerDrain != null)
            source.SetPowerDrain(_module.PowerDrain);

        BepinPlugin.Log.LogDebug(
            $"[Forge] Power-gated {lights.Length} light(s) and {renderers.Length} emissive renderer(s) " +
            $"on {name} using '{setting.name}'.");
    }

    // Ghost previews and docked relics are transient; PoweredLightSource captures its
    // references once and would keep dereferencing destroyed ones. Neither exists this
    // early, but the ghost filter matches ForgeOutline's and says so out loud.
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

    // PoweredLightSource filters for emissive materials itself, but on `== Color.black`,
    // which compares alpha too — every HDRP/Lit material the prefab ships stores
    // _EmissiveColor as (0,0,0,0) or (0,0,0,1), so the (0,0,0,0) half reads as "emissive"
    // there and the pipes and glass would light up red on a blackout. RGB only, which is
    // what actually reaches the shader. Empty today; anything the prefab gains later is
    // picked up without touching this file.
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

    // Prefer a module's setting over a room's — the Forge should read as a module.
    // FindObjectsOfTypeAll rather than FindObjectsOfType: a module whose lights are
    // currently off still carries the setting we want.
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
