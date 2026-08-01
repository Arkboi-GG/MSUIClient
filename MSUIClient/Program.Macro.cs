using System.Numerics;
using ImGuiNET;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _macroOpen;
    private readonly byte[] _macroName=new byte[32];
    private string _macroBody="";

    private void DrawMacroFrame()
    {
        if(!_macroOpen||_gameplayArt is null)return;
        float s=GameplayUiScale();Vector2 origin=new(0,8*s),logicalSize=new(384,512);
        ImGui.SetNextWindowPos(origin,ImGuiCond.Always);ImGui.SetNextWindowSize(logicalSize*s,ImGuiCond.Always);ImGui.SetNextWindowBgAlpha(0);
        if(!ImGui.Begin("##macro",ImGuiWindowFlags.NoDecoration|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoBackground|ImGuiWindowFlags.NoNav)){ImGui.End();return;}
        ImDrawListPtr dl=ImGui.GetWindowDrawList();if(_uiParityArmed&&_uiParityPanel=="macro"){BeginUiParityFrame(origin,s);CollectUiParityDraw("MacroFrame","Frame",origin,logicalSize*s,"",new("",0,"IMGUI_HOST","ANCHOR:ABSOLUTE","","",0,8));}
        (string Element,string Path,Vector2 Offset,Vector2 Size)[] art=[
            ("MacroFramePortrait",@"Interface\MacroFrame\MacroFrame-Icon",new(7,6),new(60,60)),
            ("MacroFrame/Texture",@"Interface\PaperDollInfoFrame\UI-Character-General-TopLeft",Vector2.Zero,new(256,256)),
            ("MacroFrame/Texture#2",@"Interface\PaperDollInfoFrame\UI-Character-General-TopRight",new(256,0),new(128,256)),
            ("MacroFrame/Texture#3",@"Interface\MacroFrame\MacroFrame-BotLeft",new(0,256),new(256,256)),
            ("MacroFrame/Texture#4",@"Interface\MacroFrame\MacroFrame-BotRight",new(256,256),new(128,256))];
        foreach(var r in art){Vector2 m=origin+r.Offset*s;DrawArt(dl,r.Path,m,r.Size,s);if(_uiParityArmed&&_uiParityPanel=="macro")CollectUiParityDraw(r.Element,"Texture",m,r.Size*s,"MacroFrame",new(r.Path,0xffffffff,"IMGUI_IMAGE","TOPLEFT","MacroFrame","TOPLEFT",r.Offset.X,-r.Offset.Y));}
        ImGui.SetCursorScreenPos(origin+new Vector2(30,72)*s);ImGui.BeginChild("##macro-content",new Vector2(305,360)*s,false);
        ImGui.TextUnformatted("General Macros");for(int i=0;i<6;i++){if(i>0)ImGui.SameLine();ImGui.Button($" {i+1} ##macro-slot-{i}",new Vector2(36)*s);}
        ImGui.InputText("Name",_macroName,(uint)_macroName.Length);
        ImGui.InputTextMultiline("Macro text",ref _macroBody,255,new Vector2(275,150)*s);
        if(ImGui.Button("New")){Array.Clear(_macroName);_macroBody="";}ImGui.SameLine();if(ImGui.Button("Delete")){Array.Clear(_macroName);_macroBody="";}
        ImGui.EndChild();Vector2 close=origin+new Vector2(326,14)*s;DrawImageButton(dl,"##macro-close",close,new Vector2(32)*s,@"Interface\Buttons\UI-Panel-MinimizeButton-Up",@"Interface\Buttons\UI-Panel-MinimizeButton-Down",@"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");if(ImGui.IsItemClicked())_macroOpen=false;
        if(_uiParityArmed&&_uiParityPanel=="macro")MarkUiParityFrameComplete();ImGui.End();
    }
}
