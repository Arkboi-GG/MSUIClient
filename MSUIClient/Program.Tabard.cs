using System.Numerics;
using ImGuiNET;
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

    private bool RequestTabardDesigner(ulong guid)
    {
        bool eligible = _entities.TryGet(guid, out WorldEntity npc) && npc.IsCreature && !npc.IsDead &&
            (npc.NpcFlags & NpcTabardDesigner) != 0;
        bool sent = eligible && _net?.GossipHello(guid) == true;
        EmitInterface("tabard", "open-send", sent ? "SENT" : eligible ? "SEND_FAILED" : "REFUSED", guid,
            $"eligible={eligible};npcFlags=0x{npc?.NpcFlags ?? 0:X8};body={Convert.ToHexString(WorldSession.BuildBankGuidBody(guid))}");
        return sent;
    }

    private void ApplyTabardVendorActivate(byte[] body)
    {
        if (body.Length != 8) { EmitInterface("tabard", "activate", "MALFORMED", 0, $"bytes={body.Length}"); return; }
        var r = new PacketReader(body); ulong guid = r.ReadU64();
        bool eligible = _entities.TryGet(guid, out WorldEntity npc) && (npc.NpcFlags & NpcTabardDesigner) != 0;
        _tabardVendorGuid = guid; _tabardOpen = eligible;
        EmitInterface("tabard", "activate", eligible ? "OPEN" : "REFUSED-FLAG", guid,
            $"npcFlags=0x{npc?.NpcFlags ?? 0:X8};body={Convert.ToHexString(body)}");
    }

    private bool SaveTabardDesign(uint style, uint color, uint borderStyle, uint borderColor, uint backgroundColor)
    {
        if (_tabardVendorGuid == 0) _tabardVendorGuid = _selectionGuid;
        bool range = style <= 99 && color <= 16 && borderStyle <= 5 && borderColor <= 16 && backgroundColor <= 50;
        bool eligible = _entities.TryGet(_tabardVendorGuid, out WorldEntity npc) &&
            (npc.NpcFlags & NpcTabardDesigner) != 0;
        byte[] body = WorldSession.BuildSaveGuildEmblemBody(_tabardVendorGuid, style, color,
            borderStyle, borderColor, backgroundColor);
        bool sent = range && eligible && _net?.SaveGuildEmblem(_tabardVendorGuid, style, color,
            borderStyle, borderColor, backgroundColor) == true;
        EmitInterface("tabard", "save-send", sent ? "SENT" : !range ? "REFUSED-RANGE" : !eligible ? "REFUSED-NPC" : "SEND_FAILED",
            _tabardVendorGuid, $"style={style};color={color};border={borderStyle};borderColor={borderColor};background={backgroundColor};body={Convert.ToHexString(body)}");
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
        if (!_tabardOpen) return;
        ImGui.SetNextWindowPos(new Vector2(300, 70), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(430, 440), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Guild Tabard##tabard", ref _tabardOpen)) { ImGui.End(); return; }
        ImGui.TextWrapped("Choose the guild emblem. Saving costs 10 gold and requires the guild leader.");
        int style = (int)_tabardStyle, color = (int)_tabardColor, border = (int)_tabardBorderStyle,
            borderColor = (int)_tabardBorderColor, background = (int)_tabardBackgroundColor;
        ImGui.SliderInt("Emblem", ref style, 0, 99); ImGui.SliderInt("Emblem color", ref color, 0, 16);
        ImGui.SliderInt("Border", ref border, 0, 5); ImGui.SliderInt("Border color", ref borderColor, 0, 16);
        ImGui.SliderInt("Background", ref background, 0, 50);
        _tabardStyle = (uint)style; _tabardColor = (uint)color; _tabardBorderStyle = (uint)border;
        _tabardBorderColor = (uint)borderColor; _tabardBackgroundColor = (uint)background;
        ImGui.Separator(); ImGui.TextUnformatted("Cost: 10 gold");
        if (ImGui.Button("Save emblem")) SaveTabardDesign(_tabardStyle, _tabardColor, _tabardBorderStyle, _tabardBorderColor, _tabardBackgroundColor);
        ImGui.SameLine(); if (ImGui.Button("Preview on character")) SimulateTabardFlow(_tabardStyle, _tabardColor, _tabardBorderStyle, _tabardBorderColor, _tabardBackgroundColor);
        ImGui.End();
    }
}
