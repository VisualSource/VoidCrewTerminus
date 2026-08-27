using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UIElements;
using VoidCrewTerminus.UI;

namespace VoidCrewTerminus.Forge;

// Renders the Forge's level/alloy readout onto AlloyTerminalScreen's mesh —
// same RenderTexture-via-UIDocument pipeline vanilla's CG.Client.UI.Terminals.
// WorldSpaceUI uses for in-world terminals, minus everything that pipeline
// exists for (pointer-event forwarding, PanelEventHandler lookup): this screen
// takes no input, so none of that applies.
//
// Layout (VisualTreeAsset) and PanelSettings are Unity-authored bundled assets
// (AssetLoader.ForgeScreenVisualTree / ForgeScreenPanelSettingsTemplate). This
// component owns the plumbing only — RenderTexture, PanelSettings clone,
// UIDocument, material wiring — and hands the actual VisualElement tree to
// ModuleUpgradePanel (UI/ModuleUpgradePanel.cs), which builds the decorative
// elements the UXML can't (meander bars, grid lines, pips) and drives the
// level/ring/pip visuals from ApplyState.
public class ForgeScreenDisplay : MonoBehaviour
{
    [SerializeField] private int _panelWidth = 1152;
    [SerializeField] private int _panelHeight = 1536;

    private RenderTexture _renderTexture;
    private PanelSettings _panelSettings;
    private UIDocument _document;
    private Material _material;
    private ModuleUpgradePanel _panel;

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

    private void OnLevelChanged(int _)
    {
        Refresh();
        _panel?.FlashLevelUp();
    }

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

        // Unity auto-spawns a PanelEventHandler+PanelRaycaster for every runtime
        // panel, texture-targeted or not, and that raycaster plugs straight into
        // the shared EventSystem used by every other menu (fabricator included).
        // This screen never forwards pointer events anywhere, so left enabled it
        // just silently eats clicks meant for other UI. Vanilla's WorldSpaceUI
        // disables its own panel's raycaster the same way, but does it as a single
        // synchronous check right here — that's safe for them only because their
        // terminal input comes through a separate manual 3D-raycast path, so a
        // missed match is invisible. We have no fallback, and the handler isn't
        // guaranteed to exist the same frame the UIDocument is added, so retry
        // across a few frames instead of checking once.
        StartCoroutine(DisableInputRaycaster());

        // Added after the UIDocument is fully configured — Unity runs Awake+
        // OnEnable synchronously for a component added to an already-active
        // GameObject, so ModuleUpgradePanel.OnEnable (which reads
        // GetComponent<UIDocument>().rootVisualElement) sees a working panel
        // immediately, not a half-configured one from ordering luck.
        _panel = gameObject.AddComponent<ModuleUpgradePanel>();

        // .material (not sharedMaterial) instances it — the bundled Unlit asset
        // itself stays untouched, matching WorldSpaceUI.RebuildPanel's approach.
        //
        // _EmissiveColorMap, not _UnlitColorMap (vanilla's WorldSpaceUI target,
        // and this component's first draft): the authored ModuleScreen material
        // drives the RenderTexture through HDRP/Unlit's Emission inputs instead
        // of its plain Color map, specifically so Exposure Weight can be pinned
        // near 0 — the screen stays equally readable regardless of scene exposure,
        // which a base-color map doesn't get for free.
        _material = meshRenderer.material;
        _material.SetTexture("_EmissiveColorMap", _renderTexture);
        meshRenderer.material = _material;
    }

    private IEnumerator DisableInputRaycaster()
    {
        var panel = _document.rootVisualElement.panel;
        for (int frame = 0; frame < 30; frame++)
        {
            foreach (var handler in FindObjectsOfType<PanelEventHandler>())
            {
                if (handler.panel != panel) continue;
                var raycaster = handler.GetComponent<PanelRaycaster>();
                if (raycaster != null) raycaster.enabled = false;
                yield break;
            }
            yield return null;
        }
        BepinPlugin.Log.LogWarning("[Forge] Could not find this screen's PanelEventHandler after 30 frames — it may keep intercepting clicks meant for other UI.");
    }

    private void Refresh()
    {
        if (_panel == null) return;

        bool maxed = ForgeMeterController.IsMaxed;
        _panel.ApplyState(
            ForgeMeterController.Level,
            ForgeMeterController.Meter,
            ForgeMeterController.ThresholdFor(ForgeMeterController.Level),
            maxed);
    }

    private void DestroyGeneratedAssets()
    {
        if (_document != null) Destroy(_document);
        if (_renderTexture != null) { _renderTexture.Release(); Destroy(_renderTexture); }
        if (_panelSettings != null) Destroy(_panelSettings);
        if (_material != null) Destroy(_material);
    }
}
