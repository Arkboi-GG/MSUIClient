using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;
using MSUIClient.World.Units;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private CharacterEquipment.GuildEmblemDesign? _tabardDesign;
    private ulong _tabardVendorGuid;
    private bool _tabardOpen;
    private uint _tabardStyle, _tabardColor, _tabardBorderStyle, _tabardBorderColor, _tabardBackgroundColor;

    private void InitTabard() { }
    private void ResetTabard()
    {
        _tabardVendorGuid = 0; _tabardOpen = false; _tabardDesign = null;
        _tabardStyle = _tabardColor = _tabardBorderStyle = _tabardBorderColor = _tabardBackgroundColor = 0;
    }

    private bool TabardDesignerEligible(
        ulong guid, out WorldEntity? npc, out float distanceSquared)
    {
        npc = null;
        distanceSquared = float.PositiveInfinity;
        if (_net is not { IsInWorld: true } ||
            !TryGetSessionBodyPose(out WorldBodyPose sessionBody) ||
            !_entities.TryGet(guid, out npc) || !npc.IsCreature || npc.IsDead ||
            (npc.NpcFlags & NpcTabardDesigner) == 0)
            return false;
        distanceSquared = Vector3.DistanceSquared(sessionBody.Position, npc.Position);
        return NpcSessionUiLaw.InRange(distanceSquared);
    }

    private bool RequestTabardDesigner(ulong guid)
    {
        bool eligible = TabardDesignerEligible(
            guid, out WorldEntity? npc, out float distanceSquared);
        bool sent = eligible && _net?.GossipHello(guid) == true;
        EmitInterface("tabard", "open-send", sent ? "SENT" : eligible ? "SEND_FAILED" : "REFUSED", guid,
            $"eligible={eligible};distanceSquared={distanceSquared:R};" +
            $"npcFlags=0x{npc?.NpcFlags ?? 0:X8};" +
            $"body={Convert.ToHexString(WorldSession.BuildBankGuidBody(guid))}");
        return sent;
    }

    private bool UpdateTabardLifecycle()
    {
        if (!_tabardOpen ||
            !TryGetSessionBodyPose(out WorldBodyPose sessionBody)) return false;
        ulong sourceGuid = _tabardVendorGuid;
        bool sourceAvailable = _entities.TryGet(sourceGuid, out WorldEntity vendor) &&
            vendor.IsCreature && !vendor.IsDead &&
            (vendor.NpcFlags & NpcTabardDesigner) != 0;
        float distanceSquared = sourceAvailable
            ? Vector3.DistanceSquared(sessionBody.Position, vendor.Position)
            : float.PositiveInfinity;
        if (!NpcSessionUiLaw.ShouldClose(true, true, sourceAvailable, distanceSquared))
            return false;
        _tabardOpen = false;
        _tabardVendorGuid = 0;
        EmitInterface("tabard", "lifecycle-close", "CLOSED", sourceGuid,
            sourceAvailable
                ? $"distanceSquared={distanceSquared:R};" +
                  $"limitSquared={NpcSessionUiLaw.ServiceRangeSquared:R}"
                : "source-unavailable");
        return true;
    }

    private void ApplyTabardVendorActivate(byte[] body)
    {
        if (body.Length != 8) { EmitInterface("tabard", "activate", "MALFORMED", 0, $"bytes={body.Length}"); return; }
        var r = new PacketReader(body); ulong guid = r.ReadU64();
        bool eligible = TabardDesignerEligible(
            guid, out WorldEntity? npc, out float distanceSquared);
        _tabardVendorGuid = eligible ? guid : 0;
        _tabardOpen = eligible;
        EmitInterface("tabard", "activate", eligible ? "OPEN" : "REFUSED", guid,
            $"distanceSquared={distanceSquared:R};npcFlags=0x{npc?.NpcFlags ?? 0:X8};" +
            $"body={Convert.ToHexString(body)}");
    }

    private bool SaveTabardDesign(uint style, uint color, uint borderStyle, uint borderColor, uint backgroundColor)
    {
        bool range = style <= 99 && color <= 16 && borderStyle <= 5 && borderColor <= 16 && backgroundColor <= 50;
        float distanceSquared = float.PositiveInfinity;
        bool eligible = _tabardOpen && TabardDesignerEligible(
            _tabardVendorGuid, out _, out distanceSquared);
        byte[] body = WorldSession.BuildSaveGuildEmblemBody(_tabardVendorGuid, style, color,
            borderStyle, borderColor, backgroundColor);
        bool sent = range && eligible && _net?.SaveGuildEmblem(_tabardVendorGuid, style, color,
            borderStyle, borderColor, backgroundColor) == true;
        EmitInterface("tabard", "save-send", sent ? "SENT" : !range ? "REFUSED-RANGE" : !eligible ? "REFUSED-NPC" : "SEND_FAILED",
            _tabardVendorGuid, $"style={style};color={color};border={borderStyle};" +
            $"borderColor={borderColor};background={backgroundColor};" +
            $"distanceSquared={distanceSquared:R};body={Convert.ToHexString(body)}");
        if (sent)
        {
            _tabardStyle = style; _tabardColor = color; _tabardBorderStyle = borderStyle;
            _tabardBorderColor = borderColor; _tabardBackgroundColor = backgroundColor;
        }
        return sent;
    }

    private void ApplySaveGuildEmblemResult(byte[] body)
    {
        if (body.Length != 4) { EmitInterface("tabard", "save-result", "MALFORMED", _tabardVendorGuid, $"bytes={body.Length}"); return; }
        var r = new PacketReader(body); uint error = r.ReadU32();
        string outcome = error == 0 ? "SUCCESS" : $"FAILED-{error}";
        if (error == 0)
        {
            _tabardDesign = new(_tabardStyle, _tabardColor, _tabardBorderStyle, _tabardBorderColor, _tabardBackgroundColor);
            if (_character is not null) { _character.Equipment.GuildEmblem = _tabardDesign; _character.Reload(); }
            _paperDollDirty = true;
        }
        EmitInterface("tabard", "save-result", outcome, _tabardVendorGuid,
            $"error={error};style={_tabardStyle};color={_tabardColor};border={_tabardBorderStyle};borderColor={_tabardBorderColor};background={_tabardBackgroundColor};body={Convert.ToHexString(body)}");
    }

    private void SimulateTabardFlow(uint style, uint color, uint borderStyle, uint borderColor, uint backgroundColor)
    {
        _tabardStyle = style; _tabardColor = color; _tabardBorderStyle = borderStyle;
        _tabardBorderColor = borderColor; _tabardBackgroundColor = backgroundColor;
        _tabardDesign = new(style, color, borderStyle, borderColor, backgroundColor);
        _tabardOpen = true;
        if (_character is not null) { _character.Equipment.GuildEmblem = _tabardDesign; _character.Reload(); }
        _paperDollDirty = true;
        EmitInterface("tabard", "activate", "OPEN", 0, "source=runtime-replay;npcFlags=0x00000400");
        EmitInterface("tabard", "save-result", "SUCCESS", 0,
            $"source=runtime-replay;style={style};color={color};border={borderStyle};borderColor={borderColor};background={backgroundColor}");
        EmitInterface("tabard", "render-binding", "VERIFIED", 0,
            $"upper=Background_{backgroundColor:D2}_TU_U|Border_{borderStyle:D2}_{borderColor:D2}_TU_U|Emblem_{style:D2}_{color:D2}_TU_U;lower=Background_{backgroundColor:D2}_TL_U|Border_{borderStyle:D2}_{borderColor:D2}_TL_U|Emblem_{style:D2}_{color:D2}_TL_U");
    }

    private void DrawTabardFrame()
    {
        if (!_tabardOpen || _gameplayArt is null) return;
        if (!BeginVanillaWindow("##tabard", TabardFrameUiLaw.Frame.Min,
                TabardFrameUiLaw.Frame.Size,
                out ImDrawListPtr dl, out Vector2 origin, out float s)) { ImGui.End(); return; }
        DrawFourPieceShell(dl, origin, s,
            @"Interface\PaperDollInfoFrame\UI-Character-General-TopLeft",
            @"Interface\PaperDollInfoFrame\UI-Character-General-TopRight",
            @"Interface\ClassTrainerFrame\UI-ClassTrainer-BotLeft",
            @"Interface\ClassTrainerFrame\UI-ClassTrainer-BotRight");
        DrawArt(dl, @"Interface\TabardFrame\TabardFrameBackground",
            origin + TabardFrameUiLaw.Background.Min * s,
            TabardFrameUiLaw.Background.Size, s);
        DrawCenteredText(dl, origin + TabardFrameUiLaw.TitleCenter * s,
            "Guild Tabard", 14f * s, VanillaGold);
        DrawCenteredText(dl, origin + TabardFrameUiLaw.SubtitleCenter * s,
            "Choose your guild emblem", 11f * s, 0xffffffff);

        string[] labels = ["Emblem Symbol", "Emblem Color", "Border", "Border Color", "Background"];
        uint[] values = [_tabardStyle, _tabardColor, _tabardBorderStyle, _tabardBorderColor, _tabardBackgroundColor];
        uint[] maxima = [99, 16, 5, 16, 50];
        for (int i = 0; i < labels.Length; i++)
        {
            TabardFrameUiLaw.SelectorLayout selector = TabardFrameUiLaw.Selector(i);
            DrawCenteredText(dl, origin + selector.LabelCenter * s,
                labels[i], 9f * s, 0xffffffff);
            if (TabardArrow(dl, $"##tabard-prev-{i}", origin,
                    selector.Previous, false, s))
                values[i] = values[i] == 0 ? maxima[i] : values[i] - 1;
            if (TabardArrow(dl, $"##tabard-next-{i}", origin,
                    selector.Next, true, s))
                values[i] = values[i] >= maxima[i] ? 0 : values[i] + 1;
            DrawCenteredText(dl, origin + selector.ValueCenter * s,
                values[i].ToString(), 10f * s, VanillaGold);
        }
        (_tabardStyle, _tabardColor, _tabardBorderStyle, _tabardBorderColor, _tabardBackgroundColor) =
            (values[0], values[1], values[2], values[3], values[4]);

        GameText.DrawPlain(dl, "Cost: 10 gold", origin + TabardFrameUiLaw.Cost * s,
            10f, s, 0xffffffff);
        if (VanillaButton(dl, "##tabard-accept", "Accept",
                origin + TabardFrameUiLaw.Accept.Min * s,
                TabardFrameUiLaw.Accept.Size, s))
            SaveTabardDesign(_tabardStyle, _tabardColor, _tabardBorderStyle,
                _tabardBorderColor, _tabardBackgroundColor);
        if (VanillaButton(dl, "##tabard-cancel", "Cancel",
                origin + TabardFrameUiLaw.Cancel.Min * s,
                TabardFrameUiLaw.Cancel.Size, s)) _tabardOpen = false;
        DrawImageButton(dl, "##tabard-close", origin + TabardFrameUiLaw.Close.Min * s,
            TabardFrameUiLaw.Close.Size * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) _tabardOpen = false;
        ImGui.End();
    }

    private bool TabardArrow(ImDrawListPtr dl, string id, Vector2 origin,
        TabardFrameUiLaw.LogicalRect rect, bool next, float s)
    {
        string stem = next ? "Next" : "Prev";
        return DrawImageButtonClicked(dl, id, origin + rect.Min * s, rect.Size * s,
            $@"Interface\Buttons\UI-SpellbookIcon-{stem}Page-Up",
            $@"Interface\Buttons\UI-SpellbookIcon-{stem}Page-Down",
            @"Interface\Buttons\UI-Common-MouseHilight");
    }

    private bool DrawImageButtonClicked(ImDrawListPtr dl, string id, Vector2 min, Vector2 size,
        string normal, string pushed, string highlight)
    {
        DrawImageButton(dl, id, min, size, normal, pushed, highlight);
        return ImGui.IsItemClicked();
    }
}
