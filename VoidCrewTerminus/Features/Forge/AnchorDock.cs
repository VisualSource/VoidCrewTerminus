using System.Collections.Generic;
using CG.Objects;
using UnityEngine;

namespace VoidCrewTerminus.Forge;

// The items physically held on an Upgrade Forge's anchors.
//
// Inserted relics and the loaded BuildBox are never absorbed into an inventory —
// they stay live in the world, frozen onto their anchor, pinned there while the
// ship moves, and still grabbable. This class owns the hazardous half of that:
// two rigidbodies per item, a per-frame teleport against a moving platform, and
// the velocity that silently accumulates from it.
//
// Not tested, and not testable: every line is a Unity physics call, and a method
// body containing one cannot even be JIT-compiled in the test host. It knows
// nothing about relics, module sockets or the network — it reports which items
// have left; the Forge decides what that means and who to tell.

// Mirrors CG.Ship.Hull.CarryablesSocket.AnchorType: which of the carryable's
// authored pivots gets pinned to the anchor's origin. Base is right for a
// relic resting in a tube; Center is right for the module socket, whose
// generated trigger volume is centered on the anchor — Base there pins e.g.
// the box's top to the volume's center, leaving the box's body hanging off
// to one side instead of sitting centered in the socket.
internal enum AnchorAlign
{
    Base,
    Center,
}

internal sealed class AnchorDock
{
    private sealed class Docked
    {
        internal readonly Transform Anchor;
        internal readonly AnchorAlign Align;
        // The rotation we actually wrote the last time we placed this item —
        // NOT recomputable from the item's current transform (pivot-alignment
        // math is invariant to the item's current orientation by construction,
        // so recomputing "expected" from current state is a tautology that can
        // never detect drift). Snapshotting the literal value we wrote is the
        // only way to tell whether something else touched it since.
        internal Quaternion LastAppliedRotation;
        internal Docked(Transform anchor, AnchorAlign align) { Anchor = anchor; Align = align; }
    }

    private readonly Dictionary<GameObject, Docked> _docked = new();

    // Items docked here whose Carrier has been directly observed null at least
    // once since docking — see Dock() and Reconcile() for why this exists.
    private readonly HashSet<GameObject> _confirmedReleased = new();

    // Reused across frames: Reconcile runs every Update and must not allocate.
    private readonly List<KeyValuePair<GameObject, Docked>> _departedScratch = new();

    internal bool IsDocked(GameObject item) => item != null && _docked.ContainsKey(item);

    // Read by ForgeInteractable so it can step aside and let a docked item be grabbed.
    internal bool IsOccupied(Transform anchor)
    {
        if (anchor == null) return false;
        foreach (var kv in _docked)
            if (kv.Value.Anchor == anchor) return true;
        return false;
    }

    internal void Dock(GameObject item, Transform anchor, AnchorAlign align = AnchorAlign.Base)
    {
        if (item == null || anchor == null) return;
        var docked = new Docked(anchor, align);
        _docked[item] = docked;
        _driftWarned.Remove(item);

        var co = item.GetComponent<CarryableObject>();

        // A dock mirrored from another client (ApplyRemoteDock in
        // UpgradeForgeBehavior) can race the placer's own carry-release: our
        // custom dock message is a separate RPC from Photon's carry/ownership
        // sync, so it can arrive here before Carrier has actually cleared on this
        // machine. Reconcile must not mistake that stale non-null read for "a
        // player just grabbed this back out" — so an item only becomes eligible
        // to be reported as departed once we've actually observed Carrier go
        // null while docked. A normal local dock (Carrier already null here,
        // since ReleaseCarryable already ran synchronously) is confirmed immediately.
        if (co == null || co.Carrier == null)
            _confirmedReleased.Add(item);
        else
            _confirmedReleased.Remove(item);

        // Freeze the simulation body too, not just the main one — otherwise it
        // keeps integrating in the platform's physics scene the whole time the
        // item is docked, which is what the per-frame pin was accumulating into.
        SetDockedKinematic(co, item, true);
        docked.LastAppliedRotation = PlaceAtAnchor(item, co, anchor, align);
        ForgeAnchors.SetFilled(anchor, true);

        // Diagnostic for the "relic doesn't rotate to match its tube" report:
        // PlaceAtAnchor is the same code path for relics and the module box, so
        // if the box aligns correctly and a relic doesn't, either this relic's
        // CarryableObject.BasePivot isn't the plain identity fallback we expect,
        // or something else is overwriting rotation after this runs. First round
        // (pivotIsSelf/pivotRot/appliedRot) proved the write itself lands
        // correctly for both — so this round also captures PhotonView ownership:
        // CarryableObject.OnPhotonSerializeView's reading branch re-applies
        // LocalPosition/LocalRotation from the network stream every sync tick
        // unless Carrier != null && Carrier.AmOwner, and Carrier is null the
        // instant we dock — so a docked item this client doesn't own would get
        // silently snapped back toward whatever the actual owner last reported,
        // undoing PlaceAtAnchor a moment after this log line. See also the
        // Pin() drift check below, which catches that happening frame-to-frame.
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

    // Logged once per item, the first time Pin() finds its rotation has moved
    // since the previous frame's placement — see the drift check below.
    private readonly HashSet<GameObject> _driftWarned = new();

    // Driven from the Forge's LateUpdate.
    internal void Pin()
    {
        if (_docked.Count == 0) return;
        foreach (var kv in _docked)
        {
            if (kv.Key == null || kv.Value.Anchor == null) continue;
            var co = kv.Key.GetComponent<CarryableObject>();
            // Skip once a player has grabbed the item — Reconcile undocks it next
            // frame. Otherwise the pin snaps a carried box back to the anchor and
            // the accumulated teleport delta drifts visibly when it wakes.
            if (co != null && co.Carrier != null) continue;

            var docked = kv.Value;

            // Diagnostic: compares against the literal rotation we wrote last
            // frame (LastAppliedRotation), not a recomputed "expected" value —
            // pivot-alignment math is invariant to the item's current rotation
            // by construction, so recomputing from current state would always
            // trivially match and could never catch anything. If something
            // outside AnchorDock (network sync, physics) is fighting our
            // placement between frames, this catches the item having moved
            // since Pin()/Dock() set it, right before we stomp it back to
            // correct. Logged once per item so a real, sustained fight (e.g.
            // CarryableObject.OnPhotonSerializeView reapplying a stale
            // networked LocalRotation every tick because this client isn't the
            // PhotonView owner) shows up without spamming every frame.
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

    // Items leave in exactly two ways: destroyed (a commit consumed the relic) —
    // reaped silently, nothing to release or announce — or grabbed back out
    // through the vanilla Grabbable flow, appended to `grabbed` still alive and
    // still owed an explanation to the rest of the crew.
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
                // Now observed clear while docked, so a later non-null read is
                // trustworthy evidence of an actual grab, not the mirrored-dock
                // race described in Dock().
                _confirmedReleased.Add(go);
                continue;
            }

            // If not yet confirmed-released, Carrier is still reading stale
            // non-null from before the mirrored dock's release caught up —
            // wait rather than bounce it.
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

    // Restores physics on everything at once, so nothing is left frozen mid-air
    // when the Forge that owned it goes away (hot-reload teardown).
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

    // Pivot alignment: the item's Base or Center pivot (per align) lands on the
    // anchor's origin with its axes matching the anchor's axes. Same intent as
    // CarryablesSocket.PlaceCarryableOnSocket, but computed with quaternions
    // instead of the anchor's matrices — our anchors inherit rotated,
    // non-uniformly scaled FBX nodes whose matrices would skew an extracted
    // rotation, unlike vanilla's unit-scale store transforms.
    private static Quaternion PlaceAtAnchor(GameObject item, CarryableObject co, Transform anchor, AnchorAlign align)
    {
        var itemTr = item.transform;
        var pivot = co == null ? itemTr : align == AnchorAlign.Center ? co.CenterPivot : co.BasePivot;
        var finalRot = anchor.rotation * Quaternion.Inverse(pivot.rotation) * itemTr.rotation;
        var delta = finalRot * Quaternion.Inverse(itemTr.rotation);
        var finalPos = anchor.position - delta * (pivot.position - itemTr.position);
        itemTr.SetPositionAndRotation(finalPos, finalRot);
        return finalRot;
    }

    // CarryableObject is an ISimulatedBody: while it rides a MovingSpacePlatform
    // (the ship) its real physics lives on SimulationRigidbody, NOT MainRigidbody
    // — Rigidbody and MainRigidbody return the same field, so the simulation body
    // has to be addressed explicitly. Both are frozen unconditionally rather than
    // branching on IsBeingSimulated, since that flag can flip while an item is
    // docked and a body left un-frozen would integrate in the background.
    private static void SetDockedKinematic(CarryableObject co, GameObject go, bool kinematic)
    {
        var main = co != null ? co.MainRigidbody : go.GetComponent<Rigidbody>();
        if (main != null) main.isKinematic = kinematic;

        var sim = co != null ? co.SimulationRigidbody : null;
        if (sim != null) sim.isKinematic = kinematic;
    }

    // The per-frame Pin against a moving-ship anchor accumulates an implicit
    // velocity estimate; without zeroing it the item inherits roughly the ship's
    // velocity the instant kinematic goes false and drifts through the ship — the
    // "BuildBox floats away" bug. Writing straight to MainRigidbody is the WRONG
    // body whenever the item is being simulated (velocity survives on
    // SimulationRigidbody instead, so the box still drifts — this is why the bug
    // only reproduced intermittently). Going through the Velocity/AngularVelocity
    // properties routes to whichever body is live; zero is safe in either space.
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
