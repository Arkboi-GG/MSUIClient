using System.Numerics;
using ImGuiNET;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed record PartyMember(ulong Guid, string Name, byte Online, byte Subgroup, byte Flags);
    private readonly List<PartyMember> _partyMembers = [];
    private ulong _partyLeaderGuid;

    // Build-5875 SMSG_GROUP_LIST, mirrored from the local vmangos Group::SendUpdate:
    // group type, local subgroup/assistant flags, count of other members,
    // member rows, then leader and loot data. A disband is three zero u64s.
    private void ApplyPartyRoster(byte[] body)
    {
        if (body.Length == 24 && body.All(b => b == 0))
        {
            _partyMembers.Clear();
            _partyLeaderGuid = 0;
            return;
        }
        var r = new PacketReader(body);
        r.ReadU8(); // group type
        r.ReadU8(); // local subgroup | assistant flag
        uint count = r.ReadU32();
        if (count > 39) throw new InvalidDataException($"SMSG_GROUP_LIST member count {count}");
        _partyMembers.Clear();
        for (uint i = 0; i < count; i++)
        {
            string name = r.ReadCString();
            ulong guid = r.ReadU64();
            byte online = r.ReadU8();
            byte memberFlags = r.ReadU8();
            _partyMembers.Add(new PartyMember(guid, name, online,
                (byte)(memberFlags & 0x7f), (byte)(memberFlags & 0x80)));
            if (!_playerNames.ContainsKey(guid)) _playerNames[guid] = name;
        }
        _partyLeaderGuid = r.ReadU64();
    }

    private void DrawPartyFrames()
    {
        if (_net is null || _gameplayArt is null) return;
        bool proof = _uiParityArmed && _uiParityPanel == "party-frame";
        if (_partyMembers.Count == 0 && !proof) return;
        for (int i = 0; i < Math.Min(4, Math.Max(_partyMembers.Count, proof ? 1 : 0)); i++)
        {
            PartyMember? member = i < _partyMembers.Count ? _partyMembers[i] : null;
            WorldEntity? unit = member is not null && _entities.TryGet(member.Guid, out WorldEntity found)
                ? found : proof && _entities.TryGet(_net.PlayerGuid, out WorldEntity player) ? player : null;
            DrawPartyMemberFrame(i, member, unit);
        }
    }

    private void DrawPartyMemberFrame(int index, PartyMember? member, WorldEntity? unit)
    {
        if (_gameplayArt is null) return;
        float s = GameplayUiScale();
        Vector2 authored = new(10, 128 + index * 83);
        Vector2 p = authored * s;
        ImGui.SetNextWindowPos(p, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(128, 73) * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav;
        if (!ImGui.Begin($"##vanilla-party-member-{index + 1}", flags)) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        string root = $"PartyMemberFrame{index + 1}";

        if (_uiParityArmed && _uiParityPanel == "party-frame" && index == 0)
        {
            BeginUiParityFrame(p, s);
            CollectUiParity(root, "Button", p, new Vector2(128, 53) * s, parent: "",
                point: "TOPLEFT", offsetX: "10", offsetY: "-128", strata: "LOW");
            CollectUiParity(root + "Portrait", "Texture", p + new Vector2(7, 6) * s,
                new Vector2(37) * s, parent: root, point: "TOPLEFT", offsetX: "7", offsetY: "-6",
                layer: "BACKGROUND", strata: "LOW");
            CollectUiParity(root + "/Frame/FrameTexture", "Texture", p + new Vector2(0, 2) * s,
                new Vector2(128, 64) * s, parent: root + "/Frame/Frame", point: "TOPLEFT",
                offsetX: "0", offsetY: "-2", texture: @"Interface\TargetingFrame\UI-PartyFrame",
                layer: "BACKGROUND", strata: "LOW");
            CollectUiParity(root + "HealthBar", "StatusBar", p + new Vector2(47, 12) * s,
                new Vector2(70, 8) * s, parent: root, point: "TOPLEFT", offsetX: "47", offsetY: "-12",
                texture: @"Interface\TargetingFrame\UI-StatusBar", strata: "LOW");
            CollectUiParity(root + "ManaBar", "StatusBar", p + new Vector2(47, 21) * s,
                new Vector2(70, 8) * s, parent: root, point: "TOPLEFT", offsetX: "47", offsetY: "-21",
                texture: @"Interface\TargetingFrame\UI-StatusBar", strata: "LOW");
            CollectUiParity(root + "PetFrame", "Button", p + new Vector2(20, 47) * s,
                new Vector2(64, 26) * s, parent: root, point: "TOPLEFT", offsetX: "20", offsetY: "-47",
                strata: "LOW");
        }

        Vector2 portraitMin = p + new Vector2(7, 6) * s;
        if (unit is not null) DrawUnitPortraitImage(dl, unit, portraitMin, 37 * s, 0, false);
        uint frame = _gameplayArt.Handle(@"Interface\TargetingFrame\UI-PartyFrame");
        if (frame != 0) dl.AddImage((nint)frame, p + new Vector2(0, 2) * s,
            p + new Vector2(128, 66) * s);
        float health = unit?.HealthFraction ?? 0;
        float power = unit?.PowerFraction ?? 0;
        DrawVanillaStatusBar(dl, p + new Vector2(47, 12) * s, new Vector2(70, 8) * s,
            health, new Vector4(0, 1, 0, 1));
        DrawVanillaStatusBar(dl, p + new Vector2(47, 21) * s, new Vector2(70, 8) * s,
            power, unit is null ? new Vector4(0, 0, 1, 1) : PowerColor(unit.Fields.PowerType));
        string name = member?.Name ?? _net?.PlayerName ?? "Player";
        DrawUnitFrameText(dl, p + new Vector2(82, 8) * s, name, 10 * s, UiGoldU32());
        if (member?.Guid == _partyLeaderGuid)
            DrawArt(dl, @"Interface\GroupFrame\UI-Group-LeaderIcon", p, new Vector2(16), s);
        if (_uiParityArmed && _uiParityPanel == "party-frame" && index == 0) MarkUiParityFrameComplete();
        ImGui.End();
    }
}
