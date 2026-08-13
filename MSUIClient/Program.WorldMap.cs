using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _worldMapOpen;
    private bool _worldMapKeyWasDown;
    private int _worldMapZoom = 1;
    private WorldMapAreaCatalog? _worldMapAreas;
    private bool _worldMapAreasLoaded;

    private void UpdateWorldMapInput(bool typing)
    {
        bool down = BindingDown(GameBinding.OpenWorldMap);
        if (down && !_worldMapKeyWasDown && !typing && _net is { IsInWorld: true })
        {
            // In the free view the same binding opens the RTS commander map; the
            // vanilla map stays exactly what it always was everywhere else.
            if (_freeView) ToggleCommanderMap();
            else _worldMapOpen = !_worldMapOpen;
        }
        _worldMapKeyWasDown = down;
    }

    private void DrawWorldMapFrame()
    {
        if (!_worldMapOpen || _gameplayArt is null || _net is null ||
            !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return;
        float s = MathF.Min(GameplayUiScale(),MathF.Min(ImGui.GetIO().DisplaySize.X/1024f,ImGui.GetIO().DisplaySize.Y/768f));
        Vector2 logicalSize = new(1024,768);
        Vector2 logicalOrigin=(ImGui.GetIO().DisplaySize/s-logicalSize)*.5f;
        if (!BeginVanillaWindow("##world-map", logicalOrigin, logicalSize,
                out ImDrawListPtr dl, out Vector2 origin, out s)) { ImGui.End(); return; }
        // FULLSCREEN_DIALOG strata: submit on the foreground list so both the
        // letterbox and authored map cover every ordinary or developer window.
        dl = ImGui.GetForegroundDrawList();
        dl.AddRectFilled(Vector2.Zero,ImGui.GetIO().DisplaySize,0xff000000);
        string[] shellRows=["Top","Middle","Bottom"];
        for(int row=0;row<3;row++)for(int col=0;col<4;col++)
            DrawArt(dl,$@"Interface\WorldMap\UI-WorldMap-{shellRows[row]}{col+1}",
                origin+new Vector2(col*256,row*256)*s,new Vector2(256),s);
        EnsureWorldMapAreas();
        uint mapId=_net.Player?.Map??0;
        uint zoneId=_areas?.ParentZoneId(_minimapAreaId)??0;
        if(zoneId==0)zoneId=_net.Player?.Zone??0;
        WorldMapAreaInfo area=default;
        bool haveArea=_worldMapZoom>0&&_worldMapAreas?.TryGetArea(zoneId,out area)==true;
        if(!haveArea)_worldMapAreas?.TryGetContinent(mapId,out area);
        string directory=string.IsNullOrWhiteSpace(area.Directory)?(mapId==1?"Kalimdor":"Azeroth"):area.Directory;
        Vector2 mapMin=origin+new Vector2(11,69)*s,mapSize=new Vector2(1002,668)*s;
        dl.PushClipRect(mapMin,mapMin+mapSize,true);
        for(int row=0;row<3;row++)for(int col=0;col<4;col++)
        {
            int index=row*4+col+1;uint texture=_gameplayArt.Handle($@"Interface\WorldMap\{directory}\{directory}{index}.blp");
            if(texture!=0){Vector2 min=mapMin+new Vector2(col*256,row*256)*s;dl.AddImage((nint)texture,min,min+new Vector2(256)*s);}
        }
        dl.PopClipRect();
        if(area.Directory.Length>0)
        {
            Vector2 marker=mapMin+new Vector2(area.X(player.Position.Y)*1002,area.Y(player.Position.X)*668)*s;
            uint arrow=_gameplayArt.Handle(@"Interface\Minimap\MinimapArrow.mdx");
            dl.AddCircleFilled(marker,6*s,0xffffffff);dl.AddCircle(marker,7*s,0xff202020,0,2*s);
        }
        DrawCenteredText(dl,origin+new Vector2(512,17)*s,"World Map",14*s,VanillaGold);
        DrawCenteredText(dl,origin+new Vector2(512,48)*s,
            haveArea?(_areas?.ZoneName(zoneId)??directory):(mapId==1?"Kalimdor":"Eastern Kingdoms"),12*s,0xffffffff);
        if(haveArea&&VanillaButton(dl,"##world-map-zoomout","Zoom Out",origin+new Vector2(680,34)*s,new Vector2(110,22),s))_worldMapZoom=0;
        else if(!haveArea&&VanillaButton(dl,"##world-map-zone","Current Zone",origin+new Vector2(680,34)*s,new Vector2(110,22),s))_worldMapZoom=1;
        Vector2 close = origin + new Vector2(982,4) * s;
        DrawImageButton(dl, "##world-map-close", close, new Vector2(32) * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up", @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) _worldMapOpen = false;
        ImGui.End();
    }

    private void EnsureWorldMapAreas()
    {
        if(_worldMapAreasLoaded)return;_worldMapAreasLoaded=true;
        try{if(_mpq is not null)_worldMapAreas=WorldMapAreaCatalog.Load(_mpq);}
        catch(Exception e){Console.WriteLine($"[world-map] WorldMapArea load failed: {e.Message}");}
    }
}
