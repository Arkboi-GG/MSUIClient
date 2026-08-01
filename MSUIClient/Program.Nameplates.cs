using System.Numerics;
using ImGuiNET;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly HashSet<ulong> _vplateUnits = [];
    private readonly List<ScreenRect> _vplateClaims = [];
    private readonly List<(ScreenRect Rect, ulong Guid)> _vplateHits = [];

    private readonly record struct PlateCandidate(
        WorldEntity Unit, Vector2 Screen, FactionReaction Reaction, float SortDistance);

    private readonly record struct ScreenRect(float Left, float Top, float Right, float Bottom)
    {
        public float Width => Right - Left;
        public float Height => Bottom - Top;
        public bool Contains(Vector2 point) =>
            point.X >= Left && point.X <= Right && point.Y >= Top && point.Y <= Bottom;
        public bool Overlaps(ScreenRect other) =>
            Right > other.Left && Left < other.Right && Bottom > other.Top && Top < other.Bottom;
        public ScreenRect Offset(float x, float y) => new(Left + x, Top + y, Right + x, Bottom + y);
    }

    private void DrawWorldUnitNames()
    {
        _vplateUnits.Clear();
        _vplateHits.Clear();
        // Foreground draw-list content otherwise paints above DIALOG-strata frames.
        if (SettingsModalOpen) return;
        DrawEnemyPlates();

        foreach (WorldEntity unit in _entities.Units)
            DrawOverheadName(unit);
    }

    private void DrawOverheadName(WorldEntity unit)
    {
        if (_net is null || _vplateUnits.Contains(unit.Guid)) return;
        bool isSelf = unit.Guid == _net.PlayerGuid;
        bool isTarget = unit.Guid == _selectionGuid;
        if (!isTarget && unit.IsDead && !unit.IsPlayer) return;

        Vector3 feet = UnitWorldPosition(unit);
        float anchorHeight = UnitOverheadHeight(unit);
        Vector3 anchor = feet + new Vector3(0f, 0f, anchorHeight);
        Vector2 display = ImGui.GetIO().DisplaySize;
        if (!_window.Camera.TryWorldToScreen(anchor, display, out Vector2 screen)) return;

        EnsureUnitNameRequested(unit);
        string name = isSelf ? _net.PlayerName
            : unit.IsPlayer ? _playerNames.GetValueOrDefault(unit.Guid, "")
            : _creatureNames.GetValueOrDefault(unit.Entry, "");
        if (name.Length == 0) return;

        // benilla nameplates.rs:71-123: a normal unit's one-em pitch is 0.2 WORLD yards,
        // not a UI-scaled screen font. Project that billboard-world pitch so distance shrinks it.
        float worldPitch = anchorHeight > 4f ? (anchorHeight / 4f) * 1.5f * 0.2f : 0.2f;
        float fontSize = ProjectedWorldPitch(anchor, screen, worldPitch, display);
        if (fontSize < 1f) return;

        ImFontPtr font = ImGui.GetFont();
        Vector2 extent = ImGui.CalcTextSize(name) *
            (fontSize / MathF.Max(ImGui.GetFontSize(), 1f));
        Vector2 position = new(screen.X - extent.X * 0.5f, screen.Y - extent.Y);
        uint color = ReactionColorU32(ReactionTargetTowardPlayer(unit), unit.IsPlayer, unit.IsDead);
        ImDrawListPtr draw = ImGui.GetForegroundDrawList();
        draw.AddText(font, fontSize, position + Vector2.One, 0xc0000000u, name);
        draw.AddText(font, fontSize, position, color, name);
    }

    private void DrawEnemyPlates()
    {
        if (_net is null || _gameplayArt is null ||
            !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return;

        Vector2 display = ImGui.GetIO().DisplaySize;
        float diagonal = MathF.Sqrt(display.X * display.X + display.Y * display.Y);
        float basis = diagonal <= 1280f ? diagonal : 1280f + (diagonal - 1280f) * 0.5f;
        float width = MathF.Round(0.1f * basis);
        float height = MathF.Round(0.025f * basis);
        float nameSize = MathF.Min(32f, MathF.Round(0.01f * basis));
        float levelSize = MathF.Min(32f, MathF.Round(0.0086f * basis));
        Vector3 selfPosition = _controller?.Position ?? player.Position;
        Vector2 sortPoint = new(0.4f * diagonal, display.Y - 0.3f * diagonal);
        List<PlateCandidate> candidates = [];

        foreach (WorldEntity unit in _entities.Units)
        {
            if (unit.Guid == _net.PlayerGuid || unit.IsDead ||
                (unit.Fields.UnitFlags & NotSelectable) != 0 ||
                Vector3.DistanceSquared(selfPosition, UnitWorldPosition(unit)) > 20f * 20f)
                continue;

            FactionReaction reaction = ReactionTargetTowardPlayer(unit);
            if (reaction == FactionReaction.Friendly) continue; // benilla default: enemies ON, friends OFF

            Vector3 anchor = UnitWorldPosition(unit) +
                new Vector3(0f, 0f, UnitOverheadHeight(unit) + 2f / 3f);
            if (!_window.Camera.TryWorldToScreen(anchor, display, out Vector2 screen)) continue;
            candidates.Add(new PlateCandidate(unit, screen, reaction,
                Vector2.DistanceSquared(screen, sortPoint)));
        }

        candidates.Sort(static (a, b) => a.SortDistance.CompareTo(b.SortDistance));
        _vplateClaims.Clear();
        foreach (PlateCandidate candidate in candidates)
        {
            ScreenRect desired = new(candidate.Screen.X - width * 0.5f, candidate.Screen.Y,
                candidate.Screen.X + width * 0.5f, candidate.Screen.Y + height);
            ScreenRect plate = ResolveVplate(desired, display);
            _vplateClaims.Add(plate);
            _vplateHits.Add((plate, candidate.Unit.Guid));
            DrawEnemyPlate(candidate.Unit, candidate.Reaction, plate, basis, nameSize, levelSize, player);
            _vplateUnits.Add(candidate.Unit.Guid);
        }
    }

    private void DrawEnemyPlate(WorldEntity unit, FactionReaction reaction, ScreenRect plate,
        float basis, float nameSize, float levelSize, WorldEntity player)
    {
        ImDrawListPtr draw = ImGui.GetForegroundDrawList();
        bool target = unit.Guid == _selectionGuid;
        bool hover = plate.Contains(ImGui.GetIO().MousePos);
        bool lit = hover || unit.Guid == _hoveredGuid || target;
        float alpha = _selectionGuid == 0 || target ? 1f : 178f / 255f;
        uint alphaWhite = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, alpha));

        float Gx(float value) => MathF.Round(value * basis);
        float health = Math.Clamp(unit.HealthFraction, 0f, 1f);
        float barLeft = plate.Left + Gx(0.0031f);
        float barBottom = plate.Bottom - Gx(0.003125f);
        float barWidth = Gx(0.0804f);
        float barHeight = Gx(0.007025f);
        Vector2 barMin = new(barLeft, barBottom - barHeight);
        Vector2 barMax = new(barLeft + barWidth * health, barBottom);
        uint fill = _gameplayArt!.Handle(@"Interface\TargetingFrame\UI-TargetingFrame-BarFill");
        if (fill != 0 && health > 0f)
            draw.AddImage((nint)fill, barMin, barMax, Vector2.Zero, new Vector2(health, 1f),
                PlateTintU32(reaction, unit.IsPlayer, alpha, lit));

        // benilla vplates.rs:619-663: the border is above the cropped fill.
        uint border = _gameplayArt.Handle(@"Interface\Tooltips\Nameplate-Border");
        if (border != 0)
            draw.AddImage((nint)border, new Vector2(plate.Left, plate.Top),
                new Vector2(plate.Right, plate.Bottom), Vector2.Zero, Vector2.One, alphaWhite);

        EnsureUnitNameRequested(unit);
        string name = unit.IsPlayer
            ? _playerNames.GetValueOrDefault(unit.Guid, "Player")
            : _creatureNames.GetValueOrDefault(unit.Entry, $"Creature {unit.Entry}");
        uint nameColor = ImGui.ColorConvertFloat4ToU32(
            hover ? new Vector4(1f, 1f, 0f, alpha) : new Vector4(1f, 1f, 1f, alpha));
        DrawPlateText(draw, new Vector2((plate.Left + plate.Right) * 0.5f,
                (plate.Top + plate.Bottom) * 0.5f),
            name, nameSize, nameColor, bottomSeated: true);

        Vector2 levelAnchor = new(plate.Right - Gx(0.0092f), plate.Bottom - Gx(0.0071f));
        bool skull = reaction == FactionReaction.Hostile && unit.Level >= player.Level + 10 &&
                     !UnitIsGrey(player.Level, unit.Level);
        if (skull)
        {
            uint texture = _gameplayArt.Handle(@"Interface\TargetingFrame\UI-TargetingFrame-Skull");
            if (texture != 0)
            {
                float half = Gx(0.01f) * 0.5f;
                draw.AddImage((nint)texture, levelAnchor - new Vector2(half),
                    levelAnchor + new Vector2(half), Vector2.Zero, Vector2.One, alphaWhite);
            }
        }
        else if (unit.Level > 0)
        {
            DrawPlateText(draw, levelAnchor, unit.Level.ToString(), levelSize,
                WithAlpha(ConColorU32(player.Level, unit.Level), alpha), bottomSeated: false);
        }
    }

    private ScreenRect ResolveVplate(ScreenRect desired, Vector2 viewport)
    {
        ScreenRect result = ClampRect(desired, viewport);
        int bound = Math.Max(1, ((int)(viewport.X / MathF.Max(result.Width, 1f)) + 1) *
                                ((int)(viewport.Y / MathF.Max(result.Height, 1f)) + 1));
        for (int i = 0; i < bound; i++)
        {
            ScreenRect blocker = default;
            bool found = false;
            foreach (ScreenRect claim in _vplateClaims)
            {
                if (!result.Overlaps(claim)) continue;
                blocker = claim;
                found = true;
                break;
            }
            if (!found) break;
            result = result.Offset(0f, blocker.Top - result.Bottom); // center-region first try: UP
            result = ClampRect(result, viewport);
        }
        return new ScreenRect(MathF.Round(result.Left), MathF.Round(result.Top),
            MathF.Round(result.Left) + result.Width, MathF.Round(result.Top) + result.Height);
    }

    private static ScreenRect ClampRect(ScreenRect rect, Vector2 viewport)
    {
        float x = rect.Left;
        float y = rect.Top;
        if (x < 0f) x = 0f;
        if (x + rect.Width > viewport.X) x = MathF.Max(0f, viewport.X - rect.Width);
        if (y < 0f) y = 0f;
        if (y + rect.Height > viewport.Y) y = MathF.Max(0f, viewport.Y - rect.Height);
        return new ScreenRect(x, y, x + rect.Width, y + rect.Height);
    }

    private Vector3 UnitWorldPosition(WorldEntity unit) =>
        _net is not null && unit.Guid == _net.PlayerGuid && _controller is not null
            ? _controller.Position : unit.Position;

    private float UnitOverheadHeight(WorldEntity unit)
    {
        if (unit.IsCreature && _creatures?.TryGetOverheadHeight(unit, out float height) == true)
            return height;
        if (_net is not null && unit.Guid == _net.PlayerGuid && _character is not null)
            return MathF.Max(0.3f, _character.BindPoseHeight() * MathF.Max(0.01f, unit.Scale));
        return MathF.Max(0.3f, 2.2f * MathF.Max(0.01f, unit.Scale));
    }

    private float ProjectedWorldPitch(Vector3 anchor, Vector2 anchorScreen,
        float worldPitch, Vector2 display)
    {
        Vector3 forward = Vector3.Normalize(_window.Camera.EyeTarget - _window.Camera.Position);
        Vector3 right = Vector3.Cross(forward, Vector3.UnitZ);
        if (right.LengthSquared() < 1e-6f) right = Vector3.UnitX;
        else right = Vector3.Normalize(right);
        Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));
        return _window.Camera.TryWorldToScreen(anchor + up * worldPitch, display, out Vector2 top)
            ? Vector2.Distance(anchorScreen, top) : 0f;
    }

    private void EnsureUnitNameRequested(WorldEntity unit)
    {
        if (_net is null) return;
        if (unit.IsPlayer && !_playerNames.ContainsKey(unit.Guid) && _queriedPlayerNames.Add(unit.Guid))
            _net.NameQuery(unit.Guid);
        else if (unit.IsCreature && unit.Entry != 0 && !_creatureNames.ContainsKey(unit.Entry) &&
                 _queriedCreatureNames.Add(unit.Entry))
            _net.CreatureQuery(unit.Entry, unit.Guid);
    }

    private static void DrawPlateText(ImDrawListPtr draw, Vector2 anchor, string text,
        float fontSize, uint color, bool bottomSeated)
    {
        if (fontSize < 1f || text.Length == 0) return;
        ImFontPtr font = ImGui.GetFont();
        Vector2 extent = ImGui.CalcTextSize(text) *
            (fontSize / MathF.Max(ImGui.GetFontSize(), 1f));
        Vector2 position = new(anchor.X - extent.X * 0.5f,
            bottomSeated ? anchor.Y - extent.Y : anchor.Y - extent.Y * 0.5f);
        float shadow = MathF.Max(1f, MathF.Round(fontSize * 0.1f));
        draw.AddText(font, fontSize, position + new Vector2(shadow),
            WithAlpha(0xff000000u, MathF.Max(0.85f, AlphaOf(color))), text);
        draw.AddText(font, fontSize, position, color, text);
    }

    private static uint PlateTintU32(FactionReaction reaction, bool player, float alpha, bool lit)
    {
        Vector4 color = reaction == FactionReaction.Hostile ? new Vector4(1f, 0f, 0f, alpha)
            : player ? new Vector4(0f, 0f, 1f, alpha)
            : reaction == FactionReaction.Friendly ? new Vector4(0f, 1f, 0f, alpha)
            : new Vector4(1f, 1f, 0f, alpha);
        if (lit)
        {
            const float boost = 255f / 215f;
            color.X = MathF.Min(1f, color.X * boost);
            color.Y = MathF.Min(1f, color.Y * boost);
            color.Z = MathF.Min(1f, color.Z * boost);
        }
        return ImGui.ColorConvertFloat4ToU32(color);
    }

    private static uint ConColorU32(uint playerLevel, uint unitLevel)
    {
        long difference = (long)unitLevel - playerLevel;
        Vector4 color = difference >= 5 ? new Vector4(1f, 25f / 255f, 25f / 255f, 1f)
            : difference >= 3 ? new Vector4(1f, 127f / 255f, 63f / 255f, 1f)
            : difference >= -2 ? new Vector4(1f, 1f, 0f, 1f)
            : !UnitIsGrey(playerLevel, unitLevel)
                ? new Vector4(63f / 255f, 178f / 255f, 63f / 255f, 1f)
                : new Vector4(127f / 255f, 127f / 255f, 127f / 255f, 1f);
        return ImGui.ColorConvertFloat4ToU32(color);
    }

    private static bool UnitIsGrey(uint playerLevel, uint unitLevel)
    {
        ReadOnlySpan<uint> bands = [4, 4, 5, 5, 6, 6, 7, 7, 8, 9, 10, 11, 12, 12, 12, 12, 12, 12, 12, 12];
        uint band = bands[(int)Math.Min(playerLevel / 5, (uint)bands.Length - 1)];
        return playerLevel > unitLevel && playerLevel - unitLevel > band;
    }

    private static float AlphaOf(uint color) => ((color >> 24) & 0xff) / 255f;
    private static uint WithAlpha(uint color, float alpha) =>
        (color & 0x00ffffffu) | ((uint)Math.Clamp(MathF.Round(alpha * 255f), 0f, 255f) << 24);
}
