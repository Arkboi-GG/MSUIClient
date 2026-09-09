using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private WorldStateUiCatalog? _worldStateUiCatalog;
    private bool _worldStateUiCatalogLoaded;
    private readonly Dictionary<uint,uint> _worldStateUiValues = [];
    private ulong _worldStateUiOwner;
    private uint _worldStateUiMap, _worldStateUiZone;
    private readonly Dictionary<uint, (float Position, int Direction)> _worldStateCapturePositions = [];
    private IReadOnlyList<WorldStateUiLaw.Display>? _worldStateUiDisplay;

    private void ClearWorldStateUi()
    {
        _worldStateUiOwner = 0;
        _worldStateUiValues?.Clear();
        _worldStateUiDisplay = null;
        _worldStateCapturePositions?.Clear();
    }
    private void SetWorldStateUiContext(uint map, uint zone, IReadOnlyList<(uint Id, uint Value)> values)
    {
        ClearWorldStateUi();
        _worldStateUiOwner = LocalPlayerGuid; _worldStateUiMap = map; _worldStateUiZone = zone;
        foreach (var pair in values) _worldStateUiValues[pair.Id] = pair.Value;
    }
    private void ApplyWorldStateUiValue(uint id, uint value)
    {
        if (_worldStateUiOwner == 0) return;
        _worldStateUiValues[id] = value;
        _worldStateUiDisplay = null;
    }
    private bool WorldStateUiCurrent() => _net is { IsInWorld: true } && !_worldLoading &&
        _worldStateUiOwner != 0 && _worldStateUiOwner == LocalPlayerGuid && ControlledGuid == LocalPlayerGuid &&
        _worldStateUiMap == _config.Start.Map;

    private void EnsureWorldStateUiCatalog()
    {
        if (!_worldStateUiCatalogLoaded && _mpq is not null)
        { _worldStateUiCatalogLoaded = true; _worldStateUiCatalog = WorldStateUiCatalog.Load(_mpq); }
    }

    private IReadOnlyList<WorldStateUiLaw.Display> WorldStateUiDisplays()
    {
        if (!WorldStateUiCurrent()) return [];
        EnsureWorldStateUiCatalog();
        if (_worldStateUiCatalog is null) return [];
        if (_worldStateUiDisplay is not null) return _worldStateUiDisplay;
        var display = WorldStateUiLaw.Evaluate(_worldStateUiCatalog,_worldStateUiMap,_worldStateUiZone,_worldStateUiValues);
        foreach (var item in display.Where(item => item.Row.ExtendedUi == "CAPTUREPOINT"))
        {
            float position = WorldStateUiLaw.CaptureIndicator(item.CaptureValue);
            int direction = _worldStateCapturePositions.TryGetValue(item.Row.Id,out var old)
                ? Math.Sign(position - old.Position) : 0;
            _worldStateCapturePositions[item.Row.Id] = (position,direction);
        }
        var current = display.Select(item => item.Row.Id).ToHashSet();
        foreach (uint id in _worldStateCapturePositions.Keys.Where(id => !current.Contains(id)).ToArray())
            _worldStateCapturePositions.Remove(id);
        return _worldStateUiDisplay = display;
    }

    private void DrawWorldStateUi()
    {
        var display = WorldStateUiDisplays();
        if (display.Count == 0 || _gameplayArt is null) return;
        float scale = GameplayUiScale(); var draw = ImGui.GetBackgroundDrawList(); int index = 0;
        foreach (var item in display)
        {
            if (item.Row.ExtendedUi.Length != 0) continue;
            Vector2 min = WorldStateUiLaw.AlwaysUpMin(ImGui.GetIO().DisplaySize,scale,index++);
            void Image(string path, Vector2 offset, float size, bool additive = false, uint tint = 0xffffffff)
            {
                if (path.Length == 0) return;
                uint texture = additive ? _gameplayArt.AdditiveHandle(path) : _gameplayArt.Handle(path);
                Vector2 start = min + offset * scale;
                if (texture != 0) draw.AddImage((nint)texture,start,start+new Vector2(size)*scale,Vector2.Zero,Vector2.One,tint);
            }
            Image(item.Row.Icon,new(-6,-9),42);
            GameText.Draw(draw,"GameFontNormalSmall",item.Text,
                min + new Vector2(24,2)*scale - new Vector2(0,GameText.LinePitch("GameFontNormalSmall",scale)*.5f),scale);
            if (item.State == 2)
            {
                Image(item.Row.DynamicIcon,new(53,-13),32);
                uint alpha = (uint)(Math.Clamp(1-Math.Abs((NowSeconds()%1)*2-1),0,1)*255);
                if (item.Row.DynamicIcon.Length != 0) Image(item.Row.DynamicIcon+"Flash",new(53,-13),32,true,(alpha<<24)|0xffffff);
                if (ImGui.IsMouseHoveringRect(min+new Vector2(53,-13)*scale,min+new Vector2(85,19)*scale,false))
                    OfferOwnerAnchoredSharedGameTooltip(new("world-state-dynamic",item.Row.Id),
                        [new(item.DynamicTooltip,GameTooltipTextTone.White)],min+new Vector2(53,19)*scale,Vector2.Zero);
            }
            if (item.Tooltip.Length != 0 && ImGui.IsMouseHoveringRect(min,min+new Vector2(45,24)*scale,false))
                OfferOwnerAnchoredSharedGameTooltip(new("world-state",item.Row.Id),
                    [new(item.Tooltip,GameTooltipTextTone.White)],min+new Vector2(0,24)*scale,Vector2.Zero);
        }
    }

    private void DrawWorldStateCaptureBars(ImDrawListPtr draw, Vector2 minimapRoot, float scale)
    {
        if (_gameplayArt is null) return;
        int index = 0;
        foreach (var item in WorldStateUiDisplays())
        {
            if (item.Row.ExtendedUi != "CAPTUREPOINT") continue;
            Vector2 min = minimapRoot + new Vector2(19,177 + 28 * index++)*scale;
            const string path = @"Interface\WorldStateFrame\WorldState-CaptureBar";
            void Slice(float x,float y,float width,float height,float u,float v,float right,float bottom,bool additive=false)
            {
                uint texture = additive ? _gameplayArt.AdditiveHandle(path) : _gameplayArt.Handle(path);
                if (texture != 0) draw.AddImage((nint)texture,min+new Vector2(x,y)*scale,
                    min+new Vector2(x+width,y+height)*scale,new(u,v),new(right,bottom));
            }
            Slice(26,8.5f,48,9,.8203125f,0,1,.140625f);
            Slice(99,8.5f,48,9,.8203125f,.171875f,1,.3125f);
            Slice(74,8.5f,25,9,.8203125f,.34375f,1,.484375f);
            Slice(0,0,173,26,0,0,.67578125f,.40625f);
            Slice(73,9,3,8,.74609375f,0,.7578125f,.125f);
            Slice(97,9,3,8,.74609375f,0,.7578125f,.125f);
            if (WorldStateUiLaw.CaptureAllianceHighlight(item.CaptureValue)) Slice(-1,-1,27,28,0,.4375f,.10546875f,.875f,true);
            if (WorldStateUiLaw.CaptureHordeHighlight(item.CaptureValue)) Slice(147,-1,27,28,0,.4375f,.10546875f,.875f,true);
            var indicator = _worldStateCapturePositions.GetValueOrDefault(item.Row.Id);
            float center = indicator.Position;
            if (indicator.Direction < 0) Slice(center-9.5f,5.5f,8,15,.7265625f,.140625f,.76171875f,.375f);
            if (indicator.Direction > 0) Slice(center+1.5f,5.5f,8,15,.76171875f,.140625f,.7265625f,.375f);
            Slice(center-2.5f,4,5,18,.77734375f,0,.796875f,.28125f);
            if (item.Tooltip.Length != 0 && ImGui.IsMouseHoveringRect(min,min+new Vector2(173,26)*scale,false))
                OfferOwnerAnchoredSharedGameTooltip(new("world-state-capture",item.Row.Id),
                    [new(item.Tooltip,GameTooltipTextTone.White)],min+new Vector2(0,26)*scale,Vector2.Zero);
        }
    }
}
