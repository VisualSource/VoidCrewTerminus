using System;
using BufferedEvents.Impacts;
using CG;
using CG.Space;
using CG.Space.Projectiles;
using Gameplay.Damage;
using Photon.Pun;
using UnityEngine;

namespace VoidCrewTerminus.Leech;

// Derives from GuidedProjectile so vanilla keeps owning the whole lifecycle after
// launch: homing, the 1 Hz position/target correction, client dead reckoning,
// server-clock expiry, sector-exit cleanup and projectile-id repair on migration.
// What this adds is an interception counter and an impact that carries a payload
// instead of damage.
internal sealed class LeechMissileBehavior : GuidedProjectile, IDamageReceiver, IHitReceiver
{
    internal int HitPointsRemaining { get; private set; } = 1;

    // Phase 2 hangs leech deployment here. Host-side only.
    internal Action<LeechMissileBehavior, Vector3, Vector3> Deployed;

    private OrbitObject _assignedTarget;
    private bool _terminated;

    internal void Arm(OrbitObject target, int hitPoints)
    {
        _assignedTarget = target;
        HitPointsRemaining = Mathf.Max(1, hitPoints);

        // Vanilla destroys the projectile inside InformOfHit on the first hit.
        // Holding this false is what turns interception into a counter — every
        // destruction from here on is ours to call.
        IsDestroyedOnImpact = false;

        if (target != null) SetTarget(target);
    }

    // The Leech Carrier fires at one ship on purpose. Vanilla's nearest-viable
    // search is the fallback for when that target dies mid-flight.
    // public rather than protected because GameLibs ships publicized reference
    // assemblies — the decompile shows these as protected, the metadata does not.
    public override OrbitObject GetTarget()
        => _assignedTarget.Valid() ? _assignedTarget : base.GetTarget();

    // Re-implementation, not an override: SyncedProjectile declares InformOfHit
    // non-virtual, and every inbound site dispatches through IHitReceiver, so
    // re-listing the interfaces above is what remaps the slot to this method.
    public new void InformOfHit(
        IDamageReceiver target,
        float damage,
        OrbitObject source,
        ImpactSize impactSize,
        DamageType damageType,
        Vector3 point,
        Quaternion rotation,
        bool isMine = false)
    {
        base.InformOfHit(target, damage, source, impactSize, damageType, point, rotation, isMine);

        // Point-defense kill paths are master-only, but player gunfire reaches
        // this on every client — an ungated decrement would spend one HP per
        // player in the room for a single hit.
        if (!PhotonNetwork.IsMasterClient || _terminated) return;

        HitPointsRemaining--;
        BepinPlugin.Log.LogDebug(
            $"[Leech] missile {ProjectileId} took a hit, {HitPointsRemaining} HP remaining.");

        if (HitPointsRemaining <= 0) Terminate();
    }

    // Deliberately does not call base: the design forbids the missile damaging
    // hull HP on impact, and base.OnImpact applies Damage to the struck object.
    // The leeches are the payload.
    public override bool OnImpact(IHitReceiver target, Vector3 impactPoint, Vector3 normal, float damage)
    {
        if (!ShouldDamageOrbitObject(target, out OrbitObject hit)) return false;

        OnDamagableHit?.Invoke(hit);
        Deploy(impactPoint, normal);
        return true;
    }

    public override void ImpactEnvironment(Vector3 impactPoint, Vector3 normal)
        => Deploy(impactPoint, normal);

    private void Deploy(Vector3 point, Vector3 normal)
    {
        if (_terminated) return;

        ExplodeEvent(point, Quaternion.LookRotation(normal));

        if (PhotonNetwork.IsMasterClient) Deployed?.Invoke(this, point, normal);

        Terminate();
    }

    // DestroyProjectile tells every client, so calling it twice would emit a
    // second removal for an id the synchronizer has already dropped.
    private void Terminate()
    {
        if (_terminated) return;
        _terminated = true;
        DestroyProjectile();
    }
}
