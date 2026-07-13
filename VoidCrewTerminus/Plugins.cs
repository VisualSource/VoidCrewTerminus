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
        private static Harmony _harmony;

        internal static ManualLogSource Log;

        // Stored so OnDestroy can unsubscribe — a bare lambda can't be removed, and
        // under ScriptEngine hot-reload a leaked handler from the old assembly
        // would keep running against the old statics.
        private System.EventHandler _onHostStartSession;

        [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Unity magic method")]
        private void Awake()
        {
            _harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly());
            LoadResources();
        }

        // Unity magic method — runs when ScriptEngine (BepInEx.Debug) destroys the
        // plugin object on F6 reload. Everything global this plugin touched must be
        // undone here, or the OLD assembly keeps patches/subscriptions alive next
        // to the freshly loaded copy.
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

            _onHostStartSession = (_, _) =>
            {
                VoidCrewTerminus.Forge.ForgeOverlayTable.ClearAll();
                VoidCrewTerminus.Forge.ForgeMeterController.ResetForRun();
            };
            VoidManager.Events.Instance.HostStartSession += _onHostStartSession;

            // VoidManager discovers commands/plugins by walking the BepInEx
            // Chainloader — which never sees ScriptEngine-loaded assemblies. When
            // running from BepInEx/scripts (hot-reload dev flow), self-register the
            // chat commands so the ! dev harness keeps working. VoidPlugin/modlist
            // registration has no such hook, so script-loaded sessions are
            // solo-dev only.
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(MyPluginInfo.PLUGIN_GUID))
            {
                Logger.LogDebug("Loaded via ScriptEngine — self-registering chat commands with VoidManager.");
                SelfRegisterCommands();
            }

            Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
        }

        // VoidManager's CommandHandler is internal, so the re-discovery entry
        // points are reached via reflection. Best-effort: a VoidManager update
        // renaming them degrades to a logged warning, not a crash.
        private void SelfRegisterCommands()
        {
            try
            {
                var handler = AccessTools.TypeByName("VoidManager.Chat.Router.CommandHandler");
                var asm = Assembly.GetExecutingAssembly();
                AccessTools.Method(handler, "DiscoverCommands")?.Invoke(null, new object[] { asm, MyPluginInfo.USERS_PLUGIN_NAME });
                AccessTools.Method(handler, "DiscoverPublicCommands")?.Invoke(null, new object[] { asm, MyPluginInfo.USERS_PLUGIN_NAME });
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

            // Live scene objects created by this assembly must go with it: a
            // reloaded assembly has its OWN UpgradeForgeBehavior type, so the
            // attach patch would stack a second behavior beside the orphaned old
            // one. Teardown undocks held items (restoring their physics) first.
            foreach (var forge in FindObjectsOfType<Forge.UpgradeForgeBehavior>(true))
                forge.TeardownForReload();
            foreach (var interactable in FindObjectsOfType<Forge.ForgeInteractable>(true))
            {
                if (interactable.gameObject.name.StartsWith("ForgeInteractable_"))
                    Destroy(interactable.gameObject); // generated click target
                else
                    Destroy(interactable);            // authored collider — strip only our component
            }

            AssetLoader.UnloadBundles();
            Log?.LogDebug("Plugin resources unloaded (hot-reload teardown).");
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
