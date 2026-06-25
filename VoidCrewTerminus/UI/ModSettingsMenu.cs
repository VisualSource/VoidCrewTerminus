using static UnityEngine.GUILayout;
using VoidManager.CustomGUI;

namespace VoidCrewTerminus;

class TerminusModMenu : ModSettingsMenu
{

    public override string Name()
    {
        return "Terminus";
    }

    public override void Draw()
    {
        TerminusConfig.AllowRelicReplication.Value = Toggle(TerminusConfig.AllowRelicReplication.Value, "Allow Relic replication in fabractor");
        TerminusConfig.EnableDevMode.Value = Toggle(TerminusConfig.EnableDevMode.Value, "Enable dev mode, allows for spawning items amoung other things");
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