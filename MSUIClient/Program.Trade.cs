using System.Numerics;
using ImGuiNET;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _tradeOpen;
    private int _tradeMoney;

    private void DrawTradeFrame()
    {
        if(!_tradeOpen||_gameplayArt is null)return;
        float s=GameplayUiScale();Vector2 origin=new(0,8*s),logicalSize=new(384,512);
        ImGui.SetNextWindowPos(origin,ImGuiCond.Always);ImGui.SetNextWindowSize(logicalSize*s,ImGuiCond.Always);ImGui.SetNextWindowBgAlpha(0);
        if(!ImGui.Begin("##trade",ImGuiWindowFlags.NoDecoration|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoBackground|ImGuiWindowFlags.NoNav)){ImGui.End();return;}
        ImDrawListPtr dl=ImGui.GetWindowDrawList();if(_uiParityArmed&&_uiParityPanel=="trade"){BeginUiParityFrame(origin,s);CollectUiParityDraw("TradeFrame","Frame",origin,logicalSize*s,"",new("",0,"IMGUI_HOST","ANCHOR:ABSOLUTE","","",0,8));}
        (string Element,string Path,Vector2 Offset,Vector2 Size)[] art=[
            ("TradeFrame/Texture",@"Interface\TradeFrame\UI-TradeFrame-TopLeft",Vector2.Zero,new(256,256)),
            ("TradeFrame/Texture#2",@"Interface\TradeFrame\UI-TradeFrame-TopRight",new(256,0),new(128,256)),
            ("TradeFrame/Texture#3",@"Interface\TradeFrame\UI-TradeFrame-BotLeft",new(0,256),new(256,256)),
            ("TradeFrame/Texture#4",@"Interface\TradeFrame\UI-TradeFrame-BotRight",new(256,256),new(128,256))];
        foreach(var r in art){Vector2 m=origin+r.Offset*s;DrawArt(dl,r.Path,m,r.Size,s);if(_uiParityArmed&&_uiParityPanel=="trade")CollectUiParityDraw(r.Element,"Texture",m,r.Size*s,"TradeFrame",new(r.Path,0xffffffff,"IMGUI_IMAGE","TOPLEFT","TradeFrame","TOPLEFT",r.Offset.X,-r.Offset.Y));}
        ImGui.SetCursorScreenPos(origin+new Vector2(28,78)*s);ImGui.BeginChild("##trade-content",new Vector2(315,350)*s,false);
        ImGui.Columns(2,"trade-columns",true);
        ImGui.TextUnformatted(_net?.PlayerName??"Player");for(int i=0;i<7;i++)ImGui.Selectable($"Empty##player-trade-{i}");
        ImGui.NextColumn();ImGui.TextUnformatted("Trade target");for(int i=0;i<7;i++)ImGui.Selectable($"Empty##target-trade-{i}");
        ImGui.Columns(1);ImGui.InputInt("Money (copper)",ref _tradeMoney);
        if(ImGui.Button("Accept")){}ImGui.SameLine();if(ImGui.Button("Cancel"))_tradeOpen=false;
        ImGui.EndChild();Vector2 close=origin+new Vector2(326,14)*s;DrawImageButton(dl,"##trade-close",close,new Vector2(32)*s,@"Interface\Buttons\UI-Panel-MinimizeButton-Up",@"Interface\Buttons\UI-Panel-MinimizeButton-Down",@"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");if(ImGui.IsItemClicked())_tradeOpen=false;
        if(_uiParityArmed&&_uiParityPanel=="trade")MarkUiParityFrameComplete();ImGui.End();
    }
}
