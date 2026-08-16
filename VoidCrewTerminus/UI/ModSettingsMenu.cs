using System;
using System.Collections.Generic;
using System.Linq;
using static UnityEngine.GUILayout;
using VoidManager.CustomGUI;
using UnityEngine;
using VoidCrewTerminus.Commands;

namespace VoidCrewTerminus;

class TerminusModMenu : ModSettingsMenu
{
    readonly GUILayoutOption[] labelWidth = { MaxWidth(5) };

    int selectedTab = 0;
    string[] tabs = { "General", "Lobby Ships", "Upgrade Foge", "Gameplay", "Spawn" };

    string spawnFilter = string.Empty;
    bool hideLockedSpawnables = true;
    Vector2 spawnScrollPosition;
    List<SpawnItemCommand.SpawnableCarryable> cachedCarryables = new();

    public override string Name()
    {
        return "Terminus";
    }

    public override void Draw()
    {
        var previousTab = selectedTab;
        selectedTab = Toolbar(selectedTab, tabs);

        switch (selectedTab)
        {
            case 0:
                renderGeneralTab();
                break;
            case 1:
                renderLobbyShipTabs();
                break;
            case 2:
                renderUpgradeForgeTab();
                break;
            case 3:
                renderGameplayTab();
                break;
            case 4:
                if (previousTab != 4) RefreshCarryables();
                renderSpawnTab();
                break;
        }
    }

    public override void OnClose()
    {
        base.OnClose();
    }

    public override void OnOpen()
    {
        base.OnOpen();
    }

    private void renderGeneralTab()
    {
        TerminusConfig.AllowRelicReplication.Value = Toggle(TerminusConfig.AllowRelicReplication.Value, "Allow Relic replication in fabractor");
        // Writes go through the ConfigEntry — TerminusConfig's accessors are read-only.
        TerminusConfig.EnableDevMode.Value = Toggle(TerminusConfig.EnableDevMode.Value, "Enable dev mode, allows for spawning items amoung other things");


        Label("Credits");

        BeginVertical();
        Label("==== Playtesters =====");

        Label("- Diamondrunner1");
        Label("- Mark (TODO replace this with username)");
        EndVertical();
    }

    private void renderUpgradeForgeTab()
    {
        BeginHorizontal();
        Label("Upgrade cost curve", labelWidth);
        TerminusConfig.ForgeCostCurve.Value = TextField(TerminusConfig.ForgeCostCurve.Value);
        EndHorizontal();
    }

    private void renderLobbyShipTabs()
    {
        Label("Fade");
        var fadeS = TerminusConfig.LobbyShipFadeDuration.Value.ToString();
        var fade = TextField(fadeS);
        if (fade != fadeS)
        {
            try
            {
                TerminusConfig.LobbyShipFadeDuration.Value = float.Parse(fade);
            }
            catch (System.Exception ex)
            {
                BepinPlugin.Log.LogError(ex);
            }

        }

        Label("Striker");
        TerminusConfig.StrikerLobbyHangerPosition.Value = InputGroup(TerminusConfig.StrikerLobbyHangerPosition.Value);
        Label("Striker Rot");
        TerminusConfig.StrikerLobbyHangerRot.Value = InputGroup(TerminusConfig.StrikerLobbyHangerRot.Value);

        Label("Frigate");
        TerminusConfig.FrigateLobbyHangerPosition.Value = InputGroup(TerminusConfig.FrigateLobbyHangerPosition.Value);
        Label("Frigate Rot");
        TerminusConfig.FrigateLobbyHangerRot.Value = InputGroup(TerminusConfig.FrigateLobbyHangerRot.Value);

        Label("Destroyer");
        TerminusConfig.DestroyerLobbyHangerPosition.Value = InputGroup(TerminusConfig.DestroyerLobbyHangerPosition.Value);
        Label("Destroyer Rot");
        TerminusConfig.DestroyerLobbyHangerRot.Value = InputGroup(TerminusConfig.DestroyerLobbyHangerRot.Value);
    }

    private void renderGameplayTab() { }

    private void renderSpawnTab()
    {
        if (!TerminusConfig.DevMode)
        {
            Label("Enable Dev Mode in the General tab to spawn items.");
            return;
        }

        BeginHorizontal();
        Label("Filter", labelWidth);
        spawnFilter = TextField(spawnFilter);
        if (Button("Refresh", MaxWidth(70))) RefreshCarryables();
        EndHorizontal();

        hideLockedSpawnables = Toggle(hideLockedSpawnables, "Hide Locked/Debug Items");

        var matches = cachedCarryables
            .Where(c => !hideLockedSpawnables || !c.IsLocked)
            .Where(c => string.IsNullOrEmpty(spawnFilter) || c.Name.IndexOf(spawnFilter, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();

        Label($"{matches.Count} item(s)");

        spawnScrollPosition = BeginScrollView(spawnScrollPosition);
        foreach (var item in matches)
        {
            if (Button(item.Name, Height(30)))
            {
                SpawnItemCommand.TrySpawnAtPlayer(item, out var message);
                BepinPlugin.Log.LogInfo($"[Spawn] {message}");
            }
        }
        EndScrollView();
    }

    private void RefreshCarryables()
    {
        cachedCarryables = SpawnItemCommand.GetCarryables();
    }

    private int IntInput(int value)
    {
        try
        {
            var result = TextField(value.ToString());
            return int.Parse(result);
        }
        catch (System.Exception ex)
        {
            BepinPlugin.Log.LogError(ex);
            return value;
        }
    }
    private float FloatInput(float value)
    {
        try
        {
            var result = TextField(value.ToString());
            return float.Parse(result);
        }
        catch (System.Exception ex)
        {
            BepinPlugin.Log.LogError(ex);
            return value;
        }
    }

    private Vector3 InputGroup(Vector3 source)
    {
        var sX = source.x.ToString();
        var sY = source.y.ToString();
        var sZ = source.z.ToString();

        BeginHorizontal();

        BeginHorizontal();
        Label("X", labelWidth);
        var x = TextField(sX);
        EndHorizontal();

        BeginHorizontal();
        Label("Y", labelWidth);
        var y = TextField(sY);
        EndHorizontal();

        BeginHorizontal();
        Label("Z", labelWidth);
        var z = TextField(sZ);
        EndHorizontal();

        EndHorizontal();
        if (sX != x || sY != y || sZ != z)
        {
            try
            {
                var rx = float.Parse(x);
                var ry = float.Parse(y);
                var rz = float.Parse(z);

                return new Vector3(rx, ry, rz);
            }
            catch (System.Exception ex)
            {
                BepinPlugin.Log.LogError(ex);
                return source;
            }
        }

        return source;
    }
}