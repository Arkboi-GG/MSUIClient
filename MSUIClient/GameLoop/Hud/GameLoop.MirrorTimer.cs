using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

/// <summary>MirrorTimer1/2/3: server-authoritative breath, fatigue, and feign-death bars.</summary>
public sealed partial class GameLoop
{
    private readonly MirrorTimerState _mirrorTimers = new();

    private void ResetMirrorTimers() => _mirrorTimers.Clear();

    private void ApplyMirrorTimerStart(byte[] body)
    {
        MirrorTimerStart packet = MirrorTimerPackets.ParseStart(body);
        MirrorTimerState.ActiveTimer? timer = _mirrorTimers.Start(packet, NowSeconds());
        EmitInterface("mirror-timer", "start", timer is null ? "UNSHOWN" : "RESTATED",
            packet.SpellId,
            $"kind={packet.RawKind};remainingMs={packet.RemainingMs};" +
            $"durationMs={packet.DurationMs};scale={packet.Scale};paused={packet.Paused}");
    }

    private void ApplyMirrorTimerPause(byte[] body)
    {
        (uint rawKind, bool paused) = MirrorTimerPackets.ParsePause(body);
        bool applied = _mirrorTimers.Pause(rawKind, paused, NowSeconds());
        EmitInterface("mirror-timer", "pause", applied ? "APPLIED" : "IGNORED", rawKind,
            $"paused={paused}");
    }

    private void ApplyMirrorTimerStop(byte[] body)
    {
        uint rawKind = MirrorTimerPackets.ParseStop(body);
        bool stopped = _mirrorTimers.Stop(rawKind);
        EmitInterface("mirror-timer", "stop", stopped ? "HIDDEN" : "IGNORED", rawKind,
            "bodyBytes=4");
    }

    private void DrawMirrorTimerFrames()
    {
        if (_gameplayArt is null) return;
        float scale = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize;
        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
        double now = NowSeconds();
        uint status = _gameplayArt.Handle(MirrorTimerUiLaw.StatusTexture);
        uint border = _gameplayArt.Handle(MirrorTimerUiLaw.BorderTexture);

        for (int index = 0; index < MirrorTimerState.FrameCount; index++)
        {
            MirrorTimerState.ActiveTimer? timer = _mirrorTimers.Frames[index];
            if (timer is null) continue;
            MirrorTimerUiLaw.ScreenRect frame = MirrorTimerUiLaw.FrameRect(
                display, scale, index);
            Vector2 barMin = frame.Min + MirrorTimerUiLaw.BarMin * scale;
            Vector2 barSize = new(MirrorTimerUiLaw.BarWidth * scale,
                MirrorTimerUiLaw.BarHeight * scale);
            draw.AddRectFilled(barMin, barMin + barSize, 0x80000000);

            float fraction = MirrorTimerState.FractionAt(timer, now);
            if (status != 0 && fraction > 0)
            {
                Vector2 fillSize = new(barSize.X * fraction, barSize.Y);
                uint color = ImGui.ColorConvertFloat4ToU32(MirrorTimerUiLaw.Color(timer.Kind));
                draw.AddImage((nint)status, barMin, barMin + fillSize,
                    Vector2.Zero, new Vector2(fraction, 1), color);
            }
            if (border != 0)
            {
                Vector2 borderMin = frame.Min + MirrorTimerUiLaw.BorderMin * scale;
                draw.AddImage((nint)border, borderMin,
                    borderMin + new Vector2(MirrorTimerUiLaw.BorderWidth,
                        MirrorTimerUiLaw.BorderHeight) * scale);
            }
            GameText.DrawCentered(draw, "GameFontHighlight", MirrorTimerCaption(timer),
                frame.Min + MirrorTimerUiLaw.TextCenter * scale, scale);
        }
    }

    private string MirrorTimerCaption(MirrorTimerState.ActiveTimer timer)
    {
        if (timer.SpellId != 0 &&
            _spellCatalog?.TryGet(timer.SpellId, out SpellInfo spell) == true &&
            spell.Name.Length > 0) return spell.Name;
        return MirrorTimerUiLaw.FallbackCaption(timer.Kind);
    }
}
