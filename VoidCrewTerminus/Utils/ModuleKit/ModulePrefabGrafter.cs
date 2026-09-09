using System.Collections.Generic;
using CG.Game.Configuration;
using CG.Graphics;
using CG.Ship.Modules;
using Gameplay.Power;
using Gameplay.Utilities;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace VoidCrewTerminus.ModuleKit;

// Script components don't survive the bundle pipeline, so a prefab arrives as meshes and
// a VoidCrewAsset marker and nothing else. These are the minimum stand-ins the build
// flow, PhotonNetwork.Instantiate and the power/culling systems need to not NRE.
internal static class ModulePrefabGrafter
{
    // Bundle-loaded shaders carry a different keyword/variant set than the player
    // build's copy, which renders solid black — re-resolve by name to fix it.
    // sharedMaterials since this runs on the prefab asset, not an instance.
    internal static void RelinkShaders(GameObject prefab)
    {
        foreach (var rend in prefab.GetComponentsInChildren<Renderer>(true))
        {
            foreach (var mat in rend.sharedMaterials)
            {
                if (mat == null || mat.shader == null) continue;
                var shader = Shader.Find(mat.shader.name);
                if (shader == null) continue;

                // Reassigning .shader resets renderQueue to the new shader's default
                // (opaque range) even though it leaves _SurfaceType/_SrcBlend/_DstBlend
                // untouched — HDRP sorts opaque-vs-transparent off renderQueue alone, so
                // a transparent material (e.g. Glass) silently draws fully opaque here.
                var queue = mat.renderQueue;
                mat.shader = shader;
                mat.renderQueue = queue;
            }
        }
    }

    // Deconstruction forbidden here since there's no BuildBoxRef yet (Deconstruct would NRE);
    // RegisteredModule.LinkBuildBoxRef sets it once the box guid is known.
    // MaxHitPoints/Invulnerability must be initialized: OrbitObject.Start NREs otherwise.
    internal static void Graft(GameObject prefab)
    {
        var cell = prefab.GetComponent<CellModule>();
        if (cell == null)
        {
            cell = prefab.AddComponent<CellModule>();
            cell.BuildingConstraints = BuildingConstraints.Default;
            cell.BuildingConstraints.AllowDeconstruction = false;
            cell.TimeToBoot = 1f;
            BepinPlugin.Log.LogDebug($"[ModuleKit] Grafted CellModule onto {prefab.name}");
        }
        cell.MaxHitPoints ??= new ModifiableFloat { BaseValue = 750f };
        cell.Invulnerability ??= new ModifiableInt();

        // BuildSocket.SetModule dereferences module.PowerDrain unconditionally, so it needs
        // a real one. PowerWanted stays 0; AutoPowerOn brings it up on connect.
        var drain = prefab.GetComponent<PowerDrain>();
        if (drain == null)
        {
            drain = prefab.AddComponent<PowerDrain>();
            drain.PowerWanted = new ModifiableInt();
            drain.IsOn = false;
            drain.AutoPowerOn = true;
            BepinPlugin.Log.LogDebug($"[ModuleKit] Grafted PowerDrain onto {prefab.name}");
        }
        if (cell.PowerDrain == null) cell.PowerDrain = drain;

        // Mirrors vanilla culling: Interior/Exterior child groups each get their own
        // OcclusionNode (Exterior stays visible from space/turrets); no split falls
        // back to one node on the root.
        if (prefab.GetComponentInChildren<OcclusionNode>(true) == null)
        {
            var interior = prefab.transform.Find("Interior");
            var exterior = prefab.transform.Find("Exterior");
            if (interior == null && exterior == null)
            {
                prefab.AddComponent<OcclusionNode>();
                BepinPlugin.Log.LogDebug($"[ModuleKit] Grafted root OcclusionNode onto {prefab.name} (no Interior/Exterior split)");
            }
            else
            {
                if (interior != null)
                    interior.gameObject.AddComponent<OcclusionNode>();
                if (exterior != null)
                {
                    var node = exterior.gameObject.AddComponent<OcclusionNode>();
                    AccessTools.Field(typeof(OcclusionNode), "occlusionZone").SetValue(node, OcclusionZoneType.Exterior);
                    AccessTools.Field(typeof(OcclusionNode), "hideOnLocalPlayerIsInSpace").SetValue(node, false);
                    AccessTools.Field(typeof(OcclusionNode), "hideOnLocalPlayerIsInTurret").SetValue(node, false);
                }
                BepinPlugin.Log.LogDebug($"[ModuleKit] Grafted OcclusionNodes onto {prefab.name} (interior={(interior != null)}, exterior={(exterior != null)})");
            }
        }

        var view = prefab.GetComponent<PhotonView>();
        if (view == null)
        {
            view = prefab.AddComponent<PhotonView>();
            view.OwnershipTransfer = OwnershipOption.Takeover;
            view.Synchronization = ViewSynchronization.UnreliableOnChange;
            BepinPlugin.Log.LogDebug($"[ModuleKit] Grafted PhotonView onto {prefab.name}");
        }
        // A Manual-search view with an empty ObservedComponents list syncs nothing —
        // ensure it observes the module even when authored in the editor.
        if (view.observableSearch == PhotonView.ObservableSearch.Manual &&
            (view.ObservedComponents == null || view.ObservedComponents.Count == 0))
        {
            view.ObservedComponents = new List<Component> { cell };
        }

        // PowerDrain is the only carrier of IsOn over the wire (CellModule's
        // OnPhotonSerializeView writes IsBeingDeconstructed and nothing else), so an
        // unobserved drain leaves every non-owner stuck at IsOn == false forever.
        // Vanilla prefabs list their drain in the editor; a grafted one is added here.
        // Appended, not assigned, so an authored list survives.
        if (drain != null)
        {
            view.ObservedComponents ??= new List<Component>();
            if (!view.ObservedComponents.Contains(drain))
            {
                view.ObservedComponents.Add(drain);
                BepinPlugin.Log.LogDebug($"[ModuleKit] PhotonView on {prefab.name} now observes PowerDrain (IsOn replication).");
            }
        }
    }
}
