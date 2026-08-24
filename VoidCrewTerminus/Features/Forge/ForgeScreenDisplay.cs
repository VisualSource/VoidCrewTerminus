using UnityEngine;
using UnityEngine.UIElements;

namespace VoidCrewTerminus.Forge;

// Renders the Forge's level/alloy readout onto AlloyTerminalScreen's mesh —
// same RenderTexture-via-UIDocument pipeline vanilla's CG.Client.UI.Terminals.
// WorldSpaceUI uses for in-world terminals, minus everything that pipeline
// exists for (pointer-event forwarding, PanelEventHandler lookup): this screen
// takes no input, so none of that applies.
//
// Layout and PanelSettings are Unity-authored bundled assets (AssetLoader.
// ForgeScreenVisualTree / ForgeScreenPanelSettingsTemplate) — this component
// only wires them to live ForgeMeterController data. The authored UXML is
// expected to expose these named elements (root.Q lookups below):
//   "LevelLabel"     — Label, text set to "LEVEL {n}" or "MAX" at max level.
//   "RingFillRight"  — half-circle element covering the RIGHT 50% of the ring,
//                      transform-origin at its LEFT edge (the ring's center).
//   "RingFillLeft"   — half-circle element covering the LEFT 50%, transform-
//                      origin at its RIGHT edge (also the ring's center).
// The static background track (the dim full circle behind the fill) needs no
// code at all — a plain USS circle (border-radius: 50%, uniform border-width)
// authored directly in the UXML. Only the fill sweep is data-driven.
//
// Classic two-half "pie" fill: each half starts rotated fully out of view and
// sweeps in around the shared center as its half of the range fills — right
// half covers 0-50% of the total, left half covers 50-100%. Get the rotation
// direction backwards and it'll sweep the wrong way or empty from the wrong
// side — tune signs/transform-origin visually in Play mode if so; there's no
// way to verify this by reading code alone.
public class ForgeScreenDisplay : MonoBehaviour
{
    private const string LevelLabelName = "LevelLabel";
    private const string RingFillRightName = "RingFillRight";
    private const string RingFillLeftName = "RingFillLeft";

    [SerializeField] private int _panelWidth = 512;
    [SerializeField] private int _panelHeight = 512;

    private RenderTexture _renderTexture;
    private PanelSettings _panelSettings;
    private UIDocument _document;
    private Material _material;

    private Label _levelLabel;
    private VisualElement _ringFillRight;
    private VisualElement _ringFillLeft;

    private void Awake()
    {
        Build();
        ForgeMeterController.MeterChanged += Refresh;
        ForgeMeterController.LevelChanged += OnLevelChanged;
        Refresh();
    }

    private void OnDestroy()
    {
        ForgeMeterController.MeterChanged -= Refresh;
        ForgeMeterController.LevelChanged -= OnLevelChanged;
        DestroyGeneratedAssets();
    }

    private void OnLevelChanged(int _) => Refresh();

    private void Build()
    {
        var meshRenderer = GetComponent<MeshRenderer>();
        if (meshRenderer == null)
        {
            BepinPlugin.Log.LogWarning($"[Forge] {name} has no MeshRenderer — ForgeScreenDisplay can't apply the RenderTexture.");
            return;
        }

        var visualTree = AssetLoader.ForgeScreenVisualTree;
        var panelSettingsTemplate = AssetLoader.ForgeScreenPanelSettingsTemplate;
        if (visualTree == null || panelSettingsTemplate == null)
        {
            BepinPlugin.Log.LogWarning(
                $"[Forge] AlloyTerminalScreen assets not found in bundle (layout={visualTree != null}, panelSettings={panelSettingsTemplate != null}) — screen will not update. Re-export if these were just added.");
            return;
        }

        _renderTexture = new RenderTexture(_panelWidth, _panelHeight, 24) { name = "ForgeScreen-RenderTexture" };

        // Cloned per instance (not shared) — PanelSettings.targetTexture is
        // per-panel, and a Forge module could in principle be duplicated.
        _panelSettings = Instantiate(panelSettingsTemplate);
        _panelSettings.name = "ForgeScreen-PanelSettings";
        _panelSettings.targetTexture = _renderTexture;

        _document = gameObject.AddComponent<UIDocument>();
        _document.panelSettings = _panelSettings;
        _document.visualTreeAsset = visualTree;

        var root = _document.rootVisualElement;
        _levelLabel = root.Q<Label>(LevelLabelName);
        _ringFillRight = root.Q<VisualElement>(RingFillRightName);
        _ringFillLeft = root.Q<VisualElement>(RingFillLeftName);

        if (_levelLabel == null || _ringFillRight == null || _ringFillLeft == null)
            BepinPlugin.Log.LogWarning(
                $"[Forge] ForgeScreenLayout missing expected element(s) — LevelLabel={_levelLabel != null}, RingFillRight={_ringFillRight != null}, RingFillLeft={_ringFillLeft != null}.");

        // .material (not sharedMaterial) instances it — the bundled Unlit asset
        // itself stays untouched, matching WorldSpaceUI.RebuildPanel's approach.
        _material = meshRenderer.material;
        _material.SetTexture("_UnlitColorMap", _renderTexture);
        meshRenderer.material = _material;
    }

    private void Refresh()
    {
        if (_document == null) return;

        bool maxed = ForgeMeterController.IsMaxed;
        if (_levelLabel != null)
            _levelLabel.text = maxed ? "MAX" : $"LEVEL {ForgeMeterController.Level}";

        float fraction = maxed
            ? 1f
            : Mathf.Clamp01(ForgeMeterController.Meter / ForgeMeterController.ThresholdFor(ForgeMeterController.Level));
        SetFill(fraction);
    }

    private void SetFill(float fraction)
    {
        if (_ringFillRight == null || _ringFillLeft == null) return;

        // 0→0.5 sweeps the right half in; 0.5→1 sweeps the left half in on top
        // of the now-fully-swept right half.
        float rightProgress = Mathf.Clamp01(fraction * 2f);
        float leftProgress = Mathf.Clamp01(fraction * 2f - 1f);

        _ringFillRight.style.rotate = new StyleRotate(new Rotate(new Angle(Mathf.Lerp(-90f, 90f, rightProgress))));
        _ringFillLeft.style.rotate = new StyleRotate(new Rotate(new Angle(Mathf.Lerp(-90f, 90f, leftProgress))));
    }

    private void DestroyGeneratedAssets()
    {
        if (_document != null) Destroy(_document);
        if (_renderTexture != null) { _renderTexture.Release(); Destroy(_renderTexture); }
        if (_panelSettings != null) Destroy(_panelSettings);
        if (_material != null) Destroy(_material);
    }
}
