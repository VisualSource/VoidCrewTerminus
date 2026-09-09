using System;
using System.Linq;
using System.Reflection;
using VoidCrewTerminus.Leech;
using Xunit;

namespace VoidCrewTerminus.Tests;

// SyncedProjectile declares InformOfHit and ApplyHitDamage non-virtual, so
// LeechMissileBehavior cannot override them — it re-lists IDamageReceiver and
// IHitReceiver to re-map those interface slots onto its own members instead.
//
// That remap is invisible at a call site and silent if it breaks: drop the
// interfaces from the class declaration and everything still compiles, but every
// inbound hit dispatches to the base again, the missile dies to the first
// point-defense shot, and its hit points never decrement. These assert the map.
//
// Game types are looked up by name because GameLibs is a runtime-only dependency
// here — the test project copies the DLLs but does not compile against them.
public sealed class LeechMissileDispatchTests
{
    private static Type GameInterface(string name)
    {
        Type type = typeof(LeechMissileBehavior).GetInterfaces()
            .SingleOrDefault(i => i.Name == name);

        Assert.True(type != null, $"LeechMissileBehavior does not implement {name}");
        return type;
    }

    // InformOfHit is declared on IDamageReceiver; IHitReceiver carries only
    // ApplyHitDamage. Re-listing IHitReceiver alone would not remap the hit path.
    [Fact]
    public void InformOfHit_dispatches_to_the_leech_missile_not_the_base()
    {
        InterfaceMapping map = typeof(LeechMissileBehavior)
            .GetInterfaceMap(GameInterface("IDamageReceiver"));

        int index = Array.FindIndex(map.InterfaceMethods, m => m.Name == "InformOfHit");
        Assert.True(index >= 0, "IDamageReceiver declares no InformOfHit");

        Assert.Equal(typeof(LeechMissileBehavior), map.TargetMethods[index].DeclaringType);
    }

    [Fact]
    public void Missile_re_lists_both_receiver_interfaces()
    {
        // Re-listing is what triggers the remap. Removing either from the class
        // declaration is the regression this catches.
        Assert.NotNull(GameInterface("IHitReceiver"));
        Assert.NotNull(GameInterface("IDamageReceiver"));
    }

    [Fact]
    public void Impact_hooks_are_overridden_so_the_missile_owns_its_own_destruction()
    {
        // Arm() holds IsDestroyedOnImpact false, which disarms vanilla's
        // destroy-on-impact in both outbound paths. If these stop being
        // overridden the missile flies through the ship and never terminates.
        foreach (string hook in new[] { "OnImpact", "ImpactEnvironment" })
        {
            MethodInfo method = typeof(LeechMissileBehavior)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(m => m.Name == hook && m.DeclaringType == typeof(LeechMissileBehavior));

            Assert.True(method.IsVirtual, $"{hook} should be an override");
        }
    }
}
