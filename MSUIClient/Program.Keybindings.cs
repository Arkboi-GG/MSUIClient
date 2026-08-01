using System.Numerics;
using ImGuiNET;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _keybindingsOpen;

    private void DrawKeybindingsFrame()
    {
        if(!_keybindingsOpen||_gameplayArt is null)return;
        float s=GameplayUiScale();Vector2 origin=new(0,8*s),logicalSize=new(640,512);
        ImGui.SetNextWindowPos(origin,ImGuiCond.Always);ImGui.SetNextWindowSize(logicalSize*s,ImGuiCond.Always);ImGui.SetNextWindowBgAlpha(0);
        if(!ImGui.Begin("##keybindings",ImGuiWindowFlags.NoDecoration|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoBackground|ImGuiWindowFlags.NoNav)){ImGui.End();return;}
        ImDrawListPtr dl=ImGui.GetWindowDrawList();if(_uiParityArmed&&_uiParityPanel=="keybindings"){BeginUiParityFrame(origin,s);CollectUiParityDraw("KeyBindingFrame","Button",origin,logicalSize*s,"",new("",0,"IMGUI_HOST","ANCHOR:ABSOLUTE","","",0,8));}
        (string Element,string Path,Vector2 Offset,Vector2 Size)[] art=[
            ("KeyBindingFrame/Texture",@"Interface\KeyBindingFrame\UI-KeyBindingFrame-TopLeft",new(0,0),new(256,256)),
            ("KeyBindingFrame/Texture#2",@"Interface\KeyBindingFrame\UI-KeyBindingFrame-Top",new(256,0),new(256,256)),
            ("KeyBindingFrame/Texture#3",@"Interface\KeyBindingFrame\UI-KeyBindingFrame-TopRight",new(512,0),new(128,256)),
            ("KeyBindingFrame/Texture#4",@"Interface\KeyBindingFrame\UI-KeyBindingFrame-BotLeft",new(0,256),new(256,256)),
            ("KeyBindingFrame/Texture#5",@"Interface\KeyBindingFrame\UI-KeyBindingFrame-Bot",new(256,256),new(256,256)),
            ("KeyBindingFrame/Texture#6",@"Interface\KeyBindingFrame\UI-KeyBindingFrame-BotRight",new(512,256),new(128,256))];
        foreach(var r in art){Vector2 m=origin+r.Offset*s;DrawArt(dl,r.Path,m,r.Size,s);if(_uiParityArmed&&_uiParityPanel=="keybindings")CollectUiParityDraw(r.Element,"Texture",m,r.Size*s,"KeyBindingFrame",new(r.Path,0xffffffff,"IMGUI_IMAGE","TOPLEFT","KeyBindingFrame","TOPLEFT",r.Offset.X,-r.Offset.Y));}
        ImGui.SetCursorScreenPos(origin+new Vector2(35,55)*s);ImGui.BeginChild("##binding-list",new Vector2(540,360)*s,false);
        string[] rows=["Movement","Move Forward — W","Move Backward — S","Turn Left — A","Turn Right — D","Jump — Space","Targeting","Target Nearest Enemy — Tab","Interface","Open Game Menu — Escape","Open Backpack — B","Character Info — C"];
        foreach(string row in rows)ImGui.Selectable(row);
        ImGui.EndChild();ImGui.SetCursorScreenPos(origin+new Vector2(440,430)*s);if(ImGui.Button("Okay"))_keybindingsOpen=false;ImGui.SameLine();if(ImGui.Button("Cancel"))_keybindingsOpen=false;
        if(_uiParityArmed&&_uiParityPanel=="keybindings")MarkUiParityFrameComplete();ImGui.End();
    }
}
