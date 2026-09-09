using System;
using System.Reflection;
using VoidCrewTerminus.Leech;
using Xunit;

namespace VoidCrewTerminus.Tests;

// The Module-Biter / Hull-Biter split is positional, not random — the player is
// meant to be able to angle the ship and earn Hull-Biters deliberately. A wrong
// box test does not throw, it just quietly makes one variant unreachable, so the
// geometry is pinned here.
//
// DistanceToBox takes loose floats rather than a Vector3 precisely so it can be
// reached from the test host, where Unity's Vector3 constructor throws.
public sealed class LeechVariantAssignerTests
{
    private const float Radius = 1.5f;

    // Authored grid contract: cells are 4.0 units, so Small is 4x4x4, Medium 8x4x4
    // and Large 8x4x8 — half-extents (2,2,2), (4,2,2), (4,2,4).
    private static (float X, float Y, float Z) Small => (2f, 2f, 2f);
    private static (float X, float Y, float Z) Large => (4f, 2f, 4f);

    [Fact]
    public void A_point_inside_the_footprint_is_zero_distance()
    {
        Assert.Equal(0f, LeechVariantAssigner.DistanceToBox(0f, 0f, 0f, Small.X, Small.Y, Small.Z));
        Assert.Equal(0f, LeechVariantAssigner.DistanceToBox(1.9f, 0f, 0f, Small.X, Small.Y, Small.Z));
    }

    [Fact]
    public void Distance_is_measured_from_the_face_not_the_centre()
    {
        // 3.0 out along X on a half-extent of 2.0 is 1.0 from the surface.
        float distance = LeechVariantAssigner.DistanceToBox(3f, 0f, 0f, Small.X, Small.Y, Small.Z);

        Assert.Equal(1f, distance, 4);
    }

    // This is the case that killed the centroid approach: centroid-to-end on a
    // Large module is 4.90-6.00, so a Leech sitting on its far face would have
    // classified as a Hull-Biter. Measured from the face it is plainly inside.
    [Fact]
    public void A_leech_on_a_large_modules_far_face_is_a_module_biter()
    {
        float distance = LeechVariantAssigner.DistanceToBox(3.9f, 0f, 3.9f, Large.X, Large.Y, Large.Z);

        Assert.Equal(0f, distance);
        Assert.True(distance <= Radius);
    }

    [Fact]
    public void Corner_distance_combines_all_three_axes()
    {
        // 1.0 past the box on X and Y both → sqrt(2), not 1.0.
        float distance = LeechVariantAssigner.DistanceToBox(3f, 3f, 0f, Small.X, Small.Y, Small.Z);

        Assert.Equal(1.41421f, distance, 4);
    }

    [Theory]
    [InlineData(3.4f, true)]   // 1.4 clear — inside the radius
    [InlineData(3.5f, true)]   // exactly 1.5 — boundary counts as near
    [InlineData(3.6f, false)]  // 1.6 clear — Hull-Biter
    public void The_proximity_radius_decides_the_variant(float offsetX, bool expectedModuleBiter)
    {
        float distance = LeechVariantAssigner.DistanceToBox(offsetX, 0f, 0f, Small.X, Small.Y, Small.Z);

        Assert.Equal(expectedModuleBiter, distance <= Radius);
    }

    // 2.0 is a structural ceiling, not a taste preference: at or above it the gap
    // between adjacent 4.0-unit cells is fully covered, no anchor can land outside
    // every footprint, and Hull-Biters stop occurring at all.
    [Fact]
    public void A_radius_of_two_leaves_no_bare_hull_between_adjacent_cells()
    {
        // Midpoint of the 4.0 gap between two Small footprints one cell apart.
        float distance = LeechVariantAssigner.DistanceToBox(4f, 0f, 0f, Small.X, Small.Y, Small.Z);

        Assert.Equal(2f, distance, 4);
        Assert.True(distance > Radius, "at the default 1.5 the midpoint is still bare hull");
        Assert.False(distance > 2.0f, "at 2.0 the midpoint is swallowed and Hull-Biters die out");
    }

    // BuildSize cannot be named here — GameLibs is a runtime-only dependency of the
    // test project — so the enum values are reached by ordinal:
    // Small = 0, Medium = 1, Large = 2.
    [Theory]
    [InlineData(0, 2f, 2f, 2f)]
    [InlineData(1, 4f, 2f, 2f)]
    [InlineData(2, 4f, 2f, 4f)]
    public void Footprints_match_the_authored_grid(int buildSize, float x, float y, float z)
    {
        Assert.Equal((x, y, z), HalfExtentsFor(buildSize));
    }

    private static (float X, float Y, float Z) HalfExtentsFor(int buildSize)
    {
        MethodInfo method = typeof(LeechVariantAssigner)
            .GetMethod(nameof(LeechVariantAssigner.HalfExtentsFor),
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(method);

        object value = Enum.ToObject(method.GetParameters()[0].ParameterType, buildSize);

        return ((float, float, float))method.Invoke(null, new[] { value });
    }
}
