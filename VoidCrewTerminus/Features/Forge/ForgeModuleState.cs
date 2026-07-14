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
    // PerkDefinition ids; null = empty. Burden flags are added in Phase 7.
    private readonly string[] _perkSlots = new string[PerkPool.SlotCount];

    // ForgeStateStore keeps a strong reference; this field is only for mod removal.
    private CellModule _module;

    public IReadOnlyList<string> PerkSlots => _perkSlots;

    public void Attach(CellModule module)
    {
        _module = module;
        if (HasAnyOverlay) ApplyMods();
    }

    private bool HasAnyOverlay => Level > 3 || _perkSlots.Any(id => !string.IsNullOrEmpty(id));

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
    public ForgeSnapshot Snapshot() => ForgeSnapshot.Create(Level, _perkSlots);

    // Restore full overlay in one call. Replaces the old SetLevel + SetPerks pair
    // and RefreshMods() once at the end.
    public void ApplySnapshot(ForgeSnapshot snapshot)
    {
        if (snapshot == null) return;
        Level = System.Math.Max(3, System.Math.Min(10, snapshot.Level));
        for (int i = 0; i < _perkSlots.Length; i++)
            _perkSlots[i] = i < snapshot.PerkSlots.Count ? snapshot.PerkSlots[i] : null;
        RefreshMods();
    }

    // Remove all applied mods and detach from the module.
    public void Cleanup()
    {
        if (_module != null)
        {
            _module.Stats.RemoveModifier(this);
            SyncForgeTag(false);
        }
        _module = null;
    }

    private void RefreshMods()
    {
        if (_module == null) return;
        _module.Stats.RemoveModifier(this);
        SyncForgeTag(false);
        if (HasAnyOverlay) ApplyMods();
    }

    private void ApplyMods()
    {
        var mods = BuildMods();
        if (mods.Count > 0)
        {
            _module.Stats.ApplyModifiers(mods, this);
            SyncForgeTag(true);
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
