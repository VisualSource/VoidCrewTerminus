using System.Collections;
using CG.Ship.Modules;
using Gameplay.Power;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UIElements;
using VoidCrewTerminus.UI;

namespace VoidCrewTerminus.Forge;

// Renders the level/alloy readout onto AlloyTerminalScreen via the same RenderTexture and
// UIDocument pipeline vanilla's WorldSpaceUI uses, minus the pointer-event forwarding that
// pipeline exists for: this screen takes no input. ModuleUpgradePanel owns the element tree.
public class ForgeScreenDisplay : MonoBehaviour
{
    [SerializeField] private int _panelWidth = 1152;
    [SerializeField] private int _panelHeight = 1536;

    private RenderTexture _renderTexture;
    private PanelSettings _panelSettings;
    private UIDocument _document;
    private Material _material;
    private ModuleUpgradePanel _panel;

    // Resolved from a parent: this component lives on the screen mesh, below the module root.
    private PowerDrain _powerDrain;

    private void Awake()
    {
        Build();
        ForgeMeterController.MeterChanged += Refresh;
        ForgeMeterController.LevelChanged += OnLevelChanged;
        Refresh();
        // After Refresh, so SetPowered picks the Filling-vs-Max variant off a level that
        // already reflects the real meter rather than the field's initial 1.
        WirePower();
    }

    private void OnDestroy()
    {
        ForgeMeterController.MeterChanged -= Refresh;
        ForgeMeterController.LevelChanged -= OnLevelChanged;
        if (_powerDrain != null && _powerDrain.IsOn != null)
            _powerDrain.IsOn.OnChange -= OnPowerChanged;
        DestroyGeneratedAssets();
    }

    // IsOn covers both ways the Forge stops running: a switched-off module, and a ship-wide
    // outage pushed down by PowerPropagator. ForgePowerLights hands the same drain to the
    // interior light, so the screen and the light can never disagree.
    private void WirePower()
    {
        if (_panel == null) return;

        _powerDrain = GetComponentInParent<CellModule>()?.PowerDrain;
        if (_powerDrain == null || _powerDrain.IsOn == null)
        {
            BepinPlugin.Log.LogDebug(
                $"[Forge] {name}: no CellModule.PowerDrain in parents — the screen will stay lit through a blackout.");
            return;
        }

        _powerDrain.IsOn.OnChange += OnPowerChanged;

        // Seeded from the live value: a freshly built module sits at IsOn=false until
        // BuildSocket connects it, and that turn-on arrives as an OnChange we already
        // subscribe to. Read .Value, never assign: the setter is RequestChange and would
        // try to switch the module on.
        _panel.SetPowered(_powerDrain.IsOn.Value);
    }

    private void OnPowerChanged(bool isOn)
    {
        BepinPlugin.Log.LogDebug($"[Forge] Screen power -> {(isOn ? "on" : "off")}");
        _panel?.SetPowered(isOn);
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

        // Cloned per instance: PanelSettings.targetTexture is per-panel, and a Forge module
        // could in principle be duplicated.
        _panelSettings = Instantiate(panelSettingsTemplate);
        _panelSettings.name = "ForgeScreen-PanelSettings";
        _panelSettings.targetTexture = _renderTexture;

        _document = gameObject.AddComponent<UIDocument>();
        _document.panelSettings = _panelSettings;
        _document.visualTreeAsset = visualTree;

        // UIDocument roots are focusable by default, and EventSystem.currentSelectedGameObject
        // is one piece of global state shared by every panel in the scene. Once this passive
        // readout claims focus nothing hands it back, and every other menu's
        // isCurrentFocusedPanel reads false forever.
        _document.rootVisualElement.focusable = false;

        // Unity auto-spawns a PanelEventHandler+PanelRaycaster for every runtime panel,
        // leaving this screen a redundant hit-test target for other UI's clicks. The handler
        // isn't guaranteed to exist the same frame the UIDocument is added, so this retries
        // across a few frames rather than checking once.
        StartCoroutine(DisableInputRaycaster());

        // After the UIDocument is configured: AddComponent on an active GameObject runs Awake
        // and OnEnable synchronously, and ModuleUpgradePanel.OnEnable reads rootVisualElement.
        _panel = gameObject.AddComponent<ModuleUpgradePanel>();

        // .material, not sharedMaterial, so the bundled asset stays untouched.
        // _EmissiveColorMap, not _UnlitColorMap: the authored ModuleScreen material drives the
        // RenderTexture through HDRP/Unlit's Emission inputs so Exposure Weight can be pinned
        // near 0 and the screen stays readable regardless of scene exposure.
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
