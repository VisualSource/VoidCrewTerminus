using VoidCrewTerminus.Net;
using Xunit;

namespace VoidCrewTerminus.Tests;

// The buffer both cursed flags and module overlays sit behind, when a broadcast
// arrives before the PhotonView it describes has spawned locally.
//
// The load-bearing rule is take-once: a drained entry must be gone, or the same
// overlay reapplies on every later instantiate.
public class PendingByViewIdTests
{
    [Fact]
    public void TryTake_ReturnsBufferedValue()
    {
        var pending = new PendingByViewId<int>();
        pending.Buffer(42, 7);

        Assert.True(pending.TryTake(42, out int value));
        Assert.Equal(7, value);
    }

    [Fact]
    public void TryTake_UnknownViewId_IsFalse()
    {
        var pending = new PendingByViewId<int>();

        Assert.False(pending.TryTake(42, out int value));
        Assert.Equal(0, value);
    }

    // The whole point: draining removes. A second instantiate for the same ViewID
    // must not reapply the overlay.
    [Fact]
    public void TryTake_ConsumesTheEntry()
    {
        var pending = new PendingByViewId<int>();
        pending.Buffer(42, 7);

        Assert.True(pending.TryTake(42, out _));
        Assert.False(pending.TryTake(42, out _));
        Assert.Equal(0, pending.Count);
    }

    // A newer broadcast supersedes an older undrained one, rather than the stale
    // value winning.
    [Fact]
    public void Buffer_LatestValueWins()
    {
        var pending = new PendingByViewId<string>();
        pending.Buffer(42, "old");
        pending.Buffer(42, "new");

        Assert.Equal(1, pending.Count);
        Assert.True(pending.TryTake(42, out string value));
        Assert.Equal("new", value);
    }

    [Fact]
    public void Buffer_KeepsDistinctViewIdsSeparate()
    {
        var pending = new PendingByViewId<int>();
        pending.Buffer(1, 10);
        pending.Buffer(2, 20);

        Assert.True(pending.TryTake(2, out int second));
        Assert.Equal(20, second);
        Assert.True(pending.TryTake(1, out int first));
        Assert.Equal(10, first);
    }

    // Leaving a room invalidates everything: ViewIDs are room-scoped, so a
    // leftover would eventually land on an unrelated object that inherits the ID.
    [Fact]
    public void Clear_DropsEverything()
    {
        var pending = new PendingByViewId<int>();
        pending.Buffer(1, 10);
        pending.Buffer(2, 20);

        pending.Clear();

        Assert.Equal(0, pending.Count);
        Assert.False(pending.TryTake(1, out _));
    }

    // Reference types are the real usage (ForgeSnapshot); null must round-trip
    // as a buffered value rather than reading as "nothing buffered".
    [Fact]
    public void Buffer_TolerantOfNullValues()
    {
        var pending = new PendingByViewId<string>();
        pending.Buffer(42, null);

        Assert.Equal(1, pending.Count);
        Assert.True(pending.TryTake(42, out string value));
        Assert.Null(value);
    }
}
