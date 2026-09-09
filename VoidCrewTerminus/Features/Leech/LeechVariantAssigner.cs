using System;
using System.Collections.Generic;
using CG.Ship.Hull;
using CG.Ship.Modules;
using CG.Space;
using UnityEngine;

namespace VoidCrewTerminus.Leech;

// Decides Module-Biter vs Hull-Biter from where the anchor landed. Position, not
// RNG — angling the ship so missiles strike bare hull is a deliberate skill
// expression, so this has to be predictable.
//
// The test is analytic point-to-box against the module's authored footprint, in the
// module's own local space. Centroid distance was rejected: centroid-to-end runs
// 4.90-6.00 on a Large module, so a Leech sitting visibly *on* one would classify
// as a Hull-Biter. Collider bounds were rejected too — the collection reachable
// from a CellModule is the ship's hull, not the module's.
internal static class LeechVariantAssigner
{
    // Footprints come from the authored grid contract: BUILDSOCKET_UNIT_SIZE_HALF
    // is 2, giving a 4.0 unit cell. Small 4x4x4, Medium 8x4x4, Large 8x4x8.
    internal static (float X, float Y, float Z) HalfExtentsFor(BuildSize size) => size switch
    {
        BuildSize.Large => (4f, 2f, 4f),
        BuildSize.Medium => (4f, 2f, 2f),
        _ => (2f, 2f, 2f),
    };

    // Distance from a point to an axis-aligned box centred on the origin. Zero when
    // the point is inside. Takes loose floats rather than a Vector3 so the geometry
    // stays testable — Unity's Vector3 constructor throws under the test host.
    internal static float DistanceToBox(
        float pointX, float pointY, float pointZ,
        float halfX, float halfY, float halfZ)
    {
        float dx = Math.Max(Math.Abs(pointX) - halfX, 0f);
        float dy = Math.Max(Math.Abs(pointY) - halfY, 0f);
        float dz = Math.Max(Math.Abs(pointZ) - halfZ, 0f);

        return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    // CellModule holds no back-reference to the BuildSocket it sits in, so the
    // footprint has to be recovered by walking the ship's sockets. Cached per ship
    // build rather than per spawn.
    private static readonly Dictionary<CellModule, BuildSize> SizeByModule = new();

    internal static void RebuildCache(PlayerControlledShip ship)
    {
        SizeByModule.Clear();
        if (ship == null) return;

        foreach (BuildSocket socket in ship.GetComponentsInChildren<BuildSocket>(includeInactive: true))
        {
            CellModule installed = socket?.InstalledModule;
            if (installed != null) SizeByModule[installed] = socket.Size;
        }

        BepinPlugin.Log.LogDebug($"[Leech] footprint cache rebuilt for {SizeByModule.Count} module(s).");
    }

    internal static BuildSize FootprintOf(CellModule module)
    {
        if (module != null && SizeByModule.TryGetValue(module, out BuildSize size)) return size;

        // An unknown module is treated as Small, the tightest box, so a missing
        // cache entry biases toward Hull-Biter rather than silently widening the
        // Module-Biter radius across the whole ship.
        return BuildSize.Small;
    }

    // Returns the module when the anchor is within the proximity radius of its
    // footprint, or null for a Hull-Biter.
    internal static (LeechVariant Variant, CellModule Module) Assign(
        Vector3 anchor,
        PlayerControlledShip ship,
        float radius)
    {
        CellModule nearest = null;
        float nearestDistance = float.MaxValue;

        foreach (CellModule module in ship.GetAllModules())
        {
            if (module == null) continue;

            float distance = DistanceToFootprint(anchor, module);
            if (distance >= nearestDistance) continue;

            nearestDistance = distance;
            nearest = module;
        }

        if (nearest == null || nearestDistance > radius)
            return (LeechVariant.HullBiter, null);

        return (LeechVariant.ModuleBiter, nearest);
    }

    private static float DistanceToFootprint(Vector3 anchor, CellModule module)
    {
        // Into the module's local space, so the box test is axis-aligned and the
        // module's own rotation is handled for free.
        Vector3 local = module.transform.InverseTransformPoint(anchor);
        (float x, float y, float z) = HalfExtentsFor(FootprintOf(module));

        return DistanceToBox(local.x, local.y, local.z, x, y, z);
    }
}
