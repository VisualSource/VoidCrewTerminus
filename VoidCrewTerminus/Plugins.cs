using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System.Reflection;
using UnityEngine;
using VoidManager;
using VoidManager.MPModChecks;

namespace VoidCrewTerminus
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.USERS_PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    [BepInProcess("Void Crew.exe")]
    [BepInDependency(VoidManager.MyPluginInfo.PLUGIN_GUID)]
    public class BepinPlugin : BaseUnityPlugin
    {
        private static Harmony _harmony;

        internal static ManualLogSource Log;

        // Stored so OnDestroy can unsubscribe: a bare lambda can't be removed, and under
        // hot-reload a leaked handler would keep running against the old assembly's statics.
        private System.EventHandler _onHostStartSession;

        [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Unity magic method")]
        private void Awake()
        {
            _harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly());
            LoadResources();
        }

        // Runs when ScriptEngine destroys the plugin object on hot-reload. Everything global
        // must be undone here, or the old assembly stays live beside the freshly loaded copy.
        [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Unity magic method")]
        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            _harmony = null;
            UnloadResources();
        }

        private void LoadResources()
        {
            Log = Logger;
            TerminusConfig.Init(Config);

            AssetLoader.TryLoadAssetBundlesNextToDLL();
            Patches.ForgeSectorHook.Init();
            Net.ForgeNetSync.Init();

            _onHostStartSession = (_, _) =>
            {
                VoidCrewTerminus.Forge.ForgeStateStore.ClearAll();
                VoidCrewTerminus.Forge.ForgeMeterController.ResetForRun();
                VoidCrewTerminus.Forge.UpgradeForgeBehavior.ResetForRun();
                VoidCrewTerminus.Escalation.SectorEscalation.ResetForRun();
                Patches.BossDefeatHook.OnSessionStart();
                // Clients never get HostStartSession, so the cleared state must be pushed
                // or they keep last run's scalar, bosses, meter and level.
                Net.ForgeNetSync.BroadcastState();
            };
            VoidManager.Events.Instance.HostStartSession += _onHostStartSession;

            // VoidManager discovers commands by walking the BepInEx Chainloader, which never
            // sees ScriptEngine-loaded assemblies. VoidPlugin/modlist registration has no such
            // hook, so a script-loaded session is solo-dev only.
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(MyPluginInfo.PLUGIN_GUID))
            {
                Logger.LogDebug("Loaded via ScriptEngine — self-registering chat commands with VoidManager.");
                SelfRegisterCommands();
            }

            Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
        }

        // VoidManager's CommandHandler is internal, so the re-discovery entry points are
        // reached by reflection. A VoidManager rename degrades to a warning, not a crash.
        private void SelfRegisterCommands()
        {
            try
            {
                var handler = AccessTools.TypeByName("VoidManager.Chat.Router.CommandHandler");
                var asm = Assembly.GetExecutingAssembly();
                AccessTools.Method(handler, "DiscoverCommands")?.Invoke(null, new object[] { asm, MyPluginInfo.USERS_PLUGIN_NAME });
                AccessTools.Method(handler, "DiscoverPublicCommands")?.Invoke(null, new object[] { asm, MyPluginInfo.USERS_PLUGIN_NAME });
                // ModMessages ride the same chainloader scan, so they need the same
                // self-registration for MP net code to be reachable.
                var mmHandler = AccessTools.TypeByName("VoidManager.ModMessages.ModMessageHandler");
                AccessTools.Method(mmHandler, "DiscoverModMessages")?.Invoke(null, new object[] { asm, Info });
            }
            catch (System.Exception e)
            {
                Logger.LogWarning($"Command self-registration failed (chat commands unavailable this session): {e.Message}");
            }
        }

        private void UnloadResources()
        {
            if (_onHostStartSession != null)
            {
                VoidManager.Events.Instance.HostStartSession -= _onHostStartSession;
                _onHostStartSession = null;
            }
            Patches.ForgeSectorHook.Shutdown();
            Net.ForgeNetSync.Shutdown();

            // Live scene objects created by this assembly must go with it: a reloaded
            // assembly brings its own types, so the attach patch would stack a second
            // behavior beside the orphaned old one.
            foreach (var forge in FindObjectsOfType<Forge.UpgradeForgeBehavior>(true))
                forge.TeardownForReload();
            foreach (var interactable in FindObjectsOfType<Forge.ForgeInteractable>(true))
                DestroyForgeInteractable(interactable.gameObject, interactable);
            // Separate component types, same runtime-generated-vs-authored-collider rule.
            foreach (var commitButton in FindObjectsOfType<Forge.ForgeCommitInteractable>(true))
                DestroyForgeInteractable(commitButton.gameObject, commitButton);
            foreach (var deconstructHandle in FindObjectsOfType<Forge.ForgeDeconstructInteractable>(true))
                DestroyForgeInteractable(deconstructHandle.gameObject, deconstructHandle);

            AssetLoader.UnloadBundles();
            Log?.LogDebug("Plugin resources unloaded (hot-reload teardown).");
        }

        // A runtime-generated click region is ours outright, so the whole GameObject goes.
        // An authored collider is the prefab's own, borrowed only to carry our component,
        // so strip the component and leave the collider for the reloaded assembly to reuse.
        private static void DestroyForgeInteractable(GameObject go, Component interactable)
        {
            if (go.name.StartsWith("ForgeInteractable_"))
                Destroy(go);
            else
                Destroy(interactable);
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
