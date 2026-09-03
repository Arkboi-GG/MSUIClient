using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

/// <summary>
/// Player Power Bars — the movable health/power pair, ported from the MSUI_PowerBars addon.
/// Geometry, captions and the tick sweep are <see cref="PlayerPowerBarsLaw"/>; this half
/// draws them and watches the power field for the regen tick.
/// </summary>
public sealed partial class GameLoop
{
    /// <summary>Last observed power value, for spotting the upward jump that is a regen tick.</summary>
    private uint _powerBarsPreviousPower;
    private byte _powerBarsPreviousType = byte.MaxValue;
    private ulong _powerBarsPreviousUnit;

    /// <summary>When the most recent regen tick was observed, or null if none has been seen
    /// on this unit yet. The sweep cannot be drawn before the first tick is witnessed.</summary>
    private double? _powerBarsLastTickAt;

    private bool _powerBarsDragDirty;

    private GameSettings.PlayerPowerBarsSettings PowerBarsSettings =>
        (Settings.AddOns ??= new GameSettings.AddOnSettings()).PowerBars ??=
            new GameSettings.PlayerPowerBarsSettings();

    /// <summary>Authored home: centred horizontally, below the character, matching the
    /// addon's CENTER / y=-240 default. The saved offset is applied on top of this, so a
    /// resolution change moves the bars with the centre instead of stranding them.</summary>
    private static Vector2 PowerBarsAuthoredOrigin(Vector2 logicalDisplay, Vector2 size) =>
        new((logicalDisplay.X - size.X) * .5f, logicalDisplay.Y * .5f + 240f);

    private void DrawPlayerPowerBars()
    {
        if (_net is null && !HudPreview) return;
        var cfg = PowerBarsSettings;
        if (!cfg.Enabled) return;
        if (!_entities.TryGet(ControlledGuid, out WorldEntity player)) return;

        UpdatePowerBarsTick(player);

        int pips = cfg.ShowCombo ? VisibleComboPoints() : 0;
        PlayerPowerBarsLayout layout = PlayerPowerBarsLaw.Layout(
            cfg.Width, cfg.HealthHeight, cfg.PowerHeight, cfg.Spacing, pips);

        // The feature's own Scale multiplies the global Interface scale rather than
        // replacing it, so these bars stay in proportion with the rest of the HUD.
        float s = GameplayUiScale() * PlayerPowerBarsLaw.ClampScale(cfg.Scale);
        Vector2 logicalDisplay = ImGui.GetIO().DisplaySize / s;
        Vector2 authored = PowerBarsAuthoredOrigin(logicalDisplay, layout.Size);
        Vector2 origin = ClampPowerBarsOrigin(
            authored + new Vector2(cfg.OffsetX, cfg.OffsetY), logicalDisplay, layout.Size);

        DrawPowerBarsMover(ref origin, authored, logicalDisplay, layout, s);

        ImDrawListPtr dl = ImGui.GetBackgroundDrawList();
        PowerBarText text = PlayerPowerBarsLaw.TextMode(cfg.ShowText, cfg.ShowPercent);

        DrawPowerBarsOneBar(dl, origin + layout.HealthMin, layout.HealthSize, s,
            player.HealthFraction, new Vector4(.1f, .9f, .1f, 1f),
            PlayerPowerBarsLaw.Caption(text, player.Fields.Health, player.Fields.MaxHealth));

        if (player.Fields.ActiveMaxPower > 0)
        {
            Vector2 powerMin = origin + layout.PowerMin;
            DrawPowerBarsOneBar(dl, powerMin, layout.PowerSize, s,
                player.PowerFraction, PowerColor(player.Fields.PowerType),
                PlayerPowerBarsLaw.Caption(text, player.Fields.ActivePower,
                    player.Fields.ActiveMaxPower));
            DrawPowerBarsTickCursor(dl, powerMin, layout.PowerSize, s, player.Fields.PowerType);
        }

        if (pips > 0) DrawPowerBarsComboPips(dl, origin + layout.ComboMin, s, pips);
    }

    /// <summary>
    /// Watch the power field for the upward jump that means the server ticked. There is no
    /// packet for the tick, so it can only be inferred — but natively that inference reads
    /// the live field on the frame it changes, instead of the addon's 5/sec sample.
    ///
    /// Resets whenever the observed unit or its power type changes, so possessing a bot or
    /// a Druid leaving Cat Form cannot carry a stale energy phase across.
    /// </summary>
    private void UpdatePowerBarsTick(in WorldEntity player)
    {
        byte type = player.Fields.PowerType;
        uint current = player.Fields.ActivePower;
        if (player.Guid != _powerBarsPreviousUnit || type != _powerBarsPreviousType)
        {
            _powerBarsPreviousUnit = player.Guid;
            _powerBarsPreviousType = type;
            _powerBarsPreviousPower = current;
            _powerBarsLastTickAt = null;
            return;
        }
        if (PlayerPowerBarsLaw.IsRegenTick(type, _powerBarsPreviousPower, current))
            _powerBarsLastTickAt = NowSeconds();
        _powerBarsPreviousPower = current;
    }

    private static Vector2 ClampPowerBarsOrigin(Vector2 origin, Vector2 logicalDisplay,
        Vector2 size)
    {
        // The combo row sits ABOVE the health bar, so the top margin has to leave room for
        // it or unlocking at the top of the screen puts the pips off-screen.
        float top = PlayerPowerBarsLaw.ComboPipSize + PlayerPowerBarsLaw.ComboLift;
        return new(Math.Clamp(origin.X, 0f, MathF.Max(0f, logicalDisplay.X - size.X)),
            Math.Clamp(origin.Y, top, MathF.Max(top, logicalDisplay.Y - size.Y)));
    }

    /// <summary>One bar: dark trough, authored status-bar fill, border, centred caption.</summary>
    private void DrawPowerBarsOneBar(ImDrawListPtr dl, Vector2 min, Vector2 size, float s,
        float fraction, Vector4 color, string caption)
    {
        Vector2 pxMin = min * s;
        Vector2 pxSize = size * s;
        dl.AddRectFilled(pxMin, pxMin + pxSize, 0x99000000);
        DrawVanillaStatusBar(dl, pxMin, pxSize, fraction, color);
        dl.AddRect(pxMin, pxMin + pxSize,
            PowerBarsSettings.Unlocked ? 0xff00ccff : 0xff000000);
        if (caption.Length > 0)
            DrawUnitFrameText(dl, pxMin + pxSize * .5f, caption, 10f * s, 0xffffffff);
    }

    /// <summary>
    /// The energy tick sweep: a thin cursor crossing the power bar once per server regen
    /// tick. Energy only — every other power type regenerates on a curve the player has no
    /// reason to time, and the law refuses them.
    /// </summary>
    private void DrawPowerBarsTickCursor(ImDrawListPtr dl, Vector2 min, Vector2 size, float s,
        byte powerType)
    {
        var cfg = PowerBarsSettings;
        if (PlayerPowerBarsLaw.TickSweep(cfg.ShowTickBar, powerType, NowSeconds(),
                _powerBarsLastTickAt, cfg.TickSeconds) is not { } sweep) return;
        float usable = MathF.Max(0f, size.X - PlayerPowerBarsLaw.TickCursorWidth);
        Vector2 cursorMin = new Vector2(min.X + sweep * usable, min.Y) * s;
        Vector2 cursorSize = new Vector2(PlayerPowerBarsLaw.TickCursorWidth, size.Y) * s;
        dl.AddRectFilled(cursorMin, cursorMin + cursorSize, 0xe6ffffff);
    }

    private void DrawPowerBarsComboPips(ImDrawListPtr dl, Vector2 rowMin, float s, int points)
    {
        for (int i = 0; i < points; i++)
        {
            Vector2 pxMin = (rowMin + PlayerPowerBarsLaw.ComboPipMin(i)) * s;
            Vector2 pxSize = new Vector2(PlayerPowerBarsLaw.ComboPipSize) * s;
            // The addon's pip colour (1.00, 0.65, 0.00), packed ABGR for ImGui.
            dl.AddRectFilled(pxMin, pxMin + pxSize, 0xff00a6ff);
            dl.AddRect(pxMin, pxMin + pxSize, 0xff000000);
        }
    }

    /// <summary>
    /// The drag handle, shown only while unlocked. Same shape as the chat frame's mover:
    /// drag updates the live origin, and the settings file is written once on release
    /// rather than on every frame of the drag.
    /// </summary>
    private void DrawPowerBarsMover(ref Vector2 origin, Vector2 authored,
        Vector2 logicalDisplay, in PlayerPowerBarsLayout layout, float s)
    {
        var cfg = PowerBarsSettings;
        if (!cfg.Unlocked) return;

        Vector2 handleSize = new(layout.Size.X, 20f);
        Vector2 handle = origin + new Vector2(0f, layout.Size.Y + 4f);
        ImGui.SetNextWindowPos(handle * s);
        ImGui.SetNextWindowSize(handleSize * s);
        ImGui.SetNextWindowBgAlpha(.82f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoNav;
        ImGui.Begin("##power-bars-mover", flags);
        ImGui.Button("Drag power bars##power-bars-drag", handleSize * s);
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            origin = ClampPowerBarsOrigin(
                origin + ImGui.GetIO().MouseDelta / MathF.Max(.01f, s),
                logicalDisplay, layout.Size);
            Vector2 offset = origin - authored;
            cfg.OffsetX = offset.X;
            cfg.OffsetY = offset.Y;
            _powerBarsDragDirty = true;
        }
        if (_powerBarsDragDirty && ImGui.IsItemDeactivated())
        {
            SettingsFile?.Save();
            _powerBarsDragDirty = false;
        }
        ImGui.End();
    }
}
