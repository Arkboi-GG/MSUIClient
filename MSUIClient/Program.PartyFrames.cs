using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed record PartyMember(ulong Guid, string Name, byte Status, byte Subgroup, byte Flags);

    private sealed class PartyMemberStats
    {
        public byte? Status;
        public ushort? Health;
        public ushort? MaxHealth;
        public byte? PowerType;
        public ushort? Power;
        public ushort? MaxPower;
        public ushort? Level;
    }

    private readonly List<PartyMember> _partyMembers = [];
    private readonly Dictionary<ulong, PartyMemberStats> _partyStats = [];
    private ulong _partyLeaderGuid;
    private ulong _partyMasterLooterGuid;
    private byte _partyLootMethod;
    private string? _partyInviter;
    private long _partyInviteDeadline;
    private bool _partyProofStaged;

    private void ResetParty()
    {
        _partyMembers.Clear();
        _partyStats.Clear();
        _partyLeaderGuid = 0;
        _partyMasterLooterGuid = 0;
        _partyLootMethod = 0;
        _partyProofStaged = false;
        CancelPartyInviteFromServer();
    }

    private bool StagePartyFrameProof()
    {
        if (_net is null) return false;
        ulong own = _net.PlayerGuid;
        ulong[] guids = [own, 0xF13000000000A001, 0xF13000000000A002, 0xF13000000000A003];
        byte[] status =
        [
            PartyFrameUiLaw.Online | PartyFrameUiLaw.Pvp,
            0,
            PartyFrameUiLaw.Online | PartyFrameUiLaw.Dead,
            PartyFrameUiLaw.Online | PartyFrameUiLaw.Ghost | PartyFrameUiLaw.PvpFfa,
        ];
        string[] names = [_net.PlayerName, "Offline Member", "Dead Member", "Ghost Member"];
        _partyMembers.Clear();
        _partyStats.Clear();
        for (int i = 0; i < PartyFrameUiLaw.MemberCount; i++)
        {
            _partyMembers.Add(new PartyMember(guids[i], names[i], status[i], 0, 0));
            _partyStats[guids[i]] = new PartyMemberStats
            {
                Status = status[i], Health = (ushort)(i == 0 ? 15 : i == 1 ? 100 : 0),
                MaxHealth = 100, PowerType = 0, Power = (ushort)(70 - i * 10), MaxPower = 100,
                Level = 60,
            };
            _playerNames[guids[i]] = names[i];
        }
        _partyLeaderGuid = guids[0];
        _partyLootMethod = 2;
        _partyMasterLooterGuid = guids[2];
        _partyProofStaged = true;
        return true;
    }

    private void StagePartyInviteProof(string inviter)
    {
        _partyInviter = inviter;
        _partyInviteDeadline = Stopwatch.GetTimestamp() +
            (long)(PartyFrameUiLaw.InviteTimeoutSeconds * Stopwatch.Frequency);
    }

    // Build-5875 SMSG_GROUP_LIST: group type, local flags, recipient-excluded members, leader,
    // and (only when count > 0) loot method/master/threshold plus dungeon difficulty.
    private void ApplyPartyRoster(byte[] body)
    {
        var r = new PacketReader(body);
        r.ReadU8(); // group type
        r.ReadU8(); // local subgroup | assistant flag
        uint count = r.ReadU32();
        if (count > 39) throw new InvalidDataException($"SMSG_GROUP_LIST member count {count}");

        var next = new List<PartyMember>((int)count);
        for (uint i = 0; i < count; i++)
        {
            string name = r.ReadCString();
            ulong guid = r.ReadU64();
            byte status = r.ReadU8();
            byte memberFlags = r.ReadU8();
            next.Add(new PartyMember(guid, name, status,
                (byte)(memberFlags & 0x7f), (byte)(memberFlags & 0x80)));
            if (!_playerNames.ContainsKey(guid)) _playerNames[guid] = name;
        }

        ulong leader = r.ReadU64();
        byte lootMethod = 0;
        ulong master = 0;
        if (count > 0)
        {
            lootMethod = r.ReadU8();
            master = r.ReadU64();
            r.ReadU8(); // loot threshold
            r.ReadU8(); // dungeon difficulty, unused in 1.x
        }
        if (r.Remaining != 0)
            throw new InvalidDataException($"SMSG_GROUP_LIST trailing bytes {r.Remaining}");

        _partyMembers.Clear();
        _partyMembers.AddRange(next);
        _partyLeaderGuid = leader;
        _partyLootMethod = lootMethod;
        _partyMasterLooterGuid = lootMethod == 2 ? master : 0;

        HashSet<ulong> retained = next.Select(x => x.Guid).ToHashSet();
        foreach (ulong guid in _partyStats.Keys.Where(x => !retained.Contains(x)).ToArray())
            _partyStats.Remove(guid);
        foreach (PartyMember member in next)
            if (PartyFrameUiLaw.Has(member.Status, PartyFrameUiLaw.Online) &&
                !_entities.TryGet(member.Guid, out _))
                _net?.RequestPartyMemberStats(member.Guid);
    }

    private void ApplyPartyMemberStats(byte[] body)
    {
        var r = new PacketReader(body);
        ulong guid = r.ReadPackedGuid();
        uint mask = r.ReadU32();
        if (!_partyStats.TryGetValue(guid, out PartyMemberStats? stats))
            _partyStats[guid] = stats = new PartyMemberStats();

        if ((mask & 0x000001) != 0) stats.Status = r.ReadU8();
        if ((mask & 0x000002) != 0) stats.Health = r.ReadU16();
        if ((mask & 0x000004) != 0) stats.MaxHealth = r.ReadU16();
        if ((mask & 0x000008) != 0) stats.PowerType = r.ReadU8();
        if ((mask & 0x000010) != 0) stats.Power = r.ReadU16();
        if ((mask & 0x000020) != 0) stats.MaxPower = r.ReadU16();
        if ((mask & 0x000040) != 0) stats.Level = r.ReadU16();
        if ((mask & 0x000080) != 0) r.ReadU16(); // zone
        if ((mask & 0x000100) != 0) { r.ReadU16(); r.ReadU16(); } // x/y
        if ((mask & 0x000200) != 0) SkipPartyAuraList(r, r.ReadU32(), 32);
        if ((mask & 0x000400) != 0) SkipPartyAuraList(r, r.ReadU16(), 16);
        if ((mask & 0x000800) != 0) r.ReadU64();
        if ((mask & 0x001000) != 0) r.ReadCString();
        if ((mask & 0x002000) != 0) r.ReadU16();
        if ((mask & 0x004000) != 0) r.ReadU16();
        if ((mask & 0x008000) != 0) r.ReadU16();
        if ((mask & 0x010000) != 0) r.ReadU8();
        if ((mask & 0x020000) != 0) r.ReadU16();
        if ((mask & 0x040000) != 0) r.ReadU16();
        if ((mask & 0x080000) != 0) SkipPartyAuraList(r, r.ReadU32(), 32);
        if ((mask & 0x100000) != 0) SkipPartyAuraList(r, r.ReadU16(), 16);
        if (r.Remaining != 0)
            throw new InvalidDataException($"party member stats trailing bytes {r.Remaining}");
    }

    private static void SkipPartyAuraList(PacketReader reader, uint slots, int bits)
    {
        for (int i = 0; i < bits; i++)
            if ((slots & (1u << i)) != 0) reader.ReadU16();
    }

    private void ApplyPartyInvite(byte[] body)
    {
        string inviter = new PacketReader(body).ReadCString();
        if (_partyInviter is not null) DismissPartyInvite(PartyInviteDismissal.EscapeOrTimeout);
        _partyInviter = inviter;
        _partyInviteDeadline = Stopwatch.GetTimestamp() +
            (long)(PartyFrameUiLaw.InviteTimeoutSeconds * Stopwatch.Frequency);
        PlayUiSound("igPlayerInvite");
    }

    private void CancelPartyInviteFromServer()
    {
        _partyInviter = null;
        _partyInviteDeadline = 0;
    }

    private void DismissPartyInvite(PartyInviteDismissal dismissal)
    {
        if (_partyInviter is null) return;
        PartyInviteWireCount wires = PartyFrameUiLaw.InviteWires(dismissal);
        _partyInviter = null;
        _partyInviteDeadline = 0;
        for (int i = 0; i < wires.Accept; i++) _net?.GroupAccept();
        for (int i = 0; i < wires.Decline; i++) _net?.GroupDecline();
    }

    private bool TryDismissPartyInviteOnEscape()
    {
        if (_partyInviter is null) return false;
        DismissPartyInvite(PartyInviteDismissal.EscapeOrTimeout);
        return true;
    }

    private void DrawPartyFrames()
    {
        if (_net is null || _gameplayArt is null) return;
        bool proof = _uiParityArmed && _uiParityPanel == "party-frame";
        if (_partyMembers.Count == 0 && !proof) return;
        int count = Math.Min(PartyFrameUiLaw.MemberCount,
            Math.Max(_partyMembers.Count, proof ? 1 : 0));
        for (int i = 0; i < count; i++)
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
        Vector2 p = new Vector2(PartyFrameUiLaw.FirstX, PartyFrameUiLaw.MemberY(index)) * s;
        ImGui.SetNextWindowPos(p, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(PartyFrameUiLaw.FrameWidth,
            PartyFrameUiLaw.FrameHeight) * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoBringToFrontOnFocus;
        if (!ImGui.Begin($"##vanilla-party-member-{index + 1}", flags)) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        string root = $"PartyMemberFrame{index + 1}";

        ImGui.SetCursorScreenPos(p);
        ImGui.InvisibleButton($"##party-member-hit-{index + 1}",
            new Vector2(PartyFrameUiLaw.FrameWidth, PartyFrameUiLaw.FrameHeight) * s,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        bool hovered = ImGui.IsItemHovered();
        bool left = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        bool right = ImGui.IsItemClicked(ImGuiMouseButton.Right);

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
        }

        _partyStats.TryGetValue(member?.Guid ?? 0, out PartyMemberStats? stats);
        byte status = stats?.Status ?? member?.Status ?? PartyFrameUiLaw.Online;
        bool connected = PartyFrameUiLaw.Has(status, PartyFrameUiLaw.Online);
        bool dead = unit?.IsDead == true || PartyFrameUiLaw.Has(status, PartyFrameUiLaw.Dead);
        bool ghost = PartyFrameUiLaw.Has(status, PartyFrameUiLaw.Ghost);
        uint health = _partyProofStaged ? stats?.Health ?? unit?.Fields.Health ?? 0
            : unit?.Fields.Health ?? stats?.Health ?? 0;
        uint maxHealth = _partyProofStaged ? stats?.MaxHealth ?? unit?.Fields.MaxHealth ?? 0
            : unit?.Fields.MaxHealth ?? stats?.MaxHealth ?? 0;
        uint power = _partyProofStaged ? stats?.Power ?? unit?.Fields.ActivePower ?? 0
            : unit?.Fields.ActivePower ?? stats?.Power ?? 0;
        uint maxPower = _partyProofStaged ? stats?.MaxPower ?? unit?.Fields.ActiveMaxPower ?? 0
            : unit?.Fields.ActiveMaxPower ?? stats?.MaxPower ?? 0;
        byte powerType = _partyProofStaged ? stats?.PowerType ?? unit?.Fields.PowerType ?? 0
            : unit?.Fields.PowerType ?? stats?.PowerType ?? 0;
        float healthFraction = maxHealth > 0 ? Math.Clamp((float)health / maxHealth, 0, 1) : 0;
        float powerFraction = maxPower > 0 ? Math.Clamp((float)power / maxPower, 0, 1) : 0;

        Vector4 portraitColor = !connected ? new(.5f, .5f, .5f, 1)
            : dead ? new(.35f, .35f, .35f, 1)
            : ghost ? new(.2f, .2f, .75f, 1)
            : health > 0 && healthFraction <= .2f
                ? new(1, 0, 0, PartyFrameUiLaw.LowHealthAlpha(MovementInfo.ClientUptimeMs() / 1000f))
                : Vector4.One;
        uint portraitTint = ImGui.ColorConvertFloat4ToU32(portraitColor);
        Vector2 portraitMin = p + new Vector2(7, 6) * s;
        if (unit is not null)
            DrawUnitPortraitImage(dl, unit, portraitMin, 37 * s, 0, false, portraitTint);

        // Bars belong one frame level below the nested art frame. Painting them before the art is
        // the equivalent ImGui draw order: the semi-transparent authored tubes clip their ends.
        DrawVanillaStatusBar(dl, p + new Vector2(47, 12) * s, new Vector2(70, 8) * s,
            connected ? healthFraction : 1f,
            connected ? new Vector4(0, 1, 0, 1) : new Vector4(.5f, .5f, .5f, 1));
        if (maxPower > 0)
            DrawVanillaStatusBar(dl, p + new Vector2(47, 21) * s, new Vector2(70, 8) * s,
                powerFraction, PowerColor(powerType));

        DrawArt(dl, @"Interface\TargetingFrame\UI-PartyFrame", p + new Vector2(0, 2) * s,
            new Vector2(128, 64), s);
        string name = member?.Name ?? _net?.PlayerName ?? "Player";
        DrawUnitFrameText(dl, p + new Vector2(83, 8) * s, name, 10 * s, UiGoldU32());
        if (member?.Guid == _partyLeaderGuid)
            DrawArt(dl, @"Interface\GroupFrame\UI-Group-LeaderIcon", p, new Vector2(16), s);
        if (_partyLootMethod == 2 && member?.Guid == _partyMasterLooterGuid)
            DrawArt(dl, @"Interface\GroupFrame\UI-Group-MasterLooter",
                p + new Vector2(32, 0) * s, new Vector2(16), s);
        if (!connected)
            DrawArt(dl, @"Interface\CharacterFrame\Disconnect-Icon",
                p + new Vector2(-7, -5) * s, new Vector2(64), s);

        bool ffa = PartyFrameUiLaw.Has(status, PartyFrameUiLaw.PvpFfa);
        bool pvp = PartyFrameUiLaw.Has(status, PartyFrameUiLaw.Pvp);
        if (ffa)
            DrawArt(dl, @"Interface\TargetingFrame\UI-PVP-FFA",
                p + new Vector2(-9, 15) * s, new Vector2(32), s);
        else if (pvp)
        {
            byte race = unit?.Fields.Bytes0.Race ??
                (_entities.TryGet(_net!.PlayerGuid, out WorldEntity own) ? own.Fields.Bytes0.Race : (byte)1);
            string faction = race is 2 or 5 or 6 or 8 ? "Horde" : "Alliance";
            DrawArt(dl, $@"Interface\GroupFrame\UI-Group-PVP-{faction}",
                p + new Vector2(-9, 15) * s, new Vector2(32), s);
        }

        if (left && member is not null) CommitSelection(member.Guid, beginAttack: false);
        if (right && member is not null)
        {
            CommitSelection(member.Guid, beginAttack: false);
            _unitPopupGuid = member.Guid;
            _unitPopupPosition = p + new Vector2(47, 15) * s;
        }
        if (hovered)
        {
            ImGui.BeginTooltip();
            ImGui.TextColored(new Vector4(.376f, .376f, 1f, 1f), name);
            if (!connected) ImGui.TextDisabled("Offline");
            else if (dead) ImGui.TextDisabled(ghost ? "Ghost" : "Dead");
            ImGui.EndTooltip();
        }

        if (_uiParityArmed && _uiParityPanel == "party-frame" && index == 0)
            MarkUiParityFrameComplete();
        ImGui.End();
    }

    private void DrawPartyInvite()
    {
        if (_partyInviter is null || _skin is null) return;
        if (Stopwatch.GetTimestamp() >= _partyInviteDeadline)
        {
            DismissPartyInvite(PartyInviteDismissal.EscapeOrTimeout);
            return;
        }

        float s = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector2 size = new Vector2(360, 96) * s;
        Vector2 origin = new((display.X - size.X) * .5f, 128 * s);
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.SetNextWindowFocus();
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav;
        if (!ImGui.Begin("##party-invite", flags)) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        bool proof = _uiParityArmed && _uiParityPanel == "party-invite";
        if (proof)
        {
            BeginUiParityFrame(origin, s);
            CollectUiParity("StaticPopup1", "Frame", origin, size, parent: "UIParent",
                point: "TOP", offsetX: "0", offsetY: "-128", strata: "DIALOG");
        }
        dl.PushClipRectFullScreen();
        _skin.DrawBackdrop(dl, origin, origin + size, WowSkin.Dialog);
        _skin.GlueImage(dl, "dialog.alert", origin + new Vector2(12, 8) * s,
            origin + new Vector2(76, 72) * s);
        dl.PopClipRect();
        GameText.DrawCentered(dl, "GameFontNormal", $"{_partyInviter} invites you to a group.",
            origin + new Vector2(212, 34) * s, s);
        bool accept = DrawEnchantPopupButton(dl, "Accept", origin + new Vector2(62, 68) * s, s);
        bool decline = DrawEnchantPopupButton(dl, "Decline", origin + new Vector2(198, 68) * s, s);
        if (proof) MarkUiParityFrameComplete();
        ImGui.End();
        if (accept) DismissPartyInvite(PartyInviteDismissal.Accept);
        else if (decline) DismissPartyInvite(PartyInviteDismissal.DeclineButton);
    }
}
