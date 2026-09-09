using CG.Ship.Modules;
using CG.Space;
using Gameplay.Damage;
using Photon.Pun;
using ToolClasses;
using UnityEngine;

namespace VoidCrewTerminus.Leech;

internal enum LeechVariant
{
    // Anchors near a module: non-stacking effectiveness debuff plus stacking HP damage.
    ModuleBiter,

    // Anchors on bare hull and chews ship HP on a visible bite cadence.
    HullBiter,
}

// One attached parasite. Damage is host-authoritative; every client runs the
// MonoBehaviour so the visual and the debuff exist everywhere, but only the master
// mutates hit points.
internal sealed class LeechController : MonoBehaviour
{
    internal LeechVariant Variant { get; private set; } = LeechVariant.ModuleBiter;
    internal CellModule TargetModule { get; private set; }

    private PlayerControlledShip _ship;
    private float _damageFraction;
    private float _tickInterval;
    private float _chompMin;
    private float _chompMax;
    private float _nextTickAt;
    private bool _attached;

    internal void AttachToModule(CellModule module, float debuffMagnitude, float damageFraction, float tickInterval)
    {
        Variant = LeechVariant.ModuleBiter;
        TargetModule = module;
        _damageFraction = damageFraction;
        _tickInterval = Mathf.Max(0.5f, tickInterval);
        _nextTickAt = Time.time + _tickInterval;

        LeechModuleDebuffApplicator.Instance.Attach(module, debuffMagnitude);
        _attached = true;

        BepinPlugin.Log.LogDebug($"[Leech] Module-Biter attached to {module.name}.");
    }

    internal void AttachToHull(PlayerControlledShip ship, float damageFraction, float chompMin, float chompMax)
    {
        Variant = LeechVariant.HullBiter;
        _ship = ship;
        _damageFraction = damageFraction;
        _chompMin = Mathf.Max(0.5f, chompMin);
        _chompMax = Mathf.Max(_chompMin, chompMax);
        _nextTickAt = Time.time + RandomChompDelay();
        _attached = true;

        BepinPlugin.Log.LogDebug("[Leech] Hull-Biter attached to bare hull.");
    }

    private void Update()
    {
        if (!_attached) return;

        if (Variant == LeechVariant.ModuleBiter && (TargetModule == null || TargetModule.HitPoints <= 0f))
        {
            BepinPlugin.Log.LogDebug("[Leech] target module lost — self-destructing.");
            Remove();
            return;
        }

        if (Variant == LeechVariant.HullBiter && _ship == null)
        {
            Remove();
            return;
        }

        if (!PhotonNetwork.IsMasterClient || Time.time < _nextTickAt) return;

        if (Variant == LeechVariant.ModuleBiter)
        {
            _nextTickAt = Time.time + _tickInterval;
            BiteModule();
        }
        else
        {
            // Ranged rather than fixed so several Hull-Biters don't bite in lockstep.
            // Deliberately not scalar-scaled — chomp damage already scales, and
            // compounding cadence on top inverts the intent.
            _nextTickAt = Time.time + RandomChompDelay();
            Chomp();
        }
    }

    private float RandomChompDelay() => Random.Range(_chompMin, _chompMax);

    // Module HP damage stacks by design — each Leech ticks its own bite, unlike the
    // effectiveness debuff which the applicator ref-counts to a single application.
    private void BiteModule()
    {
        float damage = TargetModule.MaxHitPointsValue * _damageFraction;
        if (damage <= 0f) return;

        TargetModule.HitPoints = Mathf.Max(0f, TargetModule.HitPoints - damage);

        BepinPlugin.Log.LogDebug(
            $"[Leech] bit {TargetModule.name} for {damage:0.#} — {TargetModule.HitPoints:0}/{TargetModule.MaxHitPointsValue:0} left.");
    }

    // Routes through the ship's ordinary damage path so resistances apply and
    // breaches accrue by vanilla's own accounting. A Leech must never promote a
    // breach itself: repairing one restores 10-20% of max HP, so a breach-creating
    // Leech would net-heal the ship.
    private void Chomp()
    {
        float max = _ship.MaxHitPointsValue;
        float floor = max * TerminusConfig.LeechHullFloor;
        float damage = max * _damageFraction;

        // The Hull Floor: Leeches strip survival margin but never land the killing
        // blow. Ordinary combat damage still kills freely.
        float allowed = Mathf.Max(0f, _ship.HitPoints - floor);
        if (allowed <= 0f)
        {
            BepinPlugin.Log.LogDebug($"[Leech] chomp withheld — ship at the hull floor ({floor:0}).");
            return;
        }

        damage = Mathf.Min(damage, allowed);

        _ship.ApplyHitDamage(
            damage,
            null,
            DataTable<DamageTypesTable>.Instance.defaultDamageType,
            transform.position,
            transform.rotation);

        BepinPlugin.Log.LogDebug(
            $"[Leech] chomped hull for {damage:0.#} — {_ship.HitPoints:0}/{max:0}, floor {floor:0}.");
    }

    // Removal, self-destruct and teardown all land here so the ref count can never
    // be left holding a debuff for a leech that no longer exists.
    internal void Remove()
    {
        Detach();
        LeechEncounterController.Forget(this);

        if (this != null) Destroy(gameObject);
    }

    private void OnDestroy() => Detach();

    private void Detach()
    {
        if (!_attached) return;
        _attached = false;

        if (Variant == LeechVariant.ModuleBiter)
            LeechModuleDebuffApplicator.Instance.Detach(TargetModule);
    }
}
