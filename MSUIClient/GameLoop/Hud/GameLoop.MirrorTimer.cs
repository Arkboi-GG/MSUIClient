using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

/// <summary>MirrorTimer1/2/3, with a local breath fallback for client-owned movement.</summary>
public sealed partial class GameLoop
{
    private const uint PredictedBreathDurationMs = 60_000;
    private const int PredictedBreathDrainScale = -1;
    private const int PredictedBreathRecoveryScale = 10;

    private readonly MirrorTimerState _mirrorTimers = new();
    private bool _predictedBodySubmerged;
    private bool _serverBreathObservedThisImmersion;

    private void ResetMirrorTimers()
    {
        _mirrorTimers.Clear();
        _predictedBodySubmerged = false;
        _serverBreathObservedThisImmersion = false;
    }

    private void ApplyMirrorTimerStart(byte[] body)
    {
        MirrorTimerStart packet = MirrorTimerPackets.ParseStart(body);
        if (packet.Kind == MirrorTimerKind.Breath)
            _serverBreathObservedThisImmersion = true;
        MirrorTimerState.ActiveTimer? timer = _mirrorTimers.Start(packet, NowSeconds());
        EmitInterface("mirror-timer", "start", timer is null ? "UNSHOWN" : "RESTATED",
            packet.SpellId,
            $"kind={packet.RawKind};remainingMs={packet.RemainingMs};" +
            $"durationMs={packet.DurationMs};scale={packet.Scale};paused={packet.Paused}");
    }

    private void ApplyMirrorTimerPause(byte[] body)
    {
        (uint rawKind, bool paused) = MirrorTimerPackets.ParsePause(body);
        if (rawKind == (uint)MirrorTimerKind.Breath)
            _serverBreathObservedThisImmersion = true;
        bool applied = _mirrorTimers.Pause(rawKind, paused, NowSeconds());
        EmitInterface("mirror-timer", "pause", applied ? "APPLIED" : "IGNORED", rawKind,
            $"paused={paused}");
    }

    private void ApplyMirrorTimerStop(byte[] body)
    {
        uint rawKind = MirrorTimerPackets.ParseStop(body);
        if (rawKind == (uint)MirrorTimerKind.Breath)
            _serverBreathObservedThisImmersion = true;
        bool stopped = _mirrorTimers.Stop(rawKind);
        EmitInterface("mirror-timer", "stop", stopped ? "HIDDEN" : "IGNORED", rawKind,
            "bodyBytes=4");
    }

    /// <summary>
    /// The controlled mover is client-authoritative, so its local liquid-depth
    /// decision can precede (or on incomplete realms, never receive) the server
    /// mirror timer. Predict only that missing presentation. Any breath packet
    /// takes ownership for the rest of the current immersion.
    /// </summary>
    private void UpdatePredictedBreath()
    {
        double now = NowSeconds();
        bool ownsSessionBody = ControllerOwnsControlledBodyPose &&
                               ControlledGuid == LocalPlayerGuid;
        bool submerged = ownsSessionBody && _controller is not null &&
            TryGetBodyLiquidSurface(_controller.Position, out float surfaceZ, out _) &&
            surfaceZ - _controller.Position.Z > _controller.CollisionHeight;
        MirrorTimerState.ActiveTimer? breath = _mirrorTimers.Find(MirrorTimerKind.Breath);

        if (submerged)
        {
            if (!_predictedBodySubmerged &&
                breath is not { ServerAuthoritative: true })
                _serverBreathObservedThisImmersion = false;

            if (!_serverBreathObservedThisImmersion)
            {
                if (breath is null)
                {
                    StartPredictedBreath(PredictedBreathDurationMs,
                        PredictedBreathDrainScale, now);
                }
                else if (!breath.ServerAuthoritative && breath.Scale >= 0)
                {
                    uint currentMs = PredictedBreathMilliseconds(breath, now);
                    StartPredictedBreath(currentMs, PredictedBreathDrainScale, now);
                }
            }
        }
        else if (breath is { ServerAuthoritative: false })
        {
            uint currentMs = PredictedBreathMilliseconds(breath, now);
            if (currentMs >= PredictedBreathDurationMs - 1)
                _mirrorTimers.Stop((uint)MirrorTimerKind.Breath);
            else if (breath.Scale <= 0)
                StartPredictedBreath(currentMs, PredictedBreathRecoveryScale, now);
        }

        if (!submerged)
            _serverBreathObservedThisImmersion = false;
        _predictedBodySubmerged = submerged;
    }

    private void StartPredictedBreath(uint remainingMs, int scale, double now) =>
        _mirrorTimers.Start(new MirrorTimerStart(
            (uint)MirrorTimerKind.Breath, remainingMs,
            PredictedBreathDurationMs, scale, false, 0),
            now, serverAuthoritative: false);

    private static uint PredictedBreathMilliseconds(
        MirrorTimerState.ActiveTimer breath, double now) =>
        (uint)Math.Clamp(Math.Round(MirrorTimerState.ValueAt(breath, now) * 1000.0),
            0.0, PredictedBreathDurationMs);

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
            MirrorTimerUiLaw.ScreenRect bar = MirrorTimerUiLaw.BarRect(frame, scale);
            draw.AddRectFilled(bar.Min, bar.Min + bar.Size, 0x80000000);

            float fraction = MirrorTimerState.FractionAt(timer, now);
            if (status != 0 && fraction > 0)
            {
                Vector2 fillSize = MirrorTimerUiLaw.FillSize(bar.Size, fraction);
                uint color = ImGui.ColorConvertFloat4ToU32(MirrorTimerUiLaw.Color(timer.Kind));
                draw.AddImage((nint)status, bar.Min, bar.Min + fillSize,
                    Vector2.Zero, MirrorTimerUiLaw.FillUvMax(fraction), color);
            }
            if (border != 0)
            {
                MirrorTimerUiLaw.ScreenRect borderRect =
                    MirrorTimerUiLaw.BorderRect(frame, scale);
                draw.AddImage((nint)border, borderRect.Min,
                    borderRect.Min + borderRect.Size);
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
