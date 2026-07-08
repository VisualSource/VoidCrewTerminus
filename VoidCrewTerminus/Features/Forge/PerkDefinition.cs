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
        ForgeCategory.Weapon => CsTagRegistry.Weapon,
        ForgeCategory.Defense => CsTagRegistry.Defense,
        ForgeCategory.PowerProvider => CsTagRegistry.PowerProvider,
        ForgeCategory.BuiltIn => CsTagRegistry.BuiltIn,
        ForgeCategory.Utility => CsTagRegistry.Utility,
        _ => null,
    };
}

// A single authored perk. Payload is stored as (stat, additive-multiplier) pairs
// rather than live StatMod instances — StatMods bind their IModifierSource at
// apply time, so ForgeModuleState builds fresh ones per module (see BuildMods).
public sealed class PerkDefinition
{
    public string Id { get; }
    public string Name { get; }
    public ForgeCategory Category { get; }
    public string Description { get; }
    public IReadOnlyList<(StatType Stat, float Amount)> Payload { get; }

    public PerkDefinition(string id, string name, ForgeCategory category, string description,
        params (StatType Stat, float Amount)[] payload)
    {
        Id = id;
        Name = name;
        Category = category;
        Description = description;
        Payload = payload;
    }

    public override string ToString() => $"{Name} [{Id}]";
}
