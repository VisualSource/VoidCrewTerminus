using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CG.Space.Projectiles;
using Photon.Pun;
using ResourceAssets;
using UnityEngine;

namespace VoidCrewTerminus.Leech;

// The missile root has to sit on a layer inside TARGET_MASK to be shot down, and those
// are collision layers — the visibility probe reported 0 frames drawn at 8 m while on
// SpaceObjects, which is why two rounds of material fixes changed nothing. Vanilla keeps
// projectile visuals on child objects instead, so cloning a vanilla projectile prefab for
// the body inherits layers that are known to render, and brings its trail with it.
internal static class LeechMissileVisual
{
    private const int MaxStripPasses = 8;

    private static readonly string[] ListingTerms = { "Projectile", "Missile", "Torpedo", "Rocket" };

    private static MonoBehaviourContainer Container =>
        ResourceAssetContainer<MonoBehaviourContainer, MonoBehaviour, MonoBehaviourDef>.Instance;

    private static GameObject _prefab;
    private static string _resolvedFor;

    internal static void Attach(GameObject root, SyncedProjectile owner)
    {
        GameObject prefab = ResolvePrefab(TerminusConfig.LeechMissileVisualName);
        if (prefab == null)
        {
            AttachCapsule(root);
            return;
        }

        // Instantiated under an inactive holder so the prefab's own Awake never runs: it
        // would start a second, sourceless projectile lifecycle before we could strip it.
        var holder = new GameObject("Visual");
        holder.transform.SetParent(root.transform, false);
        holder.SetActive(false);

        GameObject visual = UnityEngine.Object.Instantiate(prefab, holder.transform, false);

        // Activating a half-stripped clone would wake a projectile with no Source or
        // Shooter and spam NREs every frame, so a clone that will not come apart is
        // dropped rather than shipped.
        if (!StripLogic(visual, owner))
        {
            BepinPlugin.Log.LogWarning(
                $"[Leech] '{TerminusConfig.LeechMissileVisualName}' would not give up its projectile; using a capsule.");
            UnityEngine.Object.DestroyImmediate(holder);
            AttachCapsule(root);
            return;
        }

        holder.SetActive(true);
    }

    // Only the projectile logic goes. The obvious sweep — destroy every MonoBehaviour and
    // keep Unity's built-in visual components — takes the trail with it: the comet tail is
    // not a TrailRenderer on the prefab, it is spawned at runtime by
    // VFX.OrbitObjectFollowEffect.InitEffects from a list of follow-effect prefabs. Kill
    // that script and there is a mesh and nothing behind it.
    //
    // Destroying runs in passes because [RequireComponent] refuses while a dependent is
    // still attached, and Unity only reports it afterwards as an error ("Can't remove
    // GuidedProjectile because ProjectileEOLEffect depends on it"). Taking the dependents
    // along, youngest first, keeps the strip silent.
    private static bool StripLogic(GameObject visual, SyncedProjectile owner)
    {
        List<MonoBehaviour> doomed = ProjectileLogic(visual);

        for (int pass = 0; pass < MaxStripPasses; pass++)
        {
            if (doomed.All(script => script == null)) break;

            bool progressed = false;
            foreach (MonoBehaviour script in doomed)
            {
                if (script == null || IsRequiredByPeer(script, doomed)) continue;
                UnityEngine.Object.DestroyImmediate(script);
                progressed = true;
            }

            if (!progressed) break;
        }

        RepointToOwner(visual, owner);

        // PhotonView is a MonoBehaviour like any other, so it has to be named explicitly
        // now that the sweep is selective — it would otherwise claim a view id for an
        // object nothing replicates.
        foreach (PhotonView view in visual.GetComponentsInChildren<PhotonView>(true))
            UnityEngine.Object.DestroyImmediate(view);

        foreach (Collider collider in visual.GetComponentsInChildren<Collider>(true))
            UnityEngine.Object.DestroyImmediate(collider);

        foreach (Rigidbody body in visual.GetComponentsInChildren<Rigidbody>(true))
            UnityEngine.Object.DestroyImmediate(body);

        return visual.GetComponentsInChildren<SyncedProjectile>(true).Length == 0;
    }

    // The projectile components, plus whatever [RequireComponent]s them, transitively.
    private static List<MonoBehaviour> ProjectileLogic(GameObject visual)
    {
        List<MonoBehaviour> all = visual.GetComponentsInChildren<MonoBehaviour>(true)
            .Where(script => script != null)
            .ToList();

        var doomed = all.Where(script => script is SyncedProjectile).ToList();

        for (int pass = 0; pass < MaxStripPasses; pass++)
        {
            List<MonoBehaviour> dependents = all
                .Where(script => !doomed.Contains(script) && DependsOnAny(script, doomed))
                .ToList();

            if (dependents.Count == 0) break;
            doomed.AddRange(dependents);
        }

        return doomed;
    }

    // The VFX that survive were authored against the prefab's own projectile. Pointing
    // them at ours keeps them working rather than merely quiet — OrbitObjectFollowEffect
    // hangs the trail's detach-and-fade off OnExplode/OnExpired, so with this the tail
    // releases when the missile deploys instead of vanishing with it. A null reference
    // there is what logged "Detach on death can only be used for projectile trails".
    private static void RepointToOwner(GameObject visual, SyncedProjectile owner)
    {
        if (owner == null) return;

        const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        foreach (MonoBehaviour script in visual.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (script == null) continue;

            for (Type type = script.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
                foreach (FieldInfo field in type.GetFields(fields))
                    if (field.FieldType.IsInstanceOfType(owner) && (UnityEngine.Object)field.GetValue(script) == null)
                        field.SetValue(script, owner);
        }
    }

    private static bool DependsOnAny(MonoBehaviour candidate, List<MonoBehaviour> targets) =>
        targets.Any(target => target != null
                              && target.gameObject == candidate.gameObject
                              && RequiresType(candidate, target.GetType()));

    private static bool RequiresType(MonoBehaviour dependent, Type type)
    {
        foreach (object attribute in dependent.GetType().GetCustomAttributes(typeof(RequireComponent), true))
        {
            var required = (RequireComponent)attribute;
            if (Demands(required.m_Type0, type) || Demands(required.m_Type1, type) || Demands(required.m_Type2, type))
                return true;
        }
        return false;
    }

    // RequireComponent only binds components sharing a GameObject.
    private static bool IsRequiredByPeer(MonoBehaviour candidate, List<MonoBehaviour> pool) =>
        pool.Any(peer => peer != null
                         && !ReferenceEquals(peer, candidate)
                         && peer.gameObject == candidate.gameObject
                         && RequiresType(peer, candidate.GetType()));

    private static bool Demands(Type required, Type candidate) =>
        required != null && required.IsAssignableFrom(candidate);

    private static void AttachCapsule(GameObject root)
    {
        GameObject capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        capsule.name = "Visual";
        UnityEngine.Object.DestroyImmediate(capsule.GetComponent<Collider>());

        capsule.transform.SetParent(root.transform, false);
        capsule.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        capsule.transform.localScale = new Vector3(0.5f, 0.7f, 0.5f);
        capsule.layer = RenderedLayer(root.layer);

        Material body = LeechMissileFactory.BodyMaterial();
        if (body != null) capsule.GetComponent<MeshRenderer>().sharedMaterial = body;

        BepinPlugin.Log.LogDebug($"[Leech] missile falling back to a capsule body on layer {capsule.layer}.");
    }

    // The root's layer is picked for shootability, which is a collision concern. If no
    // camera draws it, the fallback body goes on Default, which the probe run confirmed
    // does render.
    private static int RenderedLayer(int preferred)
    {
        Camera camera = Camera.main;
        return camera == null || (camera.cullingMask & (1 << preferred)) != 0 ? preferred : 0;
    }

    private static GameObject ResolvePrefab(string prefabName)
    {
        if (_resolvedFor == prefabName) return _prefab;

        // Only a hit is cached. The container is populated from the session's own asset
        // load, so an early miss is not necessarily a permanent one, and caching it would
        // pin the capsule for the rest of the run.
        _resolvedFor = null;
        _prefab = null;

        if (string.IsNullOrWhiteSpace(prefabName)) return null;

        MonoBehaviourDef def = Find(prefabName);
        if (def == null)
        {
            BepinPlugin.Log.LogWarning(
                $"[Leech] no NPC projectile prefab named '{prefabName}' — run !leechvisual for the list.");
            return null;
        }

        MonoBehaviour asset = def.Asset;
        if (asset == null)
        {
            BepinPlugin.Log.LogWarning($"[Leech] projectile prefab '{prefabName}' is registered but did not load.");
            return null;
        }

        _resolvedFor = prefabName;
        _prefab = asset.gameObject;
        BepinPlugin.Log.LogDebug(
            $"[Leech] missile body cloned from '{prefabName}' — root layer {_prefab.layer}, " +
            $"{_prefab.GetComponentsInChildren<Renderer>(true).Length} renderer(s).");
        return _prefab;
    }

    // GetAssetDefByName would do this, but it dereferences Asset on every entry it walks,
    // which Resources.Loads the whole container to reach one prefab. Filename comes off
    // the path instead, loading nothing until there is a match.
    private static MonoBehaviourDef Find(string prefabName) =>
        Container?.AssetDescriptions?.FirstOrDefault(
            def => string.Equals(def?.Ref?.Filename, prefabName, StringComparison.OrdinalIgnoreCase));

    internal static List<string> Candidates() =>
        Container?.AssetDescriptions?
            .Select(def => def?.Ref?.Filename)
            .Where(name => !string.IsNullOrEmpty(name)
                           && ListingTerms.Any(term => name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0))
            .OrderBy(name => name)
            .ToList()
        ?? new List<string>();
}
