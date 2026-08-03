using System.Numerics;
using ImGuiNET;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _inspectOpen;
    private ulong _inspectGuid;

    private void ApplyInspect(byte[] body)
    {
        if (body.Length != 8) throw new InvalidDataException($"SMSG_INSPECT has {body.Length} bytes");
        ulong guid = new PacketReader(body).ReadU64();
        if (guid == 0 || !_entities.TryGet(guid, out WorldEntity unit) || !unit.IsPlayer) return;
        _inspectGuid = guid;
        _inspectOpen = true;
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
        DrawCenteredText(dl, p + new Vector2(198, 24) * s, name, 12 * s, 0xffffffff);
        var b = player.Fields.Bytes0;
        DrawCenteredText(dl, p + new Vector2(198, 41) * s,
            $"Level {player.Level} {RaceName(b.Race)} {ClassName(b.Class)}", 10 * s, VanillaGold);

        for (int i = 0; i < LeftPaperDollSlots.Length; i++)
            DrawInspectSlot(dl, p + new Vector2(21, 74 + i * 41) * s, s, player,
                LeftPaperDollSlots[i].Slot, LeftPaperDollSlots[i].Empty);
        for (int i = 0; i < RightPaperDollSlots.Length; i++)
            DrawInspectSlot(dl, p + new Vector2(305, 74 + i * 41) * s, s, player,
                RightPaperDollSlots[i].Slot, RightPaperDollSlots[i].Empty);
        for (int i = 0; i < WeaponPaperDollSlots.Length; i++)
            DrawInspectSlot(dl, p + new Vector2(122 + i * 42, 348) * s, s, player,
                WeaponPaperDollSlots[i].Slot, WeaponPaperDollSlots[i].Empty);

        DrawCenteredText(dl, p + new Vector2(192, 403) * s, "Inspect", 12 * s, VanillaGold);
        DrawCenteredText(dl, p + new Vector2(192, 425) * s,
            "Equipment shown from the player's public build-5875 fields", 9 * s, 0xffcccccc);
        Vector2 close = p + new Vector2(324, 9) * s;
        DrawImageButton(dl, "##inspect-close", close, new Vector2(32) * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) _inspectOpen = false;
        ImGui.End();
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
        if (ImGui.IsItemHovered() && item is not null) DrawItemTooltip(item, 1);
        uint ring = _gameplayArt?.Handle(@"Interface\Buttons\UI-Quickslot2") ?? 0;
        if (ring != 0)
        {
            Vector2 center = (min + max) * .5f + new Vector2(0, -s);
            dl.AddImage((nint)ring, center - new Vector2(32 * s), center + new Vector2(32 * s));
        }
    }
}
