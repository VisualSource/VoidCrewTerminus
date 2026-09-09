using System;
using System.Reflection;
using VoidCrewTerminus.Leech;
using Xunit;

namespace VoidCrewTerminus.Tests;

// The dynamics subclasses have one failure mode that compiles cleanly and only
// shows up in game: the base Description() indexes a localization DataTable by
// concrete type, and a mod-defined type is never in that asset, so the inherited
// implementation throws KeyNotFoundException down the live tooltip path.
//
// Nothing forces the override — drop it and the build stays green.
//
// Shape of these tests is dictated by two test-host limits. The types are reached
// by name because their base classes live in an assembly the test project takes as
// a runtime-only dependency, so naming them in source will not compile. And they
// are never instantiated because the GameLibs base constructors are reference stubs
// that throw — behaviour has to be verified in game, so what is pinned here is the
// contract with the base class.
[Collection(SharedStaticStateCollection.Name)]
public sealed class LeechDynamicsTests
{
    private static Type Dynamic(string name)
    {
        Type type = typeof(LeechEncounterController).Assembly
            .GetType($"VoidCrewTerminus.Leech.Dynamics.{name}");

        Assert.True(type != null, $"{name} not found");
        return type;
    }

    [Theory]
    [InlineData("LeechAttachedCountValue")]
    [InlineData("LeechAttachedCountCondition")]
    public void Description_is_overridden_rather_than_inherited_from_the_throwing_base(string name)
    {
        Type type = Dynamic(name);

        MethodInfo description = type.GetMethod(
            "Description", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);

        Assert.Equal(type, description.DeclaringType);
    }

    // RecalculateValue and ValidateType are sealed in the generic base. The design
    // doc lists them as members to implement, which would not compile — assert they
    // stay inherited so nobody "restores" them from the stale doc.
    [Fact]
    public void Value_does_not_try_to_reimplement_the_sealed_members()
    {
        Type type = Dynamic("LeechAttachedCountValue");

        foreach (string sealedMember in new[] { "RecalculateValue", "ValidateType" })
        {
            MethodInfo declared = type.GetMethod(sealedMember,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            Assert.True(declared == null, $"{sealedMember} is sealed in the base and must not be redeclared");
        }
    }
}
