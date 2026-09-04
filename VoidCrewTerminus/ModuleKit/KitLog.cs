using BepInEx.Logging;

namespace VoidCrewTerminus.ModuleKit;

// The kit's only tie to the host plugin, assigned once from BepinPlugin.LoadResources.
// A seam rather than a direct BepinPlugin.Log reference so ModuleKit/ can move to its
// own assembly without touching every call site.
internal static class KitLog
{
    internal static ManualLogSource Log;
}
