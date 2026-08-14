using Xunit;

namespace VoidCrewTerminus.Tests;

// The mod keeps run state in process-wide statics — ForgeMeterController,
// SectorEscalation, ForgeStateStore, ForgeNetSync.Transport — because a BepInEx
// plugin has exactly one game to be stateful about.
//
// xUnit runs test CLASSES in parallel by default, so any two classes that mutate
// the same static will race and fail intermittently. Every class that touches
// them shares this collection, which xUnit serialises.
//
// If a new test class writes to any of those statics, add the attribute to it too.
[CollectionDefinition(Name)]
public sealed class SharedStaticStateCollection
{
    internal const string Name = "shared-static-state";
}
