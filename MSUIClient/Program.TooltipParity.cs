using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _tooltipParityOpen;

    private void DrawTooltipParityFrame()
    {
        if(!_tooltipParityOpen||_skin is null)return;
        float s=GameplayUiScale();Vector2 origin=new(100*s,100*s),logicalSize=new(220,80),size=logicalSize*s;
        ImGui.SetNextWindowPos(origin,ImGuiCond.Always);ImGui.SetNextWindowSize(size,ImGuiCond.Always);ImGui.SetNextWindowBgAlpha(0);
        if(!ImGui.Begin("##tooltip-parity",ImGuiWindowFlags.NoDecoration|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoBackground|ImGuiWindowFlags.NoNav)){ImGui.End();return;}
        ImDrawListPtr dl=ImGui.GetWindowDrawList();BeginUiParityFrame(origin,s);
        CollectUiParityDraw("GameTooltip","GameTooltip",origin,size,"",new("",0,"IMGUI_HOST","ANCHOR:ABSOLUTE","","",100,100));
        dl.PushClipRectFullScreen();_skin.DrawBackdrop(dl,origin,origin+size,WowSkin.Tooltip);dl.PopClipRect();
        ImGui.SetCursorScreenPos(origin+new Vector2(10,9)*s);
        ImGui.TextColored(new Vector4(1,.82f,0,1),"Breastplate of Might");
        ImGui.TextUnformatted("Binds when picked up");ImGui.TextColored(new Vector4(0.12f,1,0,1),"Plate  •  749 Armor");
        if(_uiParityArmed&&_uiParityPanel=="tooltip")MarkUiParityFrameComplete();ImGui.End();
    }
}
