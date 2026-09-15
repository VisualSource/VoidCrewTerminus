using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using VoidCrewTerminus.Forge;

namespace VoidCrewTerminus.UI;

// USS has no ::before/::after, so every meander bar and grid line is a real VisualElement,
// looped here rather than written 250 times into the UXML. USS has no @keyframes either, so
// animation is a class flip plus a transition.
[RequireComponent(typeof(UIDocument))]
public sealed class ModuleUpgradePanel : MonoBehaviour
{
    public enum PanelState { Filling, Max, Unpowered }

    // Pip count and level clamp must both match ForgeMeterController's MaxLevel, or a real
    // L6 Forge renders as if capped lower.
    public const int MaxLevel = ForgeMeterController.MaxLevel;

    const int MeanderKeys = 23;
    const int GridLinesPerGroup = 22;
    const float GridLineSpacing = 66f;
    const float GridLineStart = -260f;
    static readonly float[] GridAngles = { 0f, 60f, 120f };

    VisualElement _panel;
    VisualElement _fillTop;
    VisualElement _fillBot;
    Label _levelText;
    Label _curText;
    Label _totText;
    // One list per pip column: the two ladders mirror the same level, so a single flat list
    // spanning both would leave the right column permanently dark.
    readonly List<List<VisualElement>> _pipColumns = new List<List<VisualElement>>();

    int _level = 1;
    int _current;
    int _total;
    PanelState _state = PanelState.Filling;

    public int Level => _level;
    public int Current => _current;
    public int Total => _total;

    void OnEnable()
    {
        var root = GetComponent<UIDocument>().rootVisualElement;

        _panel = root.Q<VisualElement>("panel");
        _fillTop = root.Q<VisualElement>("fill-top");
        _fillBot = root.Q<VisualElement>("fill-bot");
        _levelText = root.Q<Label>("level-text");
        _curText = root.Q<Label>("cur-text");
        _totText = root.Q<Label>("tot-text");

        // The only load-bearing lookup: an unguarded miss throws out of OnEnable, so the
        // .anim schedule never registers and the screen silently draws authored defaults.
        if (_panel == null)
        {
            BepinPlugin.Log.LogWarning(
                "[Forge] ModuleUpgradePanel: no element named \"panel\" in the layout — the screen " +
                "will show its authored defaults and never update. Re-export the bundle.");
            return;
        }

        // OnEnable runs again on every re-enable, and UIDocument recreates its tree on some of
        // those but keeps it on others, so the builders adopt whatever is present rather than
        // appending; a stacked pip ladder would strand the live one at unreachable indices.
        _pipColumns.Clear();
        BuildMeander(root.Q<VisualElement>("meander-top"));
        BuildMeander(root.Q<VisualElement>("meander-bot"));
        BuildGrid(root.Q<VisualElement>("grid"));
        BuildPips(root.Q<VisualElement>("pips-left"));
        BuildPips(root.Q<VisualElement>("pips-right"));

        BepinPlugin.Log.LogDebug(
            $"[Forge] ModuleUpgradePanel bound: pipColumns={_pipColumns.Count}, " +
            $"fills={_fillTop != null && _fillBot != null}, " +
            $"labels={_levelText != null && _curText != null && _totText != null}");

        // ApplyState overwrites these before anything is visible; this just needs to not crash.
        Render();

        // First paint lands on the final values; transitions come on a frame
        // later so the panel never animates itself in from empty.
        _panel.schedule.Execute(() => _panel.AddToClassList("anim")).StartingIn(32);
    }

    static VisualElement Div(params string[] classes)
    {
        var el = new VisualElement();
        foreach (var c in classes) el.AddToClassList(c);
        return el;
    }

    static void BuildMeander(VisualElement host)
    {
        if (host == null) return;
        if (host.Q(className: "key") != null) return;
        for (int i = 0; i < MeanderKeys; i++)
        {
            var key = i % 2 == 1 ? Div("key", "key--flip") : Div("key");
            key.Add(Div("key__bar", "key__bar--a"));
            key.Add(Div("key__bar", "key__bar--b"));
            key.Add(Div("key__bar", "key__bar--c"));
            key.Add(Div("key__bar", "key__bar--d"));
            host.Add(key);
        }
    }

    static void BuildGrid(VisualElement host)
    {
        if (host == null) return;
        if (host.Q(className: "grid__group") != null) return;
        foreach (var angle in GridAngles)
        {
            var group = Div("grid__group");
            group.style.rotate = new Rotate(new Angle(angle, AngleUnit.Degree));
            for (int k = 0; k < GridLinesPerGroup; k++)
            {
                var line = Div("grid__line");
                line.style.top = GridLineStart + k * GridLineSpacing;
                group.Add(line);
            }
            host.Add(group);
        }
    }

    void BuildPips(VisualElement host)
    {
        if (host == null) return;

        var column = new List<VisualElement>(MaxLevel);
        host.Query(className: "rnk__pip").ForEach(column.Add);
        for (int i = column.Count; i < MaxLevel; i++)
        {
            var pip = Div("rnk__pip");
            host.Add(pip);
            column.Add(pip);
        }
        _pipColumns.Add(column);
    }

    // Leaves PanelState alone when unpowered: a power cut isn't something meter data implies.
    public void ApplyState(int level, float current, float total, bool maxed)
    {
        _level = Mathf.Clamp(level, 1, MaxLevel);

        if (maxed)
        {
            _total = Mathf.Max(1, Mathf.RoundToInt(total));
            _current = _total;
            if (_state != PanelState.Unpowered) _state = PanelState.Max;
        }
        else
        {
            _total = Mathf.Max(1, Mathf.RoundToInt(total));
            _current = Mathf.Clamp(Mathf.RoundToInt(current), 0, _total);
            if (_state != PanelState.Unpowered) _state = PanelState.Filling;
        }

        Render();
    }

    public void SetPowered(bool powered)
    {
        _state = powered
            ? (_level >= MaxLevel ? PanelState.Max : PanelState.Filling)
            : PanelState.Unpowered;
        Render();
    }

    // Public so ForgeMeterController.LevelChanged drives it; this panel detects no level-ups.
    public void FlashLevelUp()
    {
        if (_panel == null) return;
        _panel.AddToClassList("is-levelup");
        _panel.schedule.Execute(() => _panel.RemoveFromClassList("is-levelup")).StartingIn(110);
    }

    public void Render()
    {
        if (_panel == null) return;

        bool atMax = _state == PanelState.Max || _level >= MaxLevel;

        _panel.EnableInClassList("state-filling", _state == PanelState.Filling);
        _panel.EnableInClassList("state-max", _state == PanelState.Max);
        _panel.EnableInClassList("state-unpowered", _state == PanelState.Unpowered);

        if (_levelText != null) _levelText.text = _level.ToString();
        if (_curText != null) _curText.text = _current.ToString();
        if (_totText != null) _totText.text = _total.ToString();

        // Fill fraction over the full 360, starting at 9 o'clock, clockwise.
        float p = atMax ? 1f : (_total > 0 ? Mathf.Clamp01((float)_current / _total) : 0f);

        // The top half covers 9 -> 12 -> 3 (the first 180deg), the bottom half
        // the rest. Each half-disc is rotated counter-clockwise out of view and
        // swings back in as it fills, clipped by its overflow:hidden wrapper.
        float topFill = Mathf.Clamp01(p * 2f);
        float botFill = Mathf.Clamp01(p * 2f - 1f);
        SetRotation(_fillTop, -180f * (1f - topFill));
        SetRotation(_fillBot, -180f * (1f - botFill));

        _panel.EnableInClassList("is-full", p >= 0.999f);

        // Every column restates the same level; see _pipColumns.
        for (int c = 0; c < _pipColumns.Count; c++)
        {
            var column = _pipColumns[c];
            for (int i = 0; i < column.Count; i++)
                column[i].EnableInClassList("is-on", i < _level);
        }
    }

    static void SetRotation(VisualElement el, float degrees)
    {
        if (el == null) return;
        el.style.rotate = new Rotate(new Angle(degrees, AngleUnit.Degree));
    }
}
