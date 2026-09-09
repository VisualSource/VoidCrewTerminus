using CG.Ship.Modules;
using Photon.Pun;
using UnityEngine;

namespace VoidCrewTerminus.Leech;

internal enum LeechVariant
{
    // Anchors near a module: non-stacking effectiveness debuff plus stacking HP damage.
    ModuleBiter,

    // Anchors on bare hull and chews ship HP. Phase 3.
    HullBiter,
}

// One attached parasite. Damage ticks are host-authoritative; every client runs the
// same MonoBehaviour so the visual and the debuff exist everywhere, but only the
// master mutates hit points.
internal sealed class LeechController : MonoBehaviour
{
    internal LeechVariant Variant { get; private set; } = LeechVariant.ModuleBiter;
    internal CellModule TargetModule { get; private set; }

    private float _damagePercentPerTick;
    private float _tickInterval;
    private float _nextTickAt;
    private bool _attached;

    internal void AttachTo(CellModule module, float debuffMagnitude, float damagePercentPerTick, float tickInterval)
    {
        TargetModule = module;
        _damagePercentPerTick = damagePercentPerTick;
        _tickInterval = Mathf.Max(0.5f, tickInterval);
        _nextTickAt = Time.time + _tickInterval;

        LeechModuleDebuffApplicator.Instance.Attach(module, debuffMagnitude);
        _attached = true;

        BepinPlugin.Log.LogDebug($"[Leech] attached to {module.name}.");
    }

    private void Update()
    {
        if (!_attached) return;

        // The module going away is the design's self-destruct trigger: a Leech
        // whose target is destroyed dies with it rather than re-targeting.
        if (TargetModule == null || TargetModule.HitPoints <= 0f)
        {
            BepinPlugin.Log.LogDebug("[Leech] target module lost — self-destructing.");
            Remove();
            return;
        }

        if (!PhotonNetwork.IsMasterClient || Time.time < _nextTickAt) return;

        _nextTickAt = Time.time + _tickInterval;
        Bite();
    }

    // Module HP damage stacks by design — each Leech ticks its own bite, unlike the
    // effectiveness debuff which the applicator ref-counts to a single application.
    private void Bite()
    {
        float damage = TargetModule.MaxHitPointsValue * _damagePercentPerTick;
        if (damage <= 0f) return;

        TargetModule.HitPoints = Mathf.Max(0f, TargetModule.HitPoints - damage);

        BepinPlugin.Log.LogDebug(
            $"[Leech] bit {TargetModule.name} for {damage:0.#} — {TargetModule.HitPoints:0}/{TargetModule.MaxHitPointsValue:0} left.");
    }

    // Removal, self-destruct and teardown all land here so the ref count can never
    // be left holding a debuff for a leech that no longer exists.
    internal void Remove()
    {
        if (_attached)
        {
            LeechModuleDebuffApplicator.Instance.Detach(TargetModule);
            _attached = false;
        }

        LeechEncounterController.Forget(this);

        if (this != null) Destroy(gameObject);
    }

    private void OnDestroy()
    {
        if (!_attached) return;

        // Reached when the scene tears the object down without Remove() — detach so
        // the module doesn't keep a debuff from a dead leech.
        LeechModuleDebuffApplicator.Instance.Detach(TargetModule);
        _attached = false;
    }
}
