using static UnityEngine.GUILayout;
using VoidManager.CustomGUI;

namespace VoidCrewTerminus;

class TerminusModMenu : ModSettingsMenu
{
    bool ToggleBool = false;

    public override string Name()
    {
        return "Terminus";
    }

    public override void Draw()
    {
        Label("Terminus");

        ToggleBool = Toggle(ToggleBool, "Label");
    }

    public override void OnClose()
    {
        base.OnClose();
    }

    public override void OnOpen()
    {
        base.OnOpen();
    }
}