using System;
using CG.Ship.Object;
using Gameplay.Tags;
using ResourceAssets;

namespace VoidCrewTerminus.ModuleKit;

// Nothing else in ModuleKit/ knows what a Forge is.
public sealed class CustomModuleDefinition
{
    // Both bundle prefabs are bare VoidCrewAsset-marked objects with no surviving script
    // components, so the name is the only signal available to tell them apart at load time.
    public string ModulePrefabName { get; set; }

    // Marker only — its guid and flavor text are kept and the GameObject discarded. The
    // real crate is cloned from a live vanilla donor.
    public string BuildBoxPrefabName { get; set; }

    public string BuildBoxDisplayName { get; set; }
    public ECategory Category { get; set; } = ECategory.Support;
    public RarityType Rarity { get; set; } = RarityType.Common;

    // A Func, not a CsTag: tags resolve against the game's CsTagTable, which doesn't exist
    // yet when definitions are constructed at plugin Awake. Donor selection prefers a crate
    // whose module carries this tag so the borrowed crate's category label reads right;
    // null takes any non-weapon BuildBox.
    public Func<CsTag> PreferredDonorTag { get; set; }
}
