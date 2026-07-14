using System.Collections.Generic;
using System.Linq;
using CG.Ship.Modules;
using Gameplay.Tags;
using Gameplay.Utilities;
using HarmonyLib;

namespace VoidCrewTerminus.Forge;

// Per-module overlay state. Implements IModifierSource so its stat mods can be
// batch-removed via module.Stats.RemoveModifier(this) without tracking individual mods.
public class ForgeModuleState : IModifierSource
{
    public int Level { get; private set; } = 3;

    // Perk slots: [0] any tier, [1] Rare+, [2] Legendary (see PerkPool). Values are
    // PerkDefinition ids; null = empty.
    private readonly string[] _perkSlots = new string[PerkPool.SlotCount];

    // Maintenance Burdens (Phase 7-C). Set of BurdenType — multiple distinct
    // types can accumulate on one module, but never duplicates.
    private readonly List<BurdenType> _burdens = new();

    // ForgeStateStore keeps a strong reference; this field is only for mod removal.
    private CellModule _module;

    public IReadOnlyList<string> PerkSlots => _perkSlots;
    public IReadOnlyList<BurdenType> Burdens => _burdens;

    public void Attach(CellModule module)
    {
        _module = module;
        if (HasAnyOverlay) ApplyMods();
        SyncBurdenBehaviors();
    }

    private bool HasAnyOverlay =>
        Level > 3 || _perkSlots.Any(id => !string.IsNullOrEmpty(id)) || _burdens.Count > 0;

    public void SetLevel(int level)
    {
        Level = System.Math.Max(3, System.Math.Min(10, level));
        RefreshMods();
    }

    public void SetPerk(int slot, string perkId)
    {
        if (slot < 0 || slot >= _perkSlots.Length) return;
        _perkSlots[slot] = perkId;
        RefreshMods();
    }

    // Produce an opaque snapshot for the deconstruct→reconstruct bridge. Any
    // change to the snapshot shape forces this method (and ApplySnapshot below)
    // to be updated in lock-step — the compiler is the forcing function.
    public ForgeSnapshot Snapshot() => ForgeSnapshot.Create(Level, _perkSlots, _burdens);

    // Restore full overlay in one call. Replaces the old SetLevel + SetPerks pair
    // and RefreshMods() once at the end.
    public void ApplySnapshot(ForgeSnapshot snapshot)
    {
        if (snapshot == null) return;
        Level = System.Math.Max(3, System.Math.Min(10, snapshot.Level));
        for (int i = 0; i < _perkSlots.Length; i++)
            _perkSlots[i] = i < snapshot.PerkSlots.Count ? snapshot.PerkSlots[i] : null;
        _burdens.Clear();
        for (int i = 0; i < snapshot.Burdens.Count; i++)
            if (snapshot.Burdens[i] != BurdenType.None && !_burdens.Contains(snapshot.Burdens[i]))
                _burdens.Add(snapshot.Burdens[i]);
        RefreshMods();
        SyncBurdenBehaviors();
    }

    // Add a burden to the module (Phase 7-C). Idempotent — no-op if the type
    // is already present. Reapplies stat mods so the tag marker for the new
    // burden is stamped.
    public void AddBurden(BurdenType burden)
    {
        if (burden == BurdenType.None) return;
        if (_burdens.Contains(burden)) return;
        _burdens.Add(burden);
        RefreshMods();
        SyncBurdenBehaviors();
    }

    // Remove all applied mods and detach from the module.
    public void Cleanup()
    {
        if (_module != null)
        {
            _module.Stats.RemoveModifier(this);
            SyncForgeTag(false);
            DetachBurdenBehaviors();
        }
        _module = null;
    }

    private void RefreshMods()
    {
        if (_module == null) return;
        _module.Stats.RemoveModifier(this);
        SyncForgeTag(false);
        SyncBurdenTags(false);
        if (HasAnyOverlay) ApplyMods();
    }

    private void SyncBurdenTags(bool present)
    {
        if (_module == null) return;
        var tags = RuntimeTagsRef(_module.Stats);
        if (tags == null) return;
        foreach (var burden in _burdens)
        {
            var tag = Utils.CsTagRegistry.BurdenTagFor(burden);
            if (tag == null) continue;
            if (present)
            {
                if (!tags.Contains(tag)) tags.Add(tag);
            }
            else
            {
                tags.RemoveAll(t => t == tag);
            }
        }
    }

    // Attach one MonoBehaviour per burden type on the module GameObject.
    // Idempotent — checking for an existing component before adding. Removes
    // components for burden types no longer in the set (defensive; today's
    // "no removal" invariant makes this a no-op in practice).
    private void SyncBurdenBehaviors()
    {
        if (_module == null) return;
        var go = _module.gameObject;

        // Attach missing.
        foreach (var burden in _burdens)
        {
            switch (burden)
            {
                case BurdenType.RandomShutoff:
                    if (go.GetComponent<Burdens.RandomShutoffBehavior>() == null)
                        go.AddComponent<Burdens.RandomShutoffBehavior>();
                    break;
            }
        }

        // Detach stragglers not in the set.
        foreach (var existing in go.GetComponents<Burdens.MaintenanceBurdenBehavior>())
        {
            if (!_burdens.Contains(existing.BurdenType))
                UnityEngine.Object.Destroy(existing);
        }
    }

    private void DetachBurdenBehaviors()
    {
        if (_module == null) return;
        foreach (var existing in _module.gameObject.GetComponents<Burdens.MaintenanceBurdenBehavior>())
            UnityEngine.Object.Destroy(existing);
    }

    private void ApplyMods()
    {
        var mods = BuildMods();
        if (mods.Count > 0)
        {
            _module.Stats.ApplyModifiers(mods, this);
            SyncForgeTag(true);
            SyncBurdenTags(true);
        }
    }

    private static readonly AccessTools.FieldRef<StatTagCollection, List<CsTag>> RuntimeTagsRef =
        AccessTools.FieldRefAccess<StatTagCollection, List<CsTag>>("runtimeTags");

    // The game only rebuilds a collection's runtimeTags inside UpdateMods, which
    // runs solely when some mod transitions active↔inactive — a plain
    // ApplyModifiers / RemoveModifier never triggers it, so TagsToAdd on its own
    // never surfaces in LocalTags(). Mirror the Forge tag into runtimeTags
    // directly; the zero-value marker mod (see BuildMods) keeps the tag alive if
    // the game does rebuild the list from active modifiers later.
    private void SyncForgeTag(bool present)
    {
        if (_module == null) return;
        var tags = RuntimeTagsRef(_module.Stats);
        if (tags == null) return;
        var forgeTag = Utils.CsTagRegistry.ForgeUpgraded;
        if (present)
        {
            if (!tags.Contains(forgeTag)) tags.Add(forgeTag);
        }
        else
        {
            tags.RemoveAll(t => t == forgeTag);
        }
    }

    // Build AdditiveMultiplier boosts for each bonus level above vanilla L3.
    // Each group is narrowed by module category via ModTagConfiguration.RequiredTags so
    // mods only activate on modules that carry the matching category CsTag.
    // TagsToAdd = [Forge_Upgraded] stamps upgraded modules so later perk phases can
    // gate on RequiredLocalTags = [Forge_Upgraded].
    private List<StatMod> BuildMods()
    {
        float amount = (Level - 3) * 0.08f;
        var mods = new List<StatMod>();

        // Level groups only contribute above vanilla L3 — a module can still carry
        // perks at L3 (restored state), in which case only the perk mods apply.
        if (Level > 3)
        {
            AddGroup(mods, amount, Utils.CsTagRegistry.Weapon, new[]
            {
                StatType.Damage, StatType.FireRate, StatType.Range,
                StatType.ProjectileSpeed, StatType.Accuracy,
            });
            AddGroup(mods, amount, Utils.CsTagRegistry.Defense, new[]
            {
                StatType.ShieldMaxHitPoints, StatType.ShieldRechargeSpeed, StatType.ShieldAbsorption,
            });
            AddGroup(mods, amount, Utils.CsTagRegistry.BuiltIn, new[]
            {
                StatType.ForwardPower, StatType.EnginePower, StatType.YawTorque,
                StatType.ElevationPower, StatType.StrafePower, StatType.JumpChargeSpeed,
            });
            AddGroup(mods, amount, Utils.CsTagRegistry.PowerProvider, new[]
            {
                StatType.PowerProvided, StatType.BatteryRechargeAmount,
            });
            AddGroup(mods, amount, Utils.CsTagRegistry.Utility, new[]
            {
                StatType.ProcessingSpeed, StatType.HealingSpeed,
                StatType.AttractorMaxRange, StatType.AttractorPullVelocity,
            });
        }

        // Rolled perks: fresh StatMods per apply (mods bind their source), narrowed
        // to the perk's own category tag as a safety net against wrong-category
        // application.
        foreach (var perkId in _perkSlots)
        {
            if (string.IsNullOrEmpty(perkId) || !PerkPool.TryGet(perkId, out var perk)) continue;
            var categoryTag = perk.Category.ToCsTag();
            var tagCfg = new ModTagConfiguration
            {
                RequiredTags = categoryTag != null ? new[] { categoryTag } : null,
                TagsToAdd = new[] { Utils.CsTagRegistry.ForgeUpgraded },
            };
            foreach (var (stat, perkAmount) in perk.Payload)
                mods.Add(new StatMod(new FloatModifier(perkAmount, ModifierType.AdditiveMultiplier, this), stat.Id, tagCfg));
        }

        // Module-level tag marker. TagsToAdd only lands in a collection's runtime
        // tags when a carrying mod attaches to a stat registered on that collection,
        // and the category groups above attach to stats living on child collections
        // (weapon parts etc.) — so without this, the module's own LocalTags never
        // gains Forge_Upgraded (breaking !dumptags and RequiredLocalTags gating).
        // A zero-value addend on MaxHitPoints — registered by every OrbitObject —
        // carries the tag onto the module collection itself without touching stats.
        mods.Add(new StatMod(
            new FloatModifier(0f, ModifierType.PrimaryAddend, this),
            StatType.MaxHitPoints.Id,
            new ModTagConfiguration { TagsToAdd = new[] { Utils.CsTagRegistry.ForgeUpgraded } }));

        // Burden tag markers (Phase 7-C). Same zero-value-addend pattern — one
        // marker per active burden so the module's LocalTags carry
        // Burden_RandomShutoff etc. for game/mod-side queries.
        foreach (var burden in _burdens)
        {
            var burdenTag = Utils.CsTagRegistry.BurdenTagFor(burden);
            if (burdenTag == null) continue;
            mods.Add(new StatMod(
                new FloatModifier(0f, ModifierType.PrimaryAddend, this),
                StatType.MaxHitPoints.Id,
                new ModTagConfiguration { TagsToAdd = new[] { burdenTag } }));
        }

        return mods;
    }

    private void AddGroup(List<StatMod> mods, float amount, CsTag categoryTag, StatType[] statTypes)
    {
        if (categoryTag == null) return;

        var tagCfg = new ModTagConfiguration
        {
            RequiredTags = new[] { categoryTag },
            TagsToAdd = new[] { Utils.CsTagRegistry.ForgeUpgraded },
        };

        foreach (var statType in statTypes)
            mods.Add(new StatMod(new FloatModifier(amount, ModifierType.AdditiveMultiplier, this), statType.Id, tagCfg));
    }
}
