using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private ulong _comboRenderedTarget;
    private byte _comboRenderedPoints;
    private double _comboShownAt;
    private readonly double[] _comboPointEarnedAt =
        Enumerable.Repeat(double.NegativeInfinity, ComboFrameUiLaw.MaximumPoints).ToArray();

    private byte VisibleComboPoints()
    {
        if (_net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return 0;
        return ComboFrameUiLaw.VisiblePoints(player.Fields.Bytes0.Class,
            player.Fields.PlayerComboPoints, player.Fields.PlayerComboTarget, _selectionGuid);
    }

    private void DrawComboFrame(ImDrawListPtr draw, Vector2 targetFrameMinimum, float scale)
    {
        byte points = VisibleComboPoints();
        ulong target = _selectionGuid;
        double now = NowSeconds();
        if (target != _comboRenderedTarget || points != _comboRenderedPoints)
        {
            byte previous = target == _comboRenderedTarget ? _comboRenderedPoints : (byte)0;
            if (points > 0 && previous == 0) _comboShownAt = now;
            for (int i = previous; i < points; i++) _comboPointEarnedAt[i] = now;
            for (int i = points; i < ComboFrameUiLaw.MaximumPoints; i++)
                _comboPointEarnedAt[i] = double.NegativeInfinity;
            _comboRenderedTarget = target;
            _comboRenderedPoints = points;
        }
        if (points == 0 || _gameplayArt is null) return;

        uint sheet = _gameplayArt.Handle(ComboFrameUiLaw.Texture);
        uint additiveSheet = _gameplayArt.AdditiveHandle(ComboFrameUiLaw.Texture);
        if (sheet == 0) return;
        float frameAlpha = ComboFrameUiLaw.FrameAlpha(now, _comboShownAt);

        static uint White(float alpha) => ImGui.ColorConvertFloat4ToU32(
            new Vector4(1, 1, 1, Math.Clamp(alpha, 0, 1)));

        for (int i = 0; i < ComboFrameUiLaw.MaximumPoints; i++)
        {
            Vector2 pointMin = targetFrameMinimum + ComboFrameUiLaw.PointMinimum(i) * scale;
            draw.AddImage((nint)sheet, pointMin,
                pointMin + ComboFrameUiLaw.SocketSize * scale,
                ComboFrameUiLaw.SocketUvMin, ComboFrameUiLaw.SocketUvMax, White(frameAlpha));
            if (i >= points) continue;

            float highlightAlpha = frameAlpha * ComboFrameUiLaw.HighlightAlpha(
                now, _comboPointEarnedAt[i]);
            Vector2 highlightMin = pointMin + ComboFrameUiLaw.HighlightOffset * scale;
            draw.AddImage((nint)sheet, highlightMin,
                highlightMin + ComboFrameUiLaw.HighlightSize * scale,
                ComboFrameUiLaw.HighlightUvMin, ComboFrameUiLaw.HighlightUvMax,
                White(highlightAlpha));

            float shineAlpha = frameAlpha * ComboFrameUiLaw.ShineAlpha(now, _comboPointEarnedAt[i]);
            if (additiveSheet == 0 || shineAlpha <= 0) continue;
            Vector2 shineMin = pointMin + ComboFrameUiLaw.ShineOffset * scale;
            draw.AddImage((nint)additiveSheet, shineMin,
                shineMin + ComboFrameUiLaw.ShineSize * scale,
                ComboFrameUiLaw.ShineUvMin, ComboFrameUiLaw.ShineUvMax, White(shineAlpha));
        }
    }
}
