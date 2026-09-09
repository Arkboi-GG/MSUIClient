using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

/// <summary>
/// Swing Timer — the melee/ranged auto-attack rail, ported from the MSUI_SwingTimer addon.
/// Geometry, the aim band and mode selection are <see cref="SwingTimerLaw"/>; this half
/// observes the real combat wire and draws.
///
/// Swings arrive as typed events, never as parsed chat text: SMSG_ATTACKERSTATEUPDATE for
/// melee (with the offhand bit the addon had to guess at) and SMSG_SPELL_GO for ranged.
/// </summary>
public sealed partial class GameLoop
{
    /// <summary>Offhand origin on a white hit. Already the bit this client reads for swing
    /// animations and melee sounds; the addon had no equivalent and re-seeded the
    /// most-expired hand instead.</summary>
    private const uint HitInfoOffHand = 0x0004u;

    private SwingTrack _swingMain;
    private SwingTrack _swingOff;
    private SwingTrack _swingRanged;
    private double _swingLastMeleeAt = double.NegativeInfinity;
    private double _swingLastRangedAt = double.NegativeInfinity;
    private bool _swingRangedHasAimPenalty;
    private bool _swingTimerDragDirty;

    private GameSettings.SwingTimerSettings SwingTimerSettings =>
        (Settings.AddOns ??= new GameSettings.AddOnSettings()).SwingTimer ??=
            new GameSettings.SwingTimerSettings();

    /// <summary>
    /// A completed melee swing by the unit we are driving. The server has already processed
    /// it, so the timer starts back-dated by the packet's flight time rather than at now.
    /// </summary>
    private void NoteSwingTimerMelee(CombatMeleeSwing swing)
    {
        var cfg = SwingTimerSettings;
        if (!cfg.Enabled || !cfg.TrackMelee || _net is null) return;
        if (swing.Attacker != ControlledGuid) return;
        if (!_entities.TryGet(ControlledGuid, out WorldEntity self)) return;

        bool offHand = (swing.HitInfo & HitInfoOffHand) != 0;
        float duration = SwingTimerLaw.SwingSeconds(offHand
            ? self.Fields.OffhandAttackTime : self.Fields.MainAttackTime);
        if (duration <= 0f) return;

        double started = NowSeconds() - SwingTimerLaw.FlightCompensation(
            cfg.CompensateLatency, _net.LatencyMs);
        if (offHand) _swingOff = new SwingTrack(started, duration);
        else _swingMain = new SwingTrack(started, duration);
        _swingLastMeleeAt = started;
    }

    /// <summary>
    /// Core resets the ranged attack timer when an auto-repeat shot fires.
    /// Other ranged abilities (for example Arcane Shot) do not restart that timer.
    /// The auto-repeat attribute also covers custom shots without a name table.
    /// </summary>
    private void NoteSwingTimerRanged(uint spellId, in SpellInfo info, ulong caster)
    {
        var cfg = SwingTimerSettings;
        if (!cfg.Enabled || !cfg.TrackRanged || _net is null) return;
        if (caster != ControlledGuid || !info.AutoRepeat) return;
        if (!_entities.TryGet(ControlledGuid, out WorldEntity self)) return;

        float duration = SwingTimerLaw.SwingSeconds(self.Fields.RangedAttackTime);
        if (duration <= 0f) return;

        double started = NowSeconds()
            - SwingTimerLaw.FlightCompensation(cfg.CompensateLatency, _net.LatencyMs)
            - SwingTimerLaw.ClampTravel(cfg.RangedTravelSeconds);
        _swingRanged = new SwingTrack(started, duration);
        _swingLastRangedAt = started;
        // Only a real ranged weapon reloads with an aim penalty; a wand does not. The
        // addon gated this on the player's class being HUNTER, which is the same set in
        // 1.12 but says the wrong thing about why.
        _swingRangedHasAimPenalty = info.RangedSpeedCooldown && spellId != WandShootSpellId;
    }

    /// <summary>Wand auto-shot. Its reload has no plant/aim window.</summary>
    private const uint WandShootSpellId = 5019;

    private void DrawSwingTimer()
    {
        if (_net is null && !HudPreview) return;
        var cfg = SwingTimerSettings;
        double now = NowSeconds();

        SwingMode mode = SwingTimerLaw.Mode(cfg.TrackMelee, cfg.TrackRanged,
            _swingLastMeleeAt, _swingLastRangedAt);
        float? main = mode == SwingMode.Melee ? _swingMain.Progress(now) : null;
        float? off = mode == SwingMode.Melee ? _swingOff.Progress(now) : null;
        float? ranged = mode == SwingMode.Ranged ? _swingRanged.Progress(now) : null;
        bool anyRunning = main is not null || off is not null || ranged is not null;

        if (!SwingTimerLaw.Visible(cfg.Enabled, cfg.Unlocked, cfg.HideWhenIdle, anyRunning))
            return;

        float s = GameplayUiScale() * SwingTimerLaw.ClampScale(cfg.Scale);
        Vector2 size = new(SwingTimerLaw.ClampWidth(cfg.Width),
            SwingTimerLaw.ClampHeight(cfg.Height));
        Vector2 logicalDisplay = ImGui.GetIO().DisplaySize / s;
        Vector2 authored = new((logicalDisplay.X - size.X) * .5f, logicalDisplay.Y * .5f + 180f);
        Vector2 origin = ClampSwingTimerOrigin(
            authored + new Vector2(cfg.OffsetX, cfg.OffsetY), logicalDisplay, size);

        DrawSwingTimerMover(ref origin, authored, logicalDisplay, size, s);

        ImDrawListPtr dl = ImGui.GetBackgroundDrawList();
        Vector2 pxMin = origin * s;
        Vector2 pxSize = size * s;
        dl.AddRectFilled(pxMin, pxMin + pxSize, 0x99000000);

        if (mode == SwingMode.Ranged && SwingTimerLaw.AimBand(cfg.ShowAimBand,
                _swingRangedHasAimPenalty, _swingRanged.Duration) is { } band)
        {
            Vector2 bandMin = new(pxMin.X + band.Start * pxSize.X, pxMin.Y);
            Vector2 bandMax = new(pxMin.X + band.End * pxSize.X, pxMin.Y + pxSize.Y);
            dl.AddRectFilled(bandMin, bandMax, 0x593333d9);
        }

        dl.AddRect(pxMin, pxMin + pxSize, cfg.Unlocked ? 0xff00ccff : 0xff000000);

        if (main is { } m) DrawSwingCursor(dl, origin, size, s, m, 0xff268cf2);
        if (off is { } o) DrawSwingCursor(dl, origin, size, s, o, 0xffd9bf33);
        if (ranged is { } r) DrawSwingCursor(dl, origin, size, s, r, 0xff59d959);

        if (!cfg.ShowText) return;
        SwingTrack lead = mode == SwingMode.Ranged ? _swingRanged
            : _swingOff.Progress(now) is not null && _swingMain.Progress(now) is null
                ? _swingOff : _swingMain;
        if (SwingTimerLaw.Remaining(lead, now) is { } remaining)
            DrawUnitFrameText(dl, pxMin + pxSize * .5f,
                remaining.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                10f * s, 0xffffffff);
    }

    private static void DrawSwingCursor(ImDrawListPtr dl, Vector2 origin, Vector2 size,
        float s, float progress, uint color)
    {
        float offset = SwingTimerLaw.CursorOffset(progress, size.X);
        Vector2 min = new Vector2(origin.X + offset, origin.Y) * s;
        Vector2 cursor = new Vector2(SwingTimerLaw.CursorWidth, size.Y) * s;
        dl.AddRectFilled(min, min + cursor, color);
    }

    private static Vector2 ClampSwingTimerOrigin(Vector2 origin, Vector2 logicalDisplay,
        Vector2 size) =>
        new(Math.Clamp(origin.X, 0f, MathF.Max(0f, logicalDisplay.X - size.X)),
            Math.Clamp(origin.Y, 0f, MathF.Max(0f, logicalDisplay.Y - size.Y)));

    private void DrawSwingTimerMover(ref Vector2 origin, Vector2 authored,
        Vector2 logicalDisplay, Vector2 size, float s)
    {
        var cfg = SwingTimerSettings;
        if (!cfg.Unlocked) return;

        Vector2 handleSize = new(size.X, 20f);
        Vector2 handle = origin + new Vector2(0f, size.Y + 4f);
        ImGui.SetNextWindowPos(handle * s);
        ImGui.SetNextWindowSize(handleSize * s);
        ImGui.SetNextWindowBgAlpha(.82f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoNav;
        ImGui.Begin("##swing-timer-mover", flags);
        ImGui.Button("Drag swing timer##swing-timer-drag", handleSize * s);
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            origin = ClampSwingTimerOrigin(
                origin + ImGui.GetIO().MouseDelta / MathF.Max(.01f, s), logicalDisplay, size);
            Vector2 offset = origin - authored;
            cfg.OffsetX = offset.X;
            cfg.OffsetY = offset.Y;
            _swingTimerDragDirty = true;
        }
        if (_swingTimerDragDirty && ImGui.IsItemDeactivated())
        {
            SettingsFile?.Save();
            _swingTimerDragDirty = false;
        }
        ImGui.End();
    }
}
