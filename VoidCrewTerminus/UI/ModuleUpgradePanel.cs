using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace VoidCrewTerminus;

/// <summary>
/// Drives the module upgrade panel. Ported from src/panel.ts in the HTML mock.
///
/// Two jobs:
///   1. Build the repeated decorative elements. USS has no ::before/::after, so
///      every meander bar and grid line is a real VisualElement. Writing 250 of
///      them into the UXML would be unreadable, so they are looped here.
///   2. Drive visuals by toggling classes and setting inline style values.
///      USS has no @keyframes, so animation is a class flip plus a transition.
/// </summary>
[RequireComponent(typeof(UIDocument))]
public sealed class ModuleUpgradePanel : MonoBehaviour
{
    public enum PanelState { Filling, Max, Unpowered }

    public const int MaxLevel = 5;

    // Cost of level (i+1) -> (i+2). PLACEHOLDER - swap in the real curve.
    static readonly int[] CostCurve = { 10, 25, 50, 85 };

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
    readonly List<VisualElement> _pips = new List<VisualElement>();

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

        BuildMeander(root.Q<VisualElement>("meander-top"));
        BuildMeander(root.Q<VisualElement>("meander-bot"));
        BuildGrid(root.Q<VisualElement>("grid"));
        BuildPips(root.Q<VisualElement>("pips-left"));
        BuildPips(root.Q<VisualElement>("pips-right"));

        _total = CostForLevel(_level);
        Render();

        // First paint lands on the final values; transitions come on a frame
        // later so the panel never animates itself in from empty.
        _panel.schedule.Execute(() => _panel.AddToClassList("anim")).StartingIn(32);
    }

    // ---------------------------------------------------------------- build --

    static VisualElement Div(params string[] classes)
    {
        var el = new VisualElement();
        foreach (var c in classes) el.AddToClassList(c);
        return el;
    }

    static void BuildMeander(VisualElement host)
    {
        if (host == null) return;
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
        for (int i = 0; i < MaxLevel; i++)
        {
            var pip = Div("rnk__pip");
            host.Add(pip);
            _pips.Add(pip);
        }
    }

    // ----------------------------------------------------------------- api --

    public static int CostForLevel(int level)
    {
        int i = Mathf.Clamp(level, 1, MaxLevel) - 1;
        return i < CostCurve.Length ? CostCurve[i] : CostCurve[CostCurve.Length - 1];
    }

    /// <summary>Add alloys, levelling up as many times as the deposit covers.</summary>
    public void Deposit(int amount)
    {
        if (_state == PanelState.Unpowered) return;

        _current += amount;
        while (_current >= _total && _level < MaxLevel)
        {
            _current -= _total;
            _level++;
            _total = CostForLevel(_level);
            FlashLevelUp();
        }

        if (_level >= MaxLevel)
        {
            _state = PanelState.Max;
            _current = _total;
        }

        Render();
    }

    public void SetProgress(int current, int total)
    {
        _total = Mathf.Max(1, total);
        _current = Mathf.Clamp(current, 0, _total);
        Render();
    }

    public void SetPowered(bool powered)
    {
        _state = powered
            ? (_level >= MaxLevel ? PanelState.Max : PanelState.Filling)
            : PanelState.Unpowered;
        Render();
    }

    void FlashLevelUp()
    {
        _panel.AddToClassList("is-levelup");
        _panel.schedule.Execute(() => _panel.RemoveFromClassList("is-levelup")).StartingIn(110);
    }

    // -------------------------------------------------------------- render --

    public void Render()
    {
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

        for (int i = 0; i < _pips.Count; i++)
        {
            _pips[i].EnableInClassList("is-on", i < _level);
        }
    }

    static void SetRotation(VisualElement el, float degrees)
    {
        if (el == null) return;
        el.style.rotate = new Rotate(new Angle(degrees, AngleUnit.Degree));
    }
}
