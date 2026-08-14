using System.Collections.Generic;
using CG.Objects;
using UnityEngine;

namespace VoidCrewTerminus.Forge;

// The items physically held on an Upgrade Forge's anchors.
//
// Inserted relics and the loaded BuildBox are never absorbed into an inventory —
// they stay live in the world, frozen onto their anchor, pinned there while the
// ship moves, and still grabbable. That leaves this class holding the hazardous
// half of the feature: two rigidbodies per item, a per-frame teleport against a
// moving platform, and the velocity that silently accumulates from it.
//
// Not tested, and not testable: every line is a Unity physics call, and a method
// body containing one cannot even be JIT-compiled in the test host. What splitting
// it out buys is locality rather than coverage — the float-away bug, the
// simulated-body rule and the kinematic-before-velocity ordering are recorded
// once, here, instead of being spread through a MonoBehaviour's Update and
// LateUpdate where they read as incidental.
//
// It knows nothing about relics, module sockets or the network. It reports which
// items have left; the Forge decides what that means and who to tell.
internal sealed class AnchorDock
{
    private readonly Dictionary<GameObject, Transform> _docked = new();

    // Reused across frames: Reconcile runs every Update and must not allocate.
    private readonly List<KeyValuePair<GameObject, Transform>> _departedScratch = new();

    internal bool IsDocked(GameObject item) => item != null && _docked.ContainsKey(item);

    // Whether something is physically docked on the given anchor. Read by
    // ForgeInteractable so it can step aside and let a docked item be grabbed.
    internal bool IsOccupied(Transform anchor) => anchor != null && _docked.ContainsValue(anchor);

    internal void Dock(GameObject item, Transform anchor)
    {
        if (item == null || anchor == null) return;
        _docked[item] = anchor;

        var co = item.GetComponent<CarryableObject>();
        // Freeze the simulation body too, not just the main one — otherwise it
        // keeps integrating in the platform's physics scene the whole time the
        // item is docked, which is what the per-frame pin was accumulating into.
        SetDockedKinematic(co, item, true);
        PlaceAtAnchor(item, co, anchor);
        ForgeAnchors.SetFilled(anchor, true);
    }

    // Return one item to the simulation. False when it was not docked here.
    internal bool Undock(GameObject item)
    {
        if (item == null || !_docked.TryGetValue(item, out var anchor)) return false;
        _docked.Remove(item);
        ForgeAnchors.SetFilled(anchor, false);
        ReleaseRigidbody(item);
        return true;
    }

    // Keep docked items pinned to their anchors while the ship moves. Driven from
    // the Forge's LateUpdate.
    internal void Pin()
    {
        if (_docked.Count == 0) return;
        foreach (var kv in _docked)
        {
            if (kv.Key == null || kv.Value == null) continue;
            var co = kv.Key.GetComponent<CarryableObject>();
            // Skip pinning once a player has grabbed the item — Reconcile undocks
            // it next frame. Otherwise the pin snaps a carried box back to the
            // anchor, and the accumulated teleport delta produces a visible drift
            // when the rigidbody wakes.
            if (co != null && co.Carrier != null) continue;
            PlaceAtAnchor(kv.Key, co, kv.Value);
        }
    }

    // Items leave in exactly two ways: destroyed (a commit consumed the relic) or
    // grabbed back out through the vanilla Grabbable flow. Destroyed ones are
    // reaped silently — there is nothing left to release and nothing to announce.
    // The ones a player is now carrying are appended to `grabbed`, still alive and
    // still owed an explanation to the rest of the crew.
    internal void Reconcile(List<KeyValuePair<GameObject, Transform>> grabbed)
    {
        if (_docked.Count == 0) return;

        foreach (var kv in _docked)
        {
            var go = kv.Key;
            if (go == null) { _departedScratch.Add(kv); continue; }
            var co = go.GetComponent<CarryableObject>();
            if (co != null && co.Carrier != null) _departedScratch.Add(kv);
        }

        foreach (var kv in _departedScratch)
        {
            _docked.Remove(kv.Key);
            ForgeAnchors.SetFilled(kv.Value, false);
            if (kv.Key == null) continue;
            ReleaseRigidbody(kv.Key);
            grabbed.Add(kv);
        }
        _departedScratch.Clear();
    }

    // Let everything go at once, restoring its physics, so nothing is left frozen
    // mid-air when the Forge that owned it goes away (hot-reload teardown).
    internal void ReleaseAll()
    {
        foreach (var kv in _docked)
        {
            if (kv.Key == null) continue;
            ForgeAnchors.SetFilled(kv.Value, false);
            ReleaseRigidbody(kv.Key);
        }
        _docked.Clear();
    }

    // ---- physics ----------------------------------------------------------

    // Base-pivot alignment: the item's BasePivot lands on the anchor's origin with
    // its axes matching the anchor's axes (anchor green/Y = item up, blue/Z = item
    // facing). Same intent as CarryablesSocket.PlaceCarryableOnSocket, but computed
    // with quaternions instead of the anchor's matrices — vanilla store transforms
    // are unit-scale, while our anchors inherit rotated, non-uniformly scaled FBX
    // nodes whose matrices skew a rotation extracted from them.
    private static void PlaceAtAnchor(GameObject item, CarryableObject co, Transform anchor)
    {
        var itemTr = item.transform;
        var pivot = co != null ? co.BasePivot : itemTr;
        var finalRot = anchor.rotation * Quaternion.Inverse(pivot.rotation) * itemTr.rotation;
        var delta = finalRot * Quaternion.Inverse(itemTr.rotation);
        var finalPos = anchor.position - delta * (pivot.position - itemTr.position);
        itemTr.SetPositionAndRotation(finalPos, finalRot);
    }

    // Freeze/unfreeze BOTH bodies a CarryableObject can be simulated through.
    //
    // CarryableObject is an ISimulatedBody: while it rides a MovingSpacePlatform
    // (i.e. the ship) its real physics lives on SimulationRigidbody in a separate
    // physics scene, NOT on MainRigidbody. Note that Rigidbody and MainRigidbody
    // both return the same field, so there's no "active body" accessor — the
    // simulation body has to be addressed explicitly.
    //
    // Both are set unconditionally rather than branching on IsBeingSimulated,
    // because that flag can flip while an item is docked (the ship starts or
    // stops being simulated) and a body left un-frozen would integrate in the
    // background the whole time.
    private static void SetDockedKinematic(CarryableObject co, GameObject go, bool kinematic)
    {
        var main = co != null ? co.MainRigidbody : go.GetComponent<Rigidbody>();
        if (main != null) main.isKinematic = kinematic;

        var sim = co != null ? co.SimulationRigidbody : null;
        if (sim != null) sim.isKinematic = kinematic;
    }

    // Return a docked item to the physics simulation.
    //
    // The per-frame Pin against a moving-ship anchor accumulates an implicit
    // velocity estimate; without zeroing it the item inherits roughly the ship's
    // velocity the instant kinematic goes false and drifts through the ship — the
    // "BuildBox floats away" bug.
    //
    // The original fix wrote straight to MainRigidbody, which is the WRONG body
    // whenever the item is being simulated on the ship platform — the accumulated
    // velocity survived on SimulationRigidbody and the box drifted anyway. That's
    // why it only reproduced intermittently: it depended on whether platform
    // simulation happened to be active at that moment.
    //
    // Going through the Velocity/AngularVelocity properties routes to whichever
    // body is live. Zero is safe in either space (the simulated branch applies an
    // inverse platform rotation, and rotating zero yields zero).
    private static void ReleaseRigidbody(GameObject go)
    {
        if (go == null) return;
        var co = go.GetComponent<CarryableObject>();

        bool simulated = co != null && co.IsBeingSimulated;
        Vector3 before = Vector3.zero;
        try { before = co != null ? co.Velocity : Vector3.zero; }
        catch { /* SimulationPlatform can be null mid-transition; not worth failing the undock */ }

        // MUST clear kinematic BEFORE assigning velocity: the property's
        // non-simulated branch is `else if (!rigidBody.isKinematic)`, so writing
        // velocity to a still-kinematic body is silently dropped.
        SetDockedKinematic(co, go, false);

        if (co != null)
        {
            try
            {
                co.Velocity = Vector3.zero;
                co.AngularVelocity = Vector3.zero;
            }
            catch (System.Exception e)
            {
                BepinPlugin.Log.LogWarning($"[Forge] undock {go.name}: velocity zeroing failed ({e.GetType().Name}) — falling back to main body.");
                var rb = co.MainRigidbody;
                if (rb != null) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            }
        }
        else
        {
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
        }

        // The float-away is rare and not reliably reproducible, so it has to be
        // diagnosed from a log of the one run where it happens. A non-zero `was=`
        // on a simulated item is the signature of the original bug.
        BepinPlugin.Log.LogDebug(
            $"[Forge] undock {go.name}: simulated={simulated}, was={before}, zeroed both bodies.");
    }
}
