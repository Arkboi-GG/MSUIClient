using System.Numerics;
using ImGuiNET;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private ulong _unitPopupGuid;
    private Vector2 _unitPopupPosition;

    private void DrawUnitPopup()
    {
        if (_unitPopupGuid == 0 || _gameplayArt is null) return;
        float s = GameplayUiScale();
        Vector2 logical = _unitPopupPosition / s;
        if (!BeginVanillaWindow("##unit-popup", logical, new Vector2(128, 141),
                out ImDrawListPtr dl, out Vector2 origin, out s)) { ImGui.End(); return; }
        dl.AddRectFilled(origin, origin + new Vector2(128, 141) * s, 0xee080808, 4 * s);
        dl.AddRect(origin, origin + new Vector2(128, 141) * s, 0xffb08040, 4 * s, ImDrawFlags.None, s);
        DrawCenteredText(dl, origin + new Vector2(64, 16) * s,
            _playerNames.GetValueOrDefault(_unitPopupGuid, "Player"), 10f * s, VanillaGold);
        if (VanillaButton(dl, "##unit-trade", "Trade", origin + new Vector2(14, 34) * s,
                new Vector2(100, 22), s))
        {
            _tradePartnerGuid = _unitPopupGuid;
            _net?.InitiateTrade(_unitPopupGuid);
            _unitPopupGuid = 0;
        }
        if (VanillaButton(dl, "##unit-follow", "Follow", origin + new Vector2(14, 59) * s,
                new Vector2(100, 22), s, false)) { }
        if (VanillaButton(dl, "##unit-inspect", "Inspect", origin + new Vector2(14, 84) * s,
                new Vector2(100, 22), s))
        {
            _net?.Inspect(_unitPopupGuid);
            _unitPopupGuid = 0;
        }
        if (VanillaButton(dl, "##unit-cancel", "Cancel", origin + new Vector2(14, 109) * s,
                new Vector2(100, 22), s)) _unitPopupGuid = 0;
        ImGui.End();
    }
}
