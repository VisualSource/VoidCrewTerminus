using UnityEngine;

namespace VoidCrewTerminus.Leech;

// Two invisibility fixes have now shipped without a visual confirmation either way,
// because "I couldn't see it" cannot distinguish a material that never renders from
// an object the camera never pointed at. This reports which one it is: isVisible is
// true only on a frame some camera actually drew the renderer.
internal sealed class LeechVisibilityProbe : MonoBehaviour
{
    private const float ReportInterval = 3f;

    internal static void Attach(GameObject target, string label)
    {
        var probe = target.AddComponent<LeechVisibilityProbe>();
        probe._label = label;
    }

    private string _label;
    private Renderer _renderer;
    private int _frames;
    private int _drawnFrames;
    private float _nextReport;

    private void Awake()
    {
        _renderer = GetComponentInChildren<Renderer>(true);
        _nextReport = Time.time + ReportInterval;
    }

    private void Update()
    {
        _frames++;
        if (_renderer != null && _renderer.isVisible) _drawnFrames++;

        if (Time.time < _nextReport) return;
        _nextReport = Time.time + ReportInterval;
        Report("in flight");
    }

    private void OnDestroy() => Report("final");

    private void Report(string stage)
    {
        if (_renderer == null)
        {
            BepinPlugin.Log.LogDebug($"[Leech] probe {_label} ({stage}): no renderer.");
            return;
        }

        int layer = _renderer.gameObject.layer;
        Camera camera = Camera.main;
        string sighting = camera == null
            ? "no main camera"
            : $"{Vector3.Distance(camera.transform.position, transform.position):0} m away, " +
              $"{(InFrustum(camera) ? "in frustum" : "off screen")}, " +
              $"layer {layer} ('{LayerMask.LayerToName(layer)}') " +
              $"{((camera.cullingMask & (1 << layer)) != 0 ? "in" : "OUTSIDE")} cull mask";

        Material material = _renderer.sharedMaterial;
        string surface = material == null
            ? "no material"
            : $"'{material.shader.name}' {(material.shader.isSupported ? "supported" : "UNSUPPORTED")}, " +
              $"queue {material.renderQueue}";

        BepinPlugin.Log.LogDebug(
            $"[Leech] probe {_label} ({stage}): drawn {_drawnFrames}/{_frames} frames, " +
            $"renderer {(_renderer.enabled ? "enabled" : "DISABLED")}, {surface}, {sighting}.");
    }

    private bool InFrustum(Camera camera) =>
        GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(camera), _renderer.bounds);
}
