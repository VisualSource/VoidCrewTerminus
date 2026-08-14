using System.Runtime.CompilerServices;

// The forge-sync logic (ForgeNetSync, IForgeTransport, PendingByViewId) is
// `internal` because nothing outside this plugin should speak to it — but its
// gate rules are the part of the mod most in need of tests, so the test assembly
// is granted access rather than widening the shipped public surface.
[assembly: InternalsVisibleTo("VoidCrewTerminus.Tests")]
