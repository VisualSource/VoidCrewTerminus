using System.Collections.Generic;
using Gameplay.Tags;
using Gameplay.Utilities;

namespace VoidCrewTerminus.Forge;

// Mapped 1:1 to the built-in Module_Category_* CsTags resolved by CsTagRegistry.
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

// Payload is (stat, additive-multiplier) pairs rather than live StatMod instances, because
// StatMods bind their IModifierSource at apply time and ForgeModuleState builds fresh ones
// per module. SignatureRelicId is null for category-pool perks; non-null names the relic
// that must be consumed in the commit for this perk to roll.
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
