using VoidCrewTerminus.Forge;
using Xunit;

namespace VoidCrewTerminus.Tests;

// Snapshot-bridge coverage only. The per-module (CellModule → ForgeModuleState)
// path can't run outside the game — GetOrCreate → Attach → BuildMods hits StatType,
// same limitation the two skipped commit tests document.
public class ForgeStateStoreTests
{
    // View-ids are namespaced per test to avoid cross-test bleed (the store is static).
    private static int _nextId = 100_000;
    private static int NextId() => System.Threading.Interlocked.Increment(ref _nextId);

    [Fact]
    public void SaveSnapshot_ThenTryPeek_ReturnsSameSnapshot()
    {
        int id = NextId();
        var snap = ForgeSnapshot.Empty.WithLevel(5);
        ForgeStateStore.SaveSnapshot(id, snap);

        bool found = ForgeStateStore.TryPeekSnapshot(id, out var peeked);

        Assert.True(found);
        Assert.Same(snap, peeked);
    }

    [Fact]
    public void TryPeekSnapshot_IsNonConsuming_CanPeekTwice()
    {
        int id = NextId();
        ForgeStateStore.SaveSnapshot(id, ForgeSnapshot.Empty.WithLevel(6));

        Assert.True(ForgeStateStore.TryPeekSnapshot(id, out _));
        Assert.True(ForgeStateStore.TryPeekSnapshot(id, out _));
    }

    [Fact]
    public void TryTakeSnapshot_ConsumesTheEntry()
    {
        int id = NextId();
        ForgeStateStore.SaveSnapshot(id, ForgeSnapshot.Empty.WithLevel(8));

        Assert.True(ForgeStateStore.TryTakeSnapshot(id, out var taken));
        Assert.Equal(8, taken.Level);

        // Subsequent peek/take on the same id fails.
        Assert.False(ForgeStateStore.TryPeekSnapshot(id, out _));
        Assert.False(ForgeStateStore.TryTakeSnapshot(id, out _));
    }

    [Fact]
    public void SaveSnapshot_OverwritesPriorSnapshotForSameId()
    {
        int id = NextId();
        ForgeStateStore.SaveSnapshot(id, ForgeSnapshot.Empty.WithLevel(4));
        ForgeStateStore.SaveSnapshot(id, ForgeSnapshot.Empty.WithLevel(9));

        Assert.True(ForgeStateStore.TryPeekSnapshot(id, out var snap));
        Assert.Equal(9, snap.Level);

        // Cleanup so ClearAll test below isn't polluted.
        ForgeStateStore.TryTakeSnapshot(id, out _);
    }

    [Fact]
    public void SaveSnapshot_NullArgument_StoresEmpty()
    {
        int id = NextId();
        ForgeStateStore.SaveSnapshot(id, null);

        Assert.True(ForgeStateStore.TryPeekSnapshot(id, out var snap));
        Assert.Same(ForgeSnapshot.Empty, snap);

        ForgeStateStore.TryTakeSnapshot(id, out _);
    }

    [Fact]
    public void TryPeekSnapshot_UnknownId_ReturnsFalse()
    {
        Assert.False(ForgeStateStore.TryPeekSnapshot(NextId(), out _));
    }

    [Fact]
    public void ClearAll_WipesAllSnapshots()
    {
        int a = NextId(), b = NextId();
        ForgeStateStore.SaveSnapshot(a, ForgeSnapshot.Empty.WithLevel(4));
        ForgeStateStore.SaveSnapshot(b, ForgeSnapshot.Empty.WithLevel(5));

        ForgeStateStore.ClearAll();

        Assert.False(ForgeStateStore.TryPeekSnapshot(a, out _));
        Assert.False(ForgeStateStore.TryPeekSnapshot(b, out _));
    }
}
