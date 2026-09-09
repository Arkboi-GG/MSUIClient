using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool BattlefieldMapHasPlayer(ulong guid) => BattlefieldPositionsCurrent() &&
        (_battlefieldPositions!.Teammates.Any(p => p.Guid == guid) || _battlefieldPositions.FriendlyFlagCarrier?.Guid == guid);

    private void DrawBattlefieldMapPositions(ImDrawListPtr draw, bool haveMapArea, WorldMapAreaInfo area,
        uint playerMap, Vector2 mapMin, Vector2 mapSize, float scale)
    {
        if (!haveMapArea || !BattlefieldPositionsCurrent() || _battlefieldPositions is not { } positions ||
            _battlefieldPositionsScope?.Map != playerMap) return;
        void Marker(BattlefieldPosition observation, bool flag)
        {
            if (!flag && (observation.Guid == ControlledGuid || positions.FriendlyFlagCarrier?.Guid == observation.Guid)) return;
            var position = new Vector3(observation.Position, 0);
            if (!WorldMapUiLaw.TryPlayerMarker(playerMap, area.MapId, position, area.Left, area.Right,
                area.Top, area.Bottom, mapMin, mapSize, out Vector2 center)) return;
            string path = flag ? BattlefieldRaceTeam(ControlledGuid) switch
            {
                RaceTeam.Alliance => @"Interface\WorldStateFrame\HordeFlag",
                RaceTeam.Horde => @"Interface\WorldStateFrame\AllianceFlag",
                _ => @"Interface\BattlefieldFrame\UI-Battlefield-Icon",
            } : @"Interface\WorldMap\WorldMapPartyIcon";
            Vector2 size = new Vector2(flag ? 24 : 16) * scale, min = center - size * .5f;
            uint texture = _gameplayArt?.Handle(path) ?? 0;
            if (texture != 0) draw.AddImage((nint)texture, min, min + size);
            else draw.AddCircleFilled(center,3*scale,0xff00d7ff);
            if (ImGui.IsMouseHoveringRect(min,min+size,false))
            {
                string name = _playerNames.GetValueOrDefault(observation.Guid,"Player");
                if (string.IsNullOrEmpty(name)) name = "Player";
                var seat = WorldMapUiLaw.CorpseTooltipSeat(min,size,mapMin,mapSize);
                OfferOwnerAnchoredSharedGameTooltip(new("world-map-battlefield",observation.Guid),
                    [new(flag ? $"{name} (friendly flag carrier)" : name, GameTooltipTextTone.White)],seat.Anchor,seat.Pivot);
            }
        }
        foreach (var teammate in positions.Teammates) Marker(teammate,false);
        if (positions.FriendlyFlagCarrier is { } carrier) Marker(carrier,true);
    }
}
