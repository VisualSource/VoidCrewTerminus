using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using RuntimeAssets;

namespace VoidCrewTerminus;

public class AssetLoader
{
    public static void TryLoadAssetBundlesNextToDLL()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var dllPath = !string.IsNullOrWhiteSpace(asm.Location) ? asm.Location : new Uri(asm.CodeBase).LocalPath;

            var dir = Path.GetDirectoryName(dllPath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                BepinPlugin.Log.LogError($"[AssetLoader] Cound not resolve DLL Directory (dllPath='{dllPath}')");
                return;
            }

            BepinPlugin.Log.LogInfo($"[AssetLoader] Scanning for asset bundle manifests in: {dir}");

            int loaded = 0;
            foreach (var filepath in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
            {
                var filename = Path.GetFileName(filepath);
                var fileExt = Path.GetExtension(filepath);

                if (!string.IsNullOrEmpty(fileExt) && fileExt != ".metem") continue;
                if (!File.Exists(filepath)) continue;

                try
                {
                    var bundle = AssetBundle.LoadFromFile(filepath);
                    if (!(bool)bundle) continue;
                    bundle.Unload(true);

                    RuntimeAssetsAPI.LoadAssetBundle(filepath);
                    loaded++;


                    BepinPlugin.Log.LogInfo($"[AssetLoader] Loaded asset bundle: {filename}");
                }
                catch (System.Exception e)
                {
                    BepinPlugin.Log.LogInfo($"[AssetLoader] Error while probing/loading '{filename}': {e}");
                }
            }
        }
        catch (System.Exception e)
        {

            BepinPlugin.Log.LogError($"[AssetLoader] Failed loading asset bundles: {e}");
        }
    }
}