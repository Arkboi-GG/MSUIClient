using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _durabilityFrameShown;

    private void DrawDurabilityFrame()
    {
        _durabilityFrameShown = false;
        if (_gameplayArt is null || _net is null ||
            !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return;

        var statuses = new byte[DurabilityFrameUiLaw.EquipmentSlots.Length];
        for (int i = 0; i < statuses.Length; i++)
        {
            ulong guid = player.Fields.PlayerInventorySlot(DurabilityFrameUiLaw.EquipmentSlots[i]);
            if (guid == 0 || !_entities.TryGet(guid, out WorldEntity item)) continue;
            statuses[i] = DurabilityFrameUiLaw.AlertStatus(item.Fields.ItemFlags,
                item.Fields.ItemDurability, item.Fields.ItemMaxDurability);
        }
        if (!DurabilityFrameUiLaw.FrameShown(statuses)) return;
        _durabilityFrameShown = true;

        bool body = DurabilityFrameUiLaw.BodyShown(statuses);
        bool side = DurabilityFrameUiLaw.SideShown(statuses);
        bool offhandWeapon = false;
        ulong offhandGuid = player.Fields.PlayerInventorySlot(16);
        if (offhandGuid != 0 && _entities.TryGet(offhandGuid, out WorldEntity offhand))
        {
            _items?.Require(offhand.Entry, offhand.Guid, _net);
            offhandWeapon = _items?.TryGet(offhand.Entry, out ItemTemplate? template) == true &&
                template is { Class: 2 };
        }

        float scale = GameplayUiScale();
        Vector2 origin = DurabilityFrameUiLaw.FrameOrigin(ImGui.GetIO().DisplaySize, scale, side,
            _questTimerFrameHeight);
        uint atlas = _gameplayArt.Handle(@"Interface\Durability\UI-Durability-Icons");
        if (atlas == 0) return;
        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
        foreach (DurabilityGlyph glyph in DurabilityFrameUiLaw.Glyphs)
        {
            if (glyph.Body && !body) continue;
            if (!glyph.Body && statuses[glyph.AlertIndex] is not (3 or 4)) continue;
            if (glyph.Kind == DurabilityGlyphKind.Shield && offhandWeapon) continue;
            if (glyph.Kind == DurabilityGlyphKind.OffWeapon && !offhandWeapon) continue;
            Vector2 min = origin + glyph.Min * scale;
            draw.AddImage((nint)atlas, min, min + glyph.Size * scale,
                glyph.UvMin, glyph.UvMax,
                ImGui.ColorConvertFloat4ToU32(DurabilityFrameUiLaw.Color(statuses[glyph.AlertIndex])));
        }
    }
}
