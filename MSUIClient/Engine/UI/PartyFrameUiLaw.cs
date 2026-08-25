using System.Numerics;
using MSUIClient.Net;

namespace MSUIClient.Engine.UI;

public enum PartyPointerButton
{
    Left,
    Right,
}

public enum PartyPointerAction
{
    None,
    Target,
    OpenPartyMenu,
}

public enum PartyPortraitSourceKind
{
    Empty,
    TemporaryPlayerCircular,
}

public readonly record struct PartyMemberStatsSnapshot(
    byte? Status = null,
    ushort? Health = null,
    ushort? MaxHealth = null,
    byte? PowerType = null,
    ushort? Power = null,
    ushort? MaxPower = null,
    ushort? Level = null,
    ushort? Zone = null,
    short? PositionX = null,
    short? PositionY = null,
    ushort[]? Auras = null,
    ushort[]? NegativeAuras = null,
    ulong? PetGuid = null,
    string? PetName = null,
    ushort? PetModelId = null,
    ushort? PetHealth = null,
    ushort? PetMaxHealth = null,
    byte? PetPowerType = null,
    ushort? PetPower = null,
    ushort? PetMaxPower = null,
    ushort[]? PetAuras = null,
    ushort[]? PetNegativeAuras = null);

public readonly record struct PartyTooltipView(
    string Name,
    string? LevelLine,
    string? PvpLine,
    uint Health,
    uint MaxHealth);

public readonly record struct PartyTooltipLayout(
    float Width,
    float Height,
    float[] RowTops);

public readonly record struct PartyTooltipHealthState(
    bool Visible,
    uint Maximum,
    uint Value);

public readonly record struct PartyRosterWireMember(
    string Name, ulong Guid, byte Status, byte MemberFlags);

public readonly record struct PartyRosterWire(
    byte GroupType,
    byte OwnFlags,
    PartyRosterWireMember[] Members,
    ulong LeaderGuid,
    byte LootMethod,
    ulong MasterLooterGuid,
    byte LootThreshold = 0,
    byte DungeonDifficulty = 0);

public readonly record struct PartyMemberStatsWire(
    ulong Guid, PartyMemberStatsSnapshot Snapshot);

public readonly record struct PartyCommandResultWire(
    uint Operation, string Member, uint Result);

public readonly record struct PartyMinimapPingWire(
    ulong Guid, float X, float Y);

public readonly record struct PartyRaidTargetEntry(byte Icon, ulong Guid);

public readonly record struct PartyRaidTargetUpdateWire(
    bool IsDelta,
    byte Icon,
    ulong Guid,
    PartyRaidTargetEntry[] Entries);

public readonly record struct PartyReadyCheckWire(
    bool Started,
    ulong Guid,
    byte Ready);

public static class PartyFramePacketLaw
{
    public static PartyRosterWire ParseRoster(byte[] body)
    {
        var reader = new PacketReader(body);
        byte groupType = reader.ReadU8();
        byte ownFlags = reader.ReadU8();
        uint count = reader.ReadU32();
        if (count > 39) throw new InvalidDataException($"SMSG_GROUP_LIST member count {count}");
        var members = new PartyRosterWireMember[checked((int)count)];
        for (int i = 0; i < members.Length; i++)
            members[i] = new(ReadCStringTerminated(reader, body, "member name"),
                reader.ReadU64(), reader.ReadU8(),
                reader.ReadU8());
        ulong leader = reader.ReadU64();
        byte lootMethod = 0;
        ulong master = 0;
        byte threshold = 0;
        byte dungeonDifficulty = 0;
        if (count > 0)
        {
            lootMethod = reader.ReadU8();
            master = reader.ReadU64();
            threshold = reader.ReadU8();
            dungeonDifficulty = reader.ReadU8();
        }
        // vmangos does not send the 14-byte header-shaped empty roster when you stop being in a
        // group. Group::RemoveMember (src/game/Group/Group.cpp:506) and Group::Disband (:602) both
        // send a fixed 24-byte all-zero body — data.Initialize(SMSG_GROUP_LIST, 24) followed by
        // three uint64(0). The header shape consumes 14 of those; the remaining 10 are padding,
        // not fields. Rejecting them discarded the ONLY packet that ever clears the roster: a
        // two-man leave takes Disband(hideDestroy=true), so no SMSG_GROUP_DESTROYED arrives either.
        if (count == 0 && leader == 0 && reader.Remaining == 10)
        {
            for (int i = 0; i < 10; i++)
                if (reader.ReadU8() != 0)
                    throw new InvalidDataException(
                        "SMSG_GROUP_LIST leave padding must be all zero");
        }
        if (reader.Remaining != 0)
            throw new InvalidDataException($"SMSG_GROUP_LIST trailing bytes {reader.Remaining}");
        return new(groupType, ownFlags, members, leader, lootMethod,
            lootMethod == 2 ? master : 0, threshold, dungeonDifficulty);
    }

    public static PartyMemberStatsWire ParseMemberStats(byte[] body)
    {
        var reader = new PacketReader(body);
        ulong guid = reader.ReadPackedGuid();
        uint mask = reader.ReadU32();
        byte? status = null;
        ushort? health = null, maxHealth = null, power = null, maxPower = null, level = null;
        ushort? zone = null;
        short? positionX = null, positionY = null;
        ushort[]? auras = null, negativeAuras = null;
        ulong? petGuid = null;
        string? petName = null;
        ushort? petModelId = null, petHealth = null, petMaxHealth = null;
        ushort? petPower = null, petMaxPower = null;
        byte? petPowerType = null;
        ushort[]? petAuras = null, petNegativeAuras = null;
        byte? powerType = null;
        if ((mask & 0x000001) != 0) status = reader.ReadU8();
        if ((mask & 0x000002) != 0) health = reader.ReadU16();
        if ((mask & 0x000004) != 0) maxHealth = reader.ReadU16();
        if ((mask & 0x000008) != 0) powerType = reader.ReadU8();
        if ((mask & 0x000010) != 0) power = reader.ReadU16();
        if ((mask & 0x000020) != 0) maxPower = reader.ReadU16();
        if ((mask & 0x000040) != 0) level = reader.ReadU16();
        if ((mask & 0x000080) != 0) zone = reader.ReadU16();
        if ((mask & 0x000100) != 0)
        {
            positionX = unchecked((short)reader.ReadU16());
            positionY = unchecked((short)reader.ReadU16());
        }
        if ((mask & 0x000200) != 0) auras = ReadAuraList(reader, reader.ReadU32(), 32);
        if ((mask & 0x000400) != 0)
            negativeAuras = ReadAuraList(reader, reader.ReadU16(), 16);
        if ((mask & 0x000800) != 0) petGuid = reader.ReadU64();
        if ((mask & 0x001000) != 0)
            petName = ReadCStringTerminated(reader, body, "pet name");
        if ((mask & 0x002000) != 0) petModelId = reader.ReadU16();
        if ((mask & 0x004000) != 0) petHealth = reader.ReadU16();
        if ((mask & 0x008000) != 0) petMaxHealth = reader.ReadU16();
        if ((mask & 0x010000) != 0) petPowerType = reader.ReadU8();
        if ((mask & 0x020000) != 0) petPower = reader.ReadU16();
        if ((mask & 0x040000) != 0) petMaxPower = reader.ReadU16();
        if ((mask & 0x080000) != 0) petAuras = ReadAuraList(reader, reader.ReadU32(), 32);
        if ((mask & 0x100000) != 0)
            petNegativeAuras = ReadAuraList(reader, reader.ReadU16(), 16);
        if (reader.Remaining != 0)
            throw new InvalidDataException($"party member stats trailing bytes {reader.Remaining}");
        return new(guid, new(status, health, maxHealth, powerType, power, maxPower, level,
            zone, positionX, positionY, auras, negativeAuras, petGuid, petName, petModelId,
            petHealth, petMaxHealth, petPowerType, petPower, petMaxPower, petAuras,
            petNegativeAuras));
    }

    public static string ParseInvite(byte[] body)
    {
        var reader = new PacketReader(body);
        string inviter = ReadCStringTerminated(reader, body, "inviter");
        if (reader.Remaining != 0)
            throw new InvalidDataException($"SMSG_GROUP_INVITE trailing bytes {reader.Remaining}");
        return inviter;
    }

    public static string ParseDecline(byte[] body) => ParseNameNotice(body, "SMSG_GROUP_DECLINE");

    public static string ParseLeaderChanged(byte[] body) =>
        ParseNameNotice(body, "SMSG_GROUP_SET_LEADER");

    public static PartyCommandResultWire ParseCommandResult(byte[] body)
    {
        var reader = new PacketReader(body);
        uint operation = reader.ReadU32();
        string member = ReadCStringTerminated(reader, body, "party command member");
        uint result = reader.ReadU32();
        RequireConsumed(reader, "SMSG_PARTY_COMMAND_RESULT");
        return new(operation, member, result);
    }

    public static void ParseEmptyNotice(byte[] body, string opcodeName)
    {
        if (body.Length != 0)
            throw new InvalidDataException($"{opcodeName} trailing bytes {body.Length}");
    }

    public static PartyMinimapPingWire ParseMinimapPing(byte[] body)
    {
        var reader = new PacketReader(body);
        var wire = new PartyMinimapPingWire(reader.ReadU64(), reader.ReadF32(), reader.ReadF32());
        RequireConsumed(reader, "MSG_MINIMAP_PING");
        return wire;
    }

    public static PartyRaidTargetUpdateWire ParseRaidTargetUpdate(byte[] body)
    {
        var reader = new PacketReader(body);
        byte mode = reader.ReadU8();
        if (mode == 0)
        {
            var wire = new PartyRaidTargetUpdateWire(true, reader.ReadU8(), reader.ReadU64(), []);
            RequireConsumed(reader, "MSG_RAID_TARGET_UPDATE delta");
            return wire;
        }

        var entries = new List<PartyRaidTargetEntry>();
        while (reader.Remaining > 0)
            entries.Add(new(reader.ReadU8(), reader.ReadU64()));
        return new(false, 0, 0, entries.ToArray());
    }

    public static PartyReadyCheckWire ParseReadyCheck(byte[] body)
    {
        if (body.Length == 0) return new(true, 0, 0);
        var reader = new PacketReader(body);
        var wire = new PartyReadyCheckWire(false, reader.ReadU64(), reader.ReadU8());
        RequireConsumed(reader, "MSG_RAID_READY_CHECK");
        return wire;
    }

    private static string ParseNameNotice(byte[] body, string opcodeName)
    {
        var reader = new PacketReader(body);
        string name = ReadCStringTerminated(reader, body, opcodeName + " name");
        RequireConsumed(reader, opcodeName);
        return name;
    }

    private static string ReadCStringTerminated(PacketReader reader, byte[] body, string field)
    {
        int before = reader.Position;
        string value = reader.ReadCString();
        if (reader.Position <= before || body[reader.Position - 1] != 0)
            throw new EndOfStreamException($"unterminated party {field} CString");
        return value;
    }

    private static ushort[] ReadAuraList(PacketReader reader, uint slots, int bits)
    {
        var spells = new List<ushort>();
        for (int i = 0; i < bits; i++)
            if ((slots & (1u << i)) != 0) spells.Add(reader.ReadU16());
        return spells.ToArray();
    }

    private static void RequireConsumed(PacketReader reader, string packet)
    {
        if (reader.Remaining != 0)
            throw new InvalidDataException($"{packet} trailing bytes {reader.Remaining}");
    }
}

public static class PartyFrameUiLaw
{
    public const string PartyInvitePopupType = "PARTY_INVITE";

    /// <summary>
    /// SoundEntries kit 881 -> Sound\Interface\iPlayerInviteA.wav, the roster-change drum.
    /// The reference client plays it from the engine rather than FrameXML — the name appears
    /// nowhere in the 1.12 Lua — so the roster diff is the only hook there is to mirror.
    /// </summary>
    public const string MemberJoinedSound = "igPlayerInviteAccept";
    public static readonly StaticPopupCoordinatorLaw.Definition PartyInvitePopupDefinition = new(
        PartyInvitePopupType,
        WhileDead: true,
        HideOnEscape: true,
        HasAccept: true,
        HasCancel: true,
        HasOnShow: true,
        HasOnHide: true,
        TimeoutSeconds: 60,
        EntrySound: "igPlayerInvite");

    public const int MemberCount = 4;
    public const float FrameWidth = 128f;
    public const float FrameHeight = 53f;
    public const float FirstX = 10f;
    public const float FirstY = 128f;
    public const float PetlessStride = 63f;
    public const float TooltipFadeSeconds = .5f;
    public const string TooltipOwnerSurface = "party-member";
    public const int TooltipUnitReaction = 5;
    public const float TooltipPadding = 10f;
    public const float TooltipRowGap = 2f;
    public const float TooltipRightBaseOffset = -13f;
    public const float TooltipBottomBaseOffset = 70f;
    public const float TooltipRightLeftBarStep = 90f;
    public const float TooltipRightRightBarStep = 45f;
    public const float TooltipBottomBarStep = 27f;
    public const float TooltipPetBarStep = 23f;
    public const float TooltipReputationStep = 9f;
    public const float PopupWidth = 320f;
    public const float PopupTextWidth = 290f;
    public const float PopupTextTop = 16f;
    public const float PopupButtonWidth = 128f;
    public const float PopupButtonHeight = 20f;
    public const float PopupButtonOneX = 26f;
    public const float PopupButtonTwoX = 167f;

    public const byte Online = 0x01;
    public const byte Pvp = 0x02;
    public const byte Dead = 0x04;
    public const byte Ghost = 0x08;
    public const byte PvpFfa = 0x10;
    public const byte Afk = 0x40;
    public const uint UnitFlagPvp = 0x0000_1000;

    public static float MemberY(int zeroBasedIndex)
    {
        if (zeroBasedIndex is < 0 or >= MemberCount)
            throw new ArgumentOutOfRangeException(nameof(zeroBasedIndex));
        return FirstY + zeroBasedIndex * PetlessStride;
    }

    public static bool Has(byte status, byte bit) => (status & bit) != 0;

    public static byte Subgroup(byte flags) => (byte)(flags & 0x7f);

    public static int[] CompactRosterIndices(byte ownFlags, IReadOnlyList<byte> memberFlags)
    {
        byte ownSubgroup = Subgroup(ownFlags);
        var result = new List<int>(MemberCount);
        for (int i = 0; i < memberFlags.Count && result.Count < MemberCount; i++)
            if (Subgroup(memberFlags[i]) == ownSubgroup) result.Add(i);
        return result.ToArray();
    }

    public static PartyMemberStatsSnapshot MergeStats(
        PartyMemberStatsSnapshot previous,
        PartyMemberStatsSnapshot incoming,
        bool fullSnapshot) => fullSnapshot ? incoming : new(
            incoming.Status ?? previous.Status,
            incoming.Health ?? previous.Health,
            incoming.MaxHealth ?? previous.MaxHealth,
            incoming.PowerType ?? previous.PowerType,
            incoming.Power ?? previous.Power,
            incoming.MaxPower ?? previous.MaxPower,
            incoming.Level ?? previous.Level,
            incoming.Zone ?? previous.Zone,
            incoming.PositionX ?? previous.PositionX,
            incoming.PositionY ?? previous.PositionY,
            incoming.Auras ?? previous.Auras,
            incoming.NegativeAuras ?? previous.NegativeAuras,
            incoming.PetGuid ?? previous.PetGuid,
            incoming.PetName ?? previous.PetName,
            incoming.PetModelId ?? previous.PetModelId,
            incoming.PetHealth ?? previous.PetHealth,
            incoming.PetMaxHealth ?? previous.PetMaxHealth,
            incoming.PetPowerType ?? previous.PetPowerType,
            incoming.PetPower ?? previous.PetPower,
            incoming.PetMaxPower ?? previous.PetMaxPower,
            incoming.PetAuras ?? previous.PetAuras,
            incoming.PetNegativeAuras ?? previous.PetNegativeAuras);

    // GROUP_LIST owns connection/dead/ghost/PvP status. A delayed stats reply must not make a
    // roster-offline member look online again.
    public static byte EffectiveStatus(byte rosterStatus, byte? statsStatus) => rosterStatus;

    public static bool MergedPvp(byte rosterStatus, uint? streamedUnitFlags) =>
        Has(rosterStatus, Pvp) ||
        streamedUnitFlags is uint flags && (flags & UnitFlagPvp) != 0;

    public static string? PvpFaction(byte? memberRace, byte? ownRace)
    {
        byte race = memberRace is >= 1 and <= 8
            ? memberRace.Value
            : ownRace is >= 1 and <= 8 ? ownRace.Value : (byte)0;
        return race switch
        {
            1 or 3 or 4 or 7 => "Alliance",
            2 or 5 or 6 or 8 => "Horde",
            _ => null,
        };
    }

    // Frozen GroupState::apply_list treats leader==0 as the authoritative leave/reset shape.
    // Other fields are parsed and validated but do not narrow that discriminator.
    public static bool IsLeaveRoster(PartyRosterWire wire) => wire.LeaderGuid == 0;

    // Frozen UIParent_ManageFramePositions owns GameTooltip's default anchor through the
    // CONTAINER_OFFSET vars. The left vertical bar wins when both right-side bars are visible.
    public static float TooltipRightOffset(bool multiBarLeftVisible, bool multiBarRightVisible) =>
        TooltipRightBaseOffset - (multiBarLeftVisible
            ? TooltipRightLeftBarStep
            : multiBarRightVisible ? TooltipRightRightBarStep : 0f);

    public static float TooltipBottomOffset(bool bottomLeftVisible, bool bottomRightVisible,
        bool petOrStanceVisible, bool reputationVisible) =>
        TooltipBottomBaseOffset +
        (bottomLeftVisible || bottomRightVisible ? TooltipBottomBarStep : 0f) +
        (petOrStanceVisible ? TooltipPetBarStep : 0f) +
        (reputationVisible ? TooltipReputationStep : 0f);

    // LOGIN_VERIFY_WORLD / NEW_WORLD are map boundaries, not group-session boundaries; only
    // disconnect/session teardown may clear the session-owned party state.
    public static bool PreservePartyAcrossWorldEnter(bool socketSessionAlive) => socketSessionAlive;

    // The frozen OnUpdate pauses on a missing/disconnected token. A connected member outside the
    // living <=20% band resets to zero; an eligible member advances its own modulo-one timer.
    public static float AdvanceLowHealthTimer(float previousSlotSeconds, bool exists, bool connected,
        bool lowLivingHealth, float dt)
    {
        float previous = Math.Clamp(previousSlotSeconds, 0f, .999999f);
        if (!exists || !connected) return previous;
        if (!lowLivingHealth) return 0f;
        float next = previous + MathF.Max(0f, dt);
        return next - MathF.Floor(next);
    }

    // PartyMemberFrame.lua uses one frame-slot-local one-second triangle with 0.5-second legs
    // between 127/255 and 1.0. Slot state is independent from the member GUID currently bound.
    public static float LowHealthAlpha(float frameSlotTimerSeconds)
    {
        float t = frameSlotTimerSeconds - MathF.Floor(frameSlotTimerSeconds);
        const float low = 127f / 255f;
        return t < .5f
            ? 1f - t * (1f - low) * 2f
            : low + (t - .5f) * (1f - low) * 2f;
    }

    public static PartyPointerAction ReleaseAction(int armedIndex, int releasedIndex,
        PartyPointerButton button) =>
        armedIndex < 0 || armedIndex != releasedIndex
        ? PartyPointerAction.None
        : button == PartyPointerButton.Left
            ? PartyPointerAction.Target
            : PartyPointerAction.OpenPartyMenu;

    // Button.lua sets PUSHED only while an armed pointer remains over the button (or an
    // explicit pushed state is set). Dragging outside restores normal art before ButtonUp.
    public static bool InviteButtonPushed(bool held, bool hovered, bool pushedState = false) =>
        held && hovered || pushedState;

    public static PartyPortraitSourceKind PortraitSource(bool streamed) => streamed
        ? PartyPortraitSourceKind.TemporaryPlayerCircular
        : PartyPortraitSourceKind.Empty;

    public static string? PlayerLevelLine(uint level, string? race, string? @class,
        bool dead = false)
    {
        var parts = new List<string>();
        if (dead) parts.Add("Corpse");
        else
        {
            if (!string.IsNullOrWhiteSpace(race)) parts.Add(race);
            if (!string.IsNullOrWhiteSpace(@class)) parts.Add(@class);
        }
        string levelText = $"Level {level}";
        return parts.Count == 0
            ? $"{levelText} (Player)"
            : $"{levelText} {string.Join(' ', parts)} (Player)";
    }

    public static PartyTooltipView Tooltip(string name, uint level, string? race, string? @class,
        bool dead, bool pvp, uint health, uint maxHealth) => new(
            name,
            PlayerLevelLine(level, race, @class, dead),
            pvp ? "PvP" : null,
        health,
        maxHealth);

    // The GameTooltip owner is the fixed PartyMemberFrameN. Its SetUnit channel is the separate
    // fixed partyN token, whose roster occupant may change without firing the frame's OnEnter.
    public static GameTooltipOwnerKey TooltipOwner(int zeroBasedSlot)
    {
        ValidateTooltipSlot(zeroBasedSlot);
        return new(TooltipOwnerSurface, (ulong)(zeroBasedSlot + 1));
    }

    public static string TooltipUnitToken(int zeroBasedSlot)
    {
        ValidateTooltipSlot(zeroBasedSlot);
        return $"party{zeroBasedSlot + 1}";
    }

    public static GameTooltipContent SharedTooltipContent(int zeroBasedSlot,
        in PartyTooltipView view, bool tokenExists = true)
    {
        if (string.IsNullOrEmpty(view.LevelLine))
            throw new ArgumentException("party unit tooltip requires its level line", nameof(view));
        var lines = new List<GameTooltipLine>(3)
        {
            new(view.Name, GameTooltipTextTone.UnitReaction),
            new(view.LevelLine, GameTooltipTextTone.White),
        };
        if (view.PvpLine is not null)
            lines.Add(new(view.PvpLine, GameTooltipTextTone.White));
        PartyTooltipHealthState health = TooltipHealth(tokenExists, view.Health, view.MaxHealth);
        return new(GameTooltipAnchorKind.DefaultBottomRight, lines.ToArray(),
            TooltipUnitToken(zeroBasedSlot),
            new GameTooltipHealthState(health.Visible, health.Maximum, health.Value),
            TooltipUnitReaction);
    }

    // Only Token/Exists/Health/MaxHealth participate in the shared health watcher. The remaining
    // fields are deliberately inert so a push can never rebuild the retained SetUnit rows.
    public static GameTooltipUnitSnapshot TooltipHealthPush(int zeroBasedSlot, bool tokenExists,
        uint health, uint maxHealth) => new(
            TooltipUnitToken(zeroBasedSlot), tokenExists, "", null, 0, 0,
            TooltipUnitReaction, true, null, null, null, 0, false, null,
            false, false, false, false, health, maxHealth);

    private static void ValidateTooltipSlot(int zeroBasedSlot)
    {
        if ((uint)zeroBasedSlot >= MemberCount)
            throw new ArgumentOutOfRangeException(nameof(zeroBasedSlot));
    }

    public static PartyTooltipLayout TooltipLayout(IReadOnlyList<float> rowWidths,
        IReadOnlyList<float> rowHeights, float scale = 1f)
    {
        if (rowWidths.Count == 0 || rowWidths.Count != rowHeights.Count)
            throw new ArgumentException("party tooltip rows must be nonempty and paired");
        if (!(scale > 0f)) throw new ArgumentOutOfRangeException(nameof(scale));
        var tops = new float[rowHeights.Count];
        float cursor = TooltipPadding * scale;
        for (int i = 0; i < rowHeights.Count; i++)
        {
            tops[i] = cursor;
            cursor += rowHeights[i];
            if (i + 1 < rowHeights.Count) cursor += TooltipRowGap * scale;
        }
        return new(rowWidths.Max() + TooltipPadding * 2f * scale,
            cursor + TooltipPadding * scale, tops);
    }

    public static PartyTooltipHealthState TooltipHealth(bool tokenExists, uint health,
        uint maxHealth)
    {
        if (!tokenExists) return new(false, 0, 0);
        uint maximum = Math.Max(1u, maxHealth);
        return new(true, maximum, Math.Min(health, maximum));
    }

    public static PartyTooltipHealthState MemberHealth(bool connected, uint health,
        uint maxHealth) => connected
        ? new(true, maxHealth, Math.Min(health, maxHealth))
        : new(true, 1, 1);

    // GameTooltip:SetUnit is called by the fixed party1..party4 token's OnEnter, not on every
    // draw. Continuous hover keeps its text/reaction snapshot; a new/re-entered token rebuilds.
    public static bool BeginTooltipSnapshot(int retainedSlot, int hoveredSlot,
        bool hasSnapshot, bool fading) => hoveredSlot >= 0 &&
        (!hasSnapshot || fading || retainedSlot != hoveredSlot);

    // member_unit_state fixes every Party token at reaction 5; GameTooltip's frozen
    // FACTION_BAR_COLORS[5] is the darker friendly green (0, .6, .1).
    public static Vector4 TooltipNameColor => new(0f, .6f, .1f, 1f);

    public static float TooltipFadeAlpha(double elapsedSeconds) =>
        Math.Clamp(1f - (float)(Math.Max(0d, elapsedSeconds) / TooltipFadeSeconds), 0f, 1f);

    public static float PopupHeight(float measuredTextHeight) =>
        PopupTextTop + MathF.Max(0f, measuredTextHeight) + 8f + PopupButtonHeight + 16f;

    public static float PopupButtonTop(float measuredTextHeight) =>
        PopupTextTop + MathF.Max(0f, measuredTextHeight) + 8f;

    public static (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? PartyInvitePopup(
        StaticPopupCoordinatorLaw.Slots slots)
    {
        if (slots.First is { } first &&
            string.Equals(first.Definition.Type, PartyInvitePopupType, StringComparison.Ordinal))
            return (1, first);
        if (slots.Second is { } second &&
            string.Equals(second.Definition.Type, PartyInvitePopupType, StringComparison.Ordinal))
            return (2, second);
        return null;
    }

    public static bool IsPartyInviteVisible(StaticPopupCoordinatorLaw.Slots slots) =>
        PartyInvitePopup(slots) is not null;

}
