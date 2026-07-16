using System.Collections.Generic;
using Gameplay.Tags;
using Gameplay.Utilities;

namespace VoidCrewTerminus.Forge;

// Module categories the Forge recognises, mapped 1:1 to the built-in
// Module_Category_* CsTags resolved by CsTagRegistry.
public enum ForgeCategory
{
    Unknown = 0,
    Weapon,
    Defense,
    PowerProvider,
    BuiltIn,   // engines / thrusters / jump
    Utility,
}

public static class ForgeCategoryExtensions
{
    public static CsTag ToCsTag(this ForgeCategory category) => category switch
    {
        ForgeCategory.Weapon => Utils.CsTagRegistry.Weapon,
        ForgeCategory.Defense => Utils.CsTagRegistry.Defense,
        ForgeCategory.PowerProvider => Utils.CsTagRegistry.PowerProvider,
        ForgeCategory.BuiltIn => Utils.CsTagRegistry.BuiltIn,
        ForgeCategory.Utility => Utils.CsTagRegistry.Utility,
        _ => null,
    };
}

// A single authored perk. Payload is stored as (stat, additive-multiplier) pairs
// rather than live StatMod instances — StatMods bind their IModifierSource at
// apply time, so ForgeModuleState builds fresh ones per module (see BuildMods).
//
// SignatureRelicId is null for normal category-pool perks; non-null identifies
// the specific relic that unlocks this signature. A signature perk only rolls
// when its owning relic is consumed in the commit (see PerkPool.SignaturesFor
// and UpgradeCommitCalculator.RollPerk).
public sealed class PerkDefinition
{
    public string Id { get; }
    public string Name { get; }
    public ForgeCategory Category { get; }
    public string Description { get; }
    public string SignatureRelicId { get; }
    public IReadOnlyList<(StatType Stat, float Amount)> Payload { get; }

    public bool IsSignature => !string.IsNullOrEmpty(SignatureRelicId);

    public PerkDefinition(string id, string name, ForgeCategory category, string description,
        params (StatType Stat, float Amount)[] payload)
        : this(id, name, category, description, signatureRelicId: null, payload) { }

    public PerkDefinition(string id, string name, ForgeCategory category, string description,
        string signatureRelicId, params (StatType Stat, float Amount)[] payload)
    {
        Id = id;
        Name = name;
        Category = category;
        Description = description;
        SignatureRelicId = signatureRelicId;
        Payload = payload;
    }

    public override string ToString() => $"{Name} [{Id}]";
}
