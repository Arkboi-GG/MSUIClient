using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private const float NameplateRangeYards = NameplateUiLaw.RangeYards;
    private readonly List<NameplateUiLaw.Bounds> _vplateClaims = [];
    private readonly List<(NameplateUiLaw.Bounds Rect, ulong Guid)> _vplateHits = [];
    private readonly List<World.Units.WorldNameRenderer.Label> _worldNameLabels = [];
    // Current Benilla boots enemy/neutral plates on and friendly plates off. The two switches
    // remain independent; ALLNAMEPLATES composes them through NameplateUiLaw.ToggleAll.
    private bool _enemyNameplatesVisible = true;
    private bool _friendlyNameplatesVisible;
    private bool _enemyNameplateToggleWasDown;
    private bool _friendlyNameplateToggleWasDown;
    private bool _allNameplateToggleWasDown;

    private readonly record struct PlateCandidate(
        WorldEntity Unit, Vector2 Screen, FactionReaction Reaction, float SortDistance);

    // Shared only with the developer waypoint editor's screen-space hit regions. Player-facing
    // V-plate geometry uses NameplateUiLaw.Bounds below.
    private readonly record struct ScreenRect(float Left, float Top, float Right, float Bottom)
    {
        public bool Contains(Vector2 point) =>
            point.X >= Left && point.X <= Right && point.Y >= Top && point.Y <= Bottom;
    }

    private void DrawWorldUnitNames()
    {
        _vplateHits.Clear();
        DrawChatBubbles();
        // V-key plates remain a DIALOG-strata 2D overlay. Ordinary overhead names are a
        // separate depth-tested world batch assembled by RenderWorldUnitNames().
        if (SettingsModalOpen ||
            !_enemyNameplatesVisible && !_friendlyNameplatesVisible) return;
        DrawNameplates();
    }

    private void RenderWorldUnitNames()
    {
        if (_worldNames is null || _net is null || SettingsModalOpen ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return;

        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector3 selfPosition = _controller?.Position ?? player.Position;
        _worldNameLabels.Clear();
        foreach (WorldEntity unit in _entities.Units)
        {
            bool own = unit.Guid == ControlledGuid;
            bool selected = unit.Guid == _selectionGuid;
            if (HasLiveChatBubble(unit.Guid) || WouldHaveActiveNameplate(unit, display)) continue;
            if (own)
            {
                if (!Settings.Controls.ShowOwnName ||
                    _window.Camera.EffectiveDistance <= FirstPersonBodyHide) continue;
            }
            else if ((!selected && unit.IsPlayer && !Settings.Controls.ShowPlayerNames) ||
                     (!selected && !unit.IsPlayer && !Settings.Controls.ShowNpcNames) ||
                     (unit.IsDead && !selected))
            {
                continue;
            }

            EnsureUnitNameRequested(unit);
            string name = unit.IsPlayer
                ? _playerNames.GetValueOrDefault(unit.Guid, "Player")
                : ResolveCreatureOrPetName(unit, $"Creature {unit.Entry}");
            List<string> lines =
            [
                NameplateUiLaw.NameLine(name, unit.IsPlayer, unit.Fields.PlayerFlags),
            ];
            if (unit.IsCreature &&
                _creatureQueryRecords.TryGetValue(unit.Entry, out CreatureQueryInfo? creatureInfo) &&
                NameplateUiLaw.CreatureSubnameLine(true, creatureInfo?.Subname) is { } subname)
                lines.Add(subname);

            FactionReaction reaction = ReactionTargetTowardPlayer(unit);
            Vector3 rgb = NameplateUiLaw.SelectionRgb(reaction, unit.IsPlayer, unit.IsDead,
                _attackTargetGuid == unit.Guid, MovementInfo.ClientUptimeMs());
            Vector3 position = UnitWorldPosition(unit);
            float distance = Vector3.Distance(selfPosition, position);
            _worldNameLabels.Add(new World.Units.WorldNameRenderer.Label(
                position + new Vector3(0f, 0f, UnitOverheadHeight(unit)),
                lines, new Vector4(rgb, 1f), NameplateUiLaw.WorldNamePitch(distance)));
        }
        _worldNames.Render(_window.Camera, _worldNameLabels);
    }

    private void UpdateNameplateInput(bool typing)
    {
        bool enemy = BindingDown(GameBinding.ToggleEnemyNameplates);
        bool friendly = BindingDown(GameBinding.ToggleFriendlyNameplates);
        bool all = BindingDown(GameBinding.ToggleAllNameplates);
        if (!typing)
        {
            if (enemy && !_enemyNameplateToggleWasDown)
                _enemyNameplatesVisible = !_enemyNameplatesVisible;
            if (friendly && !_friendlyNameplateToggleWasDown)
                _friendlyNameplatesVisible = !_friendlyNameplatesVisible;
            if (all && !_allNameplateToggleWasDown)
                (_enemyNameplatesVisible, _friendlyNameplatesVisible) =
                    NameplateUiLaw.ToggleAll(_enemyNameplatesVisible,
                        _friendlyNameplatesVisible);
        }
        _enemyNameplateToggleWasDown = enemy;
        _friendlyNameplateToggleWasDown = friendly;
        _allNameplateToggleWasDown = all;
    }

    private void DrawNameplates()
    {
        if (_net is null || _gameplayArt is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return;

        Vector2 display = ImGui.GetIO().DisplaySize;
        NameplateUiLaw.PlateLayout layout = NameplateUiLaw.Layout(display);
        Vector3 selfPosition = _controller?.Position ?? player.Position;
        List<PlateCandidate> candidates = [];

        foreach (WorldEntity unit in _entities.Units)
        {
            bool own = unit.Guid == ControlledGuid;
            bool namesEnabled = own ? Settings.Controls.ShowOwnName :
                unit.IsPlayer ? Settings.Controls.ShowPlayerNames :
                Settings.Controls.ShowNpcNames;
            if (own || !namesEnabled || unit.IsDead ||
                HasLiveChatBubble(unit.Guid) ||
                (unit.Fields.UnitFlags & NotSelectable) != 0 ||
                Vector3.DistanceSquared(selfPosition, UnitWorldPosition(unit)) >
                    NameplateUiLaw.RangeYards * NameplateUiLaw.RangeYards)
                continue;

            FactionReaction reaction = ReactionTargetTowardPlayer(unit);
            if (!NameplateUiLaw.ModeAllows(reaction, _enemyNameplatesVisible,
                    _friendlyNameplatesVisible)) continue;

            Vector3 anchor = UnitWorldPosition(unit) +
                new Vector3(0f, 0f, UnitOverheadHeight(unit) + 2f / 3f);
            if (!_window.Camera.TryWorldToScreen(anchor, display, out Vector2 screen)) continue;
            candidates.Add(new PlateCandidate(unit, screen, reaction,
                Vector2.DistanceSquared(screen, layout.SortPoint)));
        }

        candidates.Sort(static (a, b) => a.SortDistance.CompareTo(b.SortDistance));
        _vplateClaims.Clear();
        foreach (PlateCandidate candidate in candidates)
        {
            NameplateUiLaw.Bounds desired = NameplateUiLaw.DesiredPlate(candidate.Screen, layout);
            NameplateUiLaw.Bounds plate = ResolveVplate(desired, display);
            _vplateClaims.Add(plate);
            _vplateHits.Add((plate, candidate.Unit.Guid));
            DrawNameplate(candidate.Unit, candidate.Reaction, plate, layout, player);
        }
    }

    private void DrawNameplate(WorldEntity unit, FactionReaction reaction,
        NameplateUiLaw.Bounds plate, NameplateUiLaw.PlateLayout layout, WorldEntity player)
    {
        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
        bool target = unit.Guid == _selectionGuid;
        bool hover = plate.Contains(ImGui.GetIO().MousePos);
        bool lit = hover || unit.Guid == _hoveredGuid || target;
        float alpha = _selectionGuid == 0 || target ? 1f : 178f / 255f;
        uint alphaWhite = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, alpha));

        float health = Math.Clamp(unit.HealthFraction, 0f, 1f);
        NameplateUiLaw.ImageRect healthRect =
            NameplateUiLaw.HealthFill(plate, layout.Basis, health);
        uint fill = _gameplayArt!.Handle(@"Interface\TargetingFrame\UI-TargetingFrame-BarFill");
        if (fill != 0 && health > 0f)
            draw.AddImage((nint)fill, healthRect.Min, healthRect.Max, Vector2.Zero,
                NameplateUiLaw.HealthUvMax(health),
                PlateTintU32(reaction, unit.IsPlayer, alpha, lit));

        // benilla vplates.rs:619-663: the border is above the cropped fill.
        uint border = _gameplayArt.Handle(@"Interface\Tooltips\Nameplate-Border");
        if (border != 0)
        {
            NameplateUiLaw.ImageRect frame = NameplateUiLaw.Frame(plate);
            draw.AddImage((nint)border, frame.Min, frame.Max,
                Vector2.Zero, Vector2.One, alphaWhite);
        }

        EnsureUnitNameRequested(unit);
        string name = unit.IsPlayer
            ? _playerNames.GetValueOrDefault(unit.Guid, "Player")
            : ResolveCreatureOrPetName(unit, $"Creature {unit.Entry}");
        string nameLine = NameplateUiLaw.NameLine(name, unit.IsPlayer, unit.Fields.PlayerFlags);
        string? subnameLine = unit.IsCreature &&
            _creatureQueryRecords.TryGetValue(unit.Entry, out CreatureQueryInfo? creatureInfo)
                ? NameplateUiLaw.CreatureSubnameLine(true, creatureInfo?.Subname)
                : null;
        Vector3 nameRgb = NameplateUiLaw.SelectionRgb(reaction, unit.IsPlayer, unit.IsDead,
            _attackTargetGuid == unit.Guid, MovementInfo.ClientUptimeMs());
        uint nameColor = ImGui.ColorConvertFloat4ToU32(new Vector4(nameRgb, alpha));
        DrawPlateText(draw, NameplateUiLaw.NameAnchor(plate, 0, layout.NameSize),
            nameLine, layout.NameSize, nameColor, bottomSeated: true);
        if (subnameLine is not null)
            DrawPlateText(draw, NameplateUiLaw.NameAnchor(plate, 1, layout.NameSize),
                subnameLine, layout.NameSize, nameColor, bottomSeated: true);

        Vector2 levelAnchor = NameplateUiLaw.LevelAnchor(plate, layout.Basis);
        bool skull = reaction == FactionReaction.Hostile && unit.Level >= player.Level + 10 &&
                     !UnitIsGrey(player.Level, unit.Level);
        if (!skull && unit.Level > 0)
        {
            DrawPlateText(draw, levelAnchor, unit.Level.ToString(), layout.LevelSize,
                WithAlpha(ConColorU32(player.Level, unit.Level), alpha), bottomSeated: false);
        }

        byte raidMark = GroupUiLaw.RaidTargetIndex(_partyRaidTargets, unit.Guid);
        if (raidMark > 0)
        {
            uint texture = _gameplayArt.Handle(RaidMarkerUiLaw.Texture);
            if (texture != 0)
            {
                RaidMarkerRect icon = RaidMarkerUiLaw.NameplateRect(
                    plate.Left, plate.Top, plate.Bottom, layout.Basis);
                RaidMarkerUv uv = RaidMarkerUiLaw.AtlasUv(raidMark);
                draw.AddImage((nint)texture, icon.Min, icon.Max, uv.Min, uv.Max, alphaWhite);
            }
        }

        if (skull)
        {
            uint texture = _gameplayArt.Handle(@"Interface\TargetingFrame\UI-TargetingFrame-Skull");
            if (texture != 0)
            {
                NameplateUiLaw.ImageRect skullRect = NameplateUiLaw.Skull(levelAnchor, layout.Basis);
                draw.AddImage((nint)texture, skullRect.Min, skullRect.Max,
                    Vector2.Zero, Vector2.One, alphaWhite);
            }
        }
    }

    /// <summary>The current-frame V-plate spawn verdict used by chat-bubble creation.</summary>
    private bool WouldHaveActiveNameplate(WorldEntity unit, Vector2 display)
    {
        if (SettingsModalOpen ||
            !_enemyNameplatesVisible && !_friendlyNameplatesVisible || _net is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return false;
        bool own = unit.Guid == ControlledGuid;
        bool namesEnabled = own ? Settings.Controls.ShowOwnName :
            unit.IsPlayer ? Settings.Controls.ShowPlayerNames : Settings.Controls.ShowNpcNames;
        if (own || !namesEnabled || unit.IsDead ||
            (unit.Fields.UnitFlags & NotSelectable) != 0 ||
            !NameplateUiLaw.ModeAllows(ReactionTargetTowardPlayer(unit),
                _enemyNameplatesVisible, _friendlyNameplatesVisible))
            return false;
        Vector3 selfPosition = _controller?.Position ?? player.Position;
        if (Vector3.DistanceSquared(selfPosition, UnitWorldPosition(unit)) >
            NameplateUiLaw.RangeYards * NameplateUiLaw.RangeYards) return false;
        Vector3 anchor = UnitWorldPosition(unit) +
            new Vector3(0f, 0f, UnitOverheadHeight(unit) + 2f / 3f);
        return _window.Camera.TryWorldToScreen(anchor, display, out _);
    }

    private NameplateUiLaw.Bounds ResolveVplate(NameplateUiLaw.Bounds desired, Vector2 viewport)
    {
        NameplateUiLaw.Bounds result = ClampRect(desired, viewport);
        int bound = Math.Max(1, ((int)(viewport.X / MathF.Max(result.Width, 1f)) + 1) *
                                ((int)(viewport.Y / MathF.Max(result.Height, 1f)) + 1));
        for (int i = 0; i < bound; i++)
        {
            NameplateUiLaw.Bounds blocker = default;
            bool found = false;
            foreach (NameplateUiLaw.Bounds claim in _vplateClaims)
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
        return new NameplateUiLaw.Bounds(MathF.Round(result.Left), MathF.Round(result.Top),
            MathF.Round(result.Left) + result.Width, MathF.Round(result.Top) + result.Height);
    }

    private static NameplateUiLaw.Bounds ClampRect(NameplateUiLaw.Bounds rect, Vector2 viewport)
    {
        float x = rect.Left;
        float y = rect.Top;
        if (x < 0f) x = 0f;
        if (x + rect.Width > viewport.X) x = MathF.Max(0f, viewport.X - rect.Width);
        if (y < 0f) y = 0f;
        if (y + rect.Height > viewport.Y) y = MathF.Max(0f, viewport.Y - rect.Height);
        return new NameplateUiLaw.Bounds(x, y, x + rect.Width, y + rect.Height);
    }

    private Vector3 UnitWorldPosition(WorldEntity unit) =>
        // In the free view the rig IS the camera — the character stands in the world
        // at its streamed position, and its name must stay planted on it.
        _net is not null && unit.Guid == ControlledGuid && _controller is not null &&
        !_freeView
            ? _controller.Position : unit.Position;

    private float UnitOverheadHeight(WorldEntity unit)
    {
        if (unit.IsCreature && _creatures?.TryGetOverheadHeight(unit, out float height) == true)
            return height;
        if (_net is not null && unit.Guid == ControlledGuid && _character is not null)
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
        else if (GuidInfo.PetNumber(unit.Guid) is uint petNumber)
        {
            if (!_petNames.ContainsKey(petNumber) && _queriedPetNames.Add(petNumber))
                _net.PetNameQuery(petNumber, unit.Guid);
        }
        else if (unit.IsCreature && TryBeginCreatureQuery(unit.Entry))
            _net.CreatureQuery(unit.Entry, unit.Guid);
    }

    private static void DrawPlateText(ImDrawListPtr draw, Vector2 anchor, string text,
        float fontSize, uint color, bool bottomSeated)
    {
        if (fontSize < 1f || text.Length == 0) return;
        ImFontPtr font = ImGui.GetFont();
        Vector2 extent = ImGui.CalcTextSize(text) *
            (fontSize / MathF.Max(ImGui.GetFontSize(), 1f));
        Vector2 position = NameplateUiLaw.TextPosition(anchor, extent, bottomSeated);
        draw.AddText(font, fontSize, position + NameplateUiLaw.TextShadow(fontSize),
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
