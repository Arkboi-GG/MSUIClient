using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _inspectOpen;
    private ulong _inspectGuid;
    private float _inspectRotation = InspectUiLaw.DefaultFacing;

    /// <summary>
    /// The reply is only an echoed guid. It is not the gate that opens the window: build 5875
    /// calls NotifyInspect and ShowUIPanel in the same UI gesture because worn gear is already in
    /// the public PLAYER_VISIBLE_ITEM fields.
    /// </summary>
    private void ApplyInspect(byte[] body)
    {
        if (body.Length != 8) throw new InvalidDataException($"SMSG_INSPECT has {body.Length} bytes");
        ulong guid = new PacketReader(body).ReadU64();
        if (_inspectOpen && guid != _inspectGuid)
            Console.WriteLine($"[inspect] ignored stale reply for 0x{guid:X16}; active=0x{_inspectGuid:X16}");
    }

    private bool RequestInspect(ulong guid)
    {
        if (_net is null || _controller is null ||
            !_entities.TryGet(guid, out WorldEntity unit)) return false;
        bool canInspect = InspectUiLaw.CanInspect(
            unit.IsPlayer, guid == _net.PlayerGuid, CanAttack(unit),
            Vector3.DistanceSquared(_controller.Position, unit.Position));
        if (!canInspect) return false;

        if (_inspectOpen && _inspectGuid == guid) return true;
        if (_inspectOpen) CloseInspect(playSound: true);
        if (!_net.Inspect(guid)) return false;

        // Inspect is a left UIPanel. Preserve MSUI's existing panels, but do not stack them in the
        // same slot behind this one.
        _characterOpen = false;
        _spellbookOpen = false;
        _talentOpen = false;
        _inspectGuid = guid;
        _inspectRotation = InspectUiLaw.DefaultFacing;
        _inspectOpen = true;
        _inspectPaperDollDirty = true;
        PlayUiSound("igCharacterInfoOpen");
        return true;
    }

    private void CloseInspect(bool playSound)
    {
        if (!_inspectOpen) return;
        _inspectOpen = false;
        _inspectGuid = 0;
        _inspectPaperDollGuid = 0;
        _inspectPaperDollUsable = false;
        if (playSound) PlayUiSound("igCharacterInfoClose");
    }

    private void UpdateInspectLifecycle()
    {
        if (!_inspectOpen) return;
        // The shipped InspectFrame watches the target token even when it was reached from another
        // unit token: clearing target closes; retargeting re-runs the complete inspect gate.
        if (_selectionGuid == 0 || !_entities.TryGet(_selectionGuid, out _))
        {
            CloseInspect(playSound: true);
            return;
        }
        if (_selectionGuid != _inspectGuid && !RequestInspect(_selectionGuid))
            CloseInspect(playSound: true);
    }

    private void DrawInspectFrame()
    {
        if (!_inspectOpen || _items is null || _gameplayArt is null ||
            !_entities.TryGet(_inspectGuid, out WorldEntity player)) return;
        float s = GameplayUiScale();
        Vector2 p = new(0, 104 * s);
        ImGui.SetNextWindowPos(p, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(384, 512) * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##inspect-frame", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav))
        { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        DrawPaperDollBackground(dl, p, s);
        DrawUnitPortraitImage(dl, player, p + new Vector2(7, 6) * s, 60 * s, 0, false);
        string name = _playerNames.GetValueOrDefault(player.Guid, "Player");
        GameText.DrawCentered(dl, "GameFontNormal", name,
            p + new Vector2(198, 24) * s, s, 0xffffffff);
        var b = player.Fields.Bytes0;
        GameText.DrawCentered(dl, "GameFontNormalSmall",
            $"Level {player.Level} {RaceName(b.Race)} {ClassName(b.Class)}",
            p + new Vector2(198, 41) * s, s);

        Vector2 modelMin = p + new Vector2(65, 78) * s;
        if (_inspectPaperDoll is not null && _inspectPaperDollUsable)
            dl.AddImage((nint)_inspectPaperDoll.TextureHandle, modelMin,
                modelMin + new Vector2(233, 300) * s, new Vector2(0, 1), new Vector2(1, 0));
        DrawInspectRotationButton(dl, p + new Vector2(65, 78) * s, left: true, s);
        DrawInspectRotationButton(dl, p + new Vector2(100, 78) * s, left: false, s);

        for (int i = 0; i < LeftPaperDollSlots.Length; i++)
            DrawInspectSlot(dl, p + new Vector2(21, 74 + i * 41) * s, s, player,
                LeftPaperDollSlots[i].Slot, LeftPaperDollSlots[i].Empty);
        for (int i = 0; i < RightPaperDollSlots.Length; i++)
            DrawInspectSlot(dl, p + new Vector2(305, 74 + i * 41) * s, s, player,
                RightPaperDollSlots[i].Slot, RightPaperDollSlots[i].Empty);
        for (int i = 0; i < WeaponPaperDollSlots.Length; i++)
            DrawInspectSlot(dl, p + new Vector2(122 + i * 42, 385) * s, s, player,
                WeaponPaperDollSlots[i].Slot, WeaponPaperDollSlots[i].Empty);

        // The one shipped Character tab occupies the reference tab row. Clicking the already-open
        // page closes the inspect window; this odd toggle is the original behavior.
        float tabWidth = VanillaCharacterTabWidth("Character", s, 0);
        if (VanillaTab(dl, "##inspect-tab-character",
            p + new Vector2(60 - tabWidth * .5f, 434) * s,
            "Character", tabWidth, s, selected: true))
            CloseInspect(playSound: true);

        Vector2 close = p + new Vector2(324, 9) * s;
        DrawImageButton(dl, "##inspect-close", close, new Vector2(32) * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) CloseInspect(playSound: true);
        ImGui.End();
    }

    private void DrawInspectRotationButton(ImDrawListPtr dl, Vector2 min, bool left, float s)
    {
        string stem = left ? "UI-RotationLeft-Button" : "UI-RotationRight-Button";
        DrawImageButton(dl, left ? "##inspect-left" : "##inspect-right", min, new Vector2(35) * s,
            $@"Interface\Buttons\{stem}-Up", $@"Interface\Buttons\{stem}-Down",
            @"Interface\Buttons\ButtonHilight-Round");
        bool changed = false;
        if (ImGui.IsItemClicked())
        {
            _inspectRotation = InspectUiLaw.ClickFacing(_inspectRotation, left);
            PlayUiSound("igInventoryRotateCharacter");
            changed = true;
        }
        if (ImGui.IsItemActive())
        {
            _inspectRotation = InspectUiLaw.HeldFacing(
                _inspectRotation, left, ImGui.GetIO().DeltaTime);
            changed = true;
        }
        if (changed) _inspectPaperDollDirty = true;
    }

    private void DrawInspectSlot(ImDrawListPtr dl, Vector2 min, float s, WorldEntity player,
        int slot, string emptySuffix)
    {
        Vector2 max = min + new Vector2(37) * s;
        uint entry = player.Fields.PlayerVisibleItemEntry(slot);
        ItemTemplate? item = null;
        if (entry != 0 && _net is not null)
        {
            _items!.Require(entry, 0, _net);
            _items.TryGet(entry, out item);
        }
        uint icon = _gameplayArt?.Handle(item?.IconPath ??
            $@"Interface\Paperdoll\UI-PaperDoll-Slot-{emptySuffix}") ?? 0;
        if (icon != 0) dl.AddImage((nint)icon, min, max);
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton($"##inspect-slot-{slot}", max - min);
        if (ImGui.IsItemHovered())
        {
            if (item is not null) DrawItemTooltip(item, 1);
            else ImGui.SetTooltip(emptySuffix);
            uint hi = _gameplayArt?.Handle(@"Interface\Buttons\ButtonHilight-Square") ?? 0;
            if (hi != 0) dl.AddImage((nint)hi, min, max);
        }
        uint ring = _gameplayArt?.Handle(@"Interface\Buttons\UI-Quickslot2") ?? 0;
        if (ring != 0)
        {
            Vector2 center = (min + max) * .5f + new Vector2(0, -s);
            dl.AddImage((nint)ring, center - new Vector2(32 * s), center + new Vector2(32 * s));
        }
    }
}
