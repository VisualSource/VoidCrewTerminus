using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System.Reflection;
using VoidManager;
using VoidManager.MPModChecks;
namespace VoidCrewTerminus
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.USERS_PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    [BepInProcess("Void Crew.exe")]
    [BepInDependency(VoidManager.MyPluginInfo.PLUGIN_GUID)]
    public class BepinPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "N/A")]
        private void Awake()
        {
            Log = Logger;
            TerminusConfig.Init(Config);
            Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly());
            AssetLoader.TryLoadAssetBundlesNextToDLL();
            VoidManager.Events.Instance.HostStartSession += (_, _) => VoidCrewTerminus.Forge.ForgeOverlayTable.ClearAll();
            Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
        }
    }


    public class VoidManagerPlugin : VoidPlugin
    {
        public override MultiplayerType MPType => MultiplayerType.All;

        public override string Author => MyPluginInfo.PLUGIN_AUTHORS;

        public override string Description => MyPluginInfo.PLUGIN_DESCRIPTION;

        public override string ThunderstoreID => MyPluginInfo.PLUGIN_THUNDERSTORE_ID;
    }
}