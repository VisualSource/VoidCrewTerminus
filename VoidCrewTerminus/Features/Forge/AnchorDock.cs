using System.Collections.Generic;
using CG.Objects;
using UnityEngine;

namespace VoidCrewTerminus.Forge;

// Which authored pivot is pinned to the anchor's origin (mirrors CarryablesSocket.AnchorType).
// The module socket's trigger volume is centered on its anchor; relic tubes rest on Base.
internal enum AnchorAlign
{
    Base,
    Center,
}

// Not testable: a method body containing a Unity call can't be JIT-compiled in the test host.

internal sealed class AnchorDock
{
    private sealed class Docked
    {
        internal readonly Transform Anchor;
        internal readonly AnchorAlign Align;
        // The literal rotation last written here. Pivot alignment is invariant to the item's
        // current orientation, so a recomputed "expected" value could never detect drift.
        internal Quaternion LastAppliedRotation;
        internal Docked(Transform anchor, AnchorAlign align) { Anchor = anchor; Align = align; }
    }

    private readonly Dictionary<GameObject, Docked> _docked = new();

    // Items whose Carrier has been observed null at least once since docking; see Dock().
    private readonly HashSet<GameObject> _confirmedReleased = new();

    // Reused across frames: Reconcile runs every Update and must not allocate.
    private readonly List<KeyValuePair<GameObject, Docked>> _departedScratch = new();

    internal int Count => _docked.Count;

    internal bool IsDocked(GameObject item) => item != null && _docked.ContainsKey(item);

    // Delegates so this and TryGetDockedAt can't disagree about a destroyed-but-unreaped item.
    internal bool IsOccupied(Transform anchor) => TryGetDockedAt(anchor, out _);

    internal bool TryGetDockedAt(Transform anchor, out GameObject item)
    {
        item = null;
        if (anchor == null) return false;
        foreach (var kv in _docked)
        {
            if (kv.Value.Anchor != anchor || kv.Key == null) continue;
            item = kv.Key;
            return true;
        }
        return false;
    }

    internal void Dock(GameObject item, Transform anchor, AnchorAlign align = AnchorAlign.Base)
    {
        if (item == null || anchor == null) return;
        var docked = new Docked(anchor, align);
        _docked[item] = docked;
        _driftWarned.Remove(item);

        var co = item.GetComponent<CarryableObject>();

        // A mirrored dock can arrive before Carrier clears (separate RPC from Photon's carry
        // sync), so departure is only reported once Carrier has been observed null while docked.
        if (co == null || co.Carrier == null)
            _confirmedReleased.Add(item);
        else
            _confirmedReleased.Remove(item);

        SetDockedKinematic(co, item, true);
        docked.LastAppliedRotation = PlaceAtAnchor(item, co, anchor, align);
        ForgeAnchors.SetFilled(anchor, true);

        var pivot = align == AnchorAlign.Center ? co?.CenterPivot : co?.BasePivot;
        var pv = item.GetComponent<Photon.Pun.PhotonView>();
        BepinPlugin.Log.LogDebug(
            $"[Forge] Dock {item.name} align={align}: pivotIsSelf={pivot == item.transform}, " +
            $"pivotRot={pivot?.rotation.eulerAngles}, anchorRot={anchor.rotation.eulerAngles}, " +
            $"appliedRot={item.transform.rotation.eulerAngles}, " +
            $"photonIsMine={pv?.IsMine}, photonOwner={pv?.Owner?.ActorNumber}");
    }

    internal bool Undock(GameObject item)
    {
        if (item == null || !_docked.TryGetValue(item, out var docked)) return false;
        _docked.Remove(item);
        _confirmedReleased.Remove(item);
        _driftWarned.Remove(item);
        ForgeAnchors.SetFilled(docked.Anchor, false);
        ReleaseRigidbody(item);
        return true;
    }

    private readonly HashSet<GameObject> _driftWarned = new();

    internal void Pin()
    {
        if (_docked.Count == 0) return;
        foreach (var kv in _docked)
        {
            if (kv.Key == null || kv.Value.Anchor == null) continue;
            var co = kv.Key.GetComponent<CarryableObject>();
            // Reconcile undocks it next frame; pinning a carried box snaps it back to the
            // anchor and the accumulated teleport delta drifts visibly when it wakes.
            if (co != null && co.Carrier != null) continue;

            var docked = kv.Value;

            if (!_driftWarned.Contains(kv.Key))
            {
                float drift = Quaternion.Angle(kv.Key.transform.rotation, docked.LastAppliedRotation);
                if (drift > 2f)
                {
                    _driftWarned.Add(kv.Key);
                    BepinPlugin.Log.LogDebug(
                        $"[Forge] Pin {kv.Key.name}: rotation drifted {drift:F1}° since last placement — something else is overwriting it.");
                }
            }

            docked.LastAppliedRotation = PlaceAtAnchor(kv.Key, co, docked.Anchor, docked.Align);
        }
    }

    // Items leave in two ways: destroyed (reaped silently), or grabbed back out. Only the
    // latter reaches `grabbed`; those are still alive and still owe the crew an announcement.
    internal void Reconcile(List<KeyValuePair<GameObject, Transform>> grabbed)
    {
        if (_docked.Count == 0) return;

        foreach (var kv in _docked)
        {
            var go = kv.Key;
            if (go == null) { _departedScratch.Add(kv); continue; }

            var co = go.GetComponent<CarryableObject>();
            bool carried = co != null && co.Carrier != null;

            if (!carried)
            {
                // Observed clear while docked, so a later non-null read is a real grab,
                // not the Dock() race.
                _confirmedReleased.Add(go);
                continue;
            }

            // Not yet confirmed released: Carrier is still stale from the mirrored dock.
            if (_confirmedReleased.Contains(go))
                _departedScratch.Add(kv);
        }

        foreach (var kv in _departedScratch)
        {
            _docked.Remove(kv.Key);
            _confirmedReleased.Remove(kv.Key);
            ForgeAnchors.SetFilled(kv.Value.Anchor, false);
            if (kv.Key == null) continue;
            ReleaseRigidbody(kv.Key);
            grabbed.Add(new KeyValuePair<GameObject, Transform>(kv.Key, kv.Value.Anchor));
        }
        _departedScratch.Clear();
    }

    // Hot-reload teardown: nothing may be left frozen mid-air when the Forge goes away.
    internal void ReleaseAll()
    {
        foreach (var kv in _docked)
        {
            if (kv.Key == null) continue;
            ForgeAnchors.SetFilled(kv.Value.Anchor, false);
            ReleaseRigidbody(kv.Key);
        }
        _docked.Clear();
        _confirmedReleased.Clear();
    }

    // Pose math lives in ForgeAnchors.ComputeDockedPose so ForgeGhosts' preview lands where
    // the item actually will. MUST write through CarryableObject.Position/Rotation, not the
    // raw Transform: MovingSpacePlatform re-drives an unclaimed item's visible transform each
    // tick from a proxy Rigidbody's local pose, so a raw write is stomped on the next tick.
    private static Quaternion PlaceAtAnchor(GameObject item, CarryableObject co, Transform anchor, AnchorAlign align)
    {
        var itemTr = item.transform;
        var pivot = co == null ? itemTr : align == AnchorAlign.Center ? co.CenterPivot : co.BasePivot;
        ForgeAnchors.ComputeDockedPose(itemTr, pivot, anchor, out var pos, out var rot);
        if (co != null)
        {
            co.Position = pos;
            co.Rotation = rot;
        }
        else
        {
            itemTr.SetPositionAndRotation(pos, rot);
        }
        return rot;
    }

    // Real physics lives on SimulationRigidbody while the item rides the ship; Rigidbody and
    // MainRigidbody return the same field, so the simulation body must be addressed explicitly.
    // Both are frozen unconditionally because IsBeingSimulated can flip while docked.
    private static void SetDockedKinematic(CarryableObject co, GameObject go, bool kinematic)
    {
        var main = co != null ? co.MainRigidbody : go.GetComponent<Rigidbody>();
        if (main != null) main.isKinematic = kinematic;

        var sim = co != null ? co.SimulationRigidbody : null;
        if (sim != null) sim.isKinematic = kinematic;
    }

    // A docked item is frozen kinematic; on undock it must return to its regime without
    // inheriting stale velocity.
    private static void ReleaseRigidbody(GameObject go)
    {
        if (go == null) return;
        var co = go.GetComponent<CarryableObject>();

        // Carrier.SetPayload already put it in the carried regime; un-kinematic it here and
        // it becomes a live body bumping into modules. Vanilla re-homes it on release.
        if (co != null && co.Carrier != null)
        {
            BepinPlugin.Log.LogDebug($"[Forge] undock {go.name}: carried — left to vanilla.");
            return;
        }

        bool simulated = co != null && co.IsBeingSimulated;
        Vector3 before = Vector3.zero;
        try { before = co != null ? co.Velocity : Vector3.zero; }
        catch { /* SimulationPlatform can be null mid-transition; not worth failing the undock */ }

        // MUST clear kinematic BEFORE assigning velocity: a write to a still-kinematic body
        // is silently dropped.
        SetDockedKinematic(co, go, false);

        // UpdateAtmosphereData periodically drops a docked item from the platform sim, leaving
        // it with ~0 world velocity so the moving ship leaves it behind. Re-drive the return.
        bool reattached = false;
        if (co != null && !co.IsBeingSimulated)
        {
            try
            {
                co.ReleaseFromCarrier();
                reattached = true;
            }
            catch (System.Exception e)
            {
                BepinPlugin.Log.LogWarning(
                    $"[Forge] undock {go.name}: re-attach via ReleaseFromCarrier failed ({e.GetType().Name}).");
            }
        }

        if (co != null)
        {
            try
            {
                // After any re-attach above: on a re-simulated item this routes to the proxy
                // in platform-local space, where 0 means "still moving with the ship".
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

        BepinPlugin.Log.LogDebug(
            $"[Forge] undock {go.name}: simulated={simulated}, was={before}, reattached={reattached}, zeroed both bodies.");
    }
}
