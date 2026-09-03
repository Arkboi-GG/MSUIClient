using MSUIClient;
using MSUIClient.Net;

/// <summary>
/// Party member facts (owner rule: party = full facts, faction = orders).
/// Verifies the wire law (pull builder + member-spells exact-length parser),
/// the opcode/capability constants including the reserved rotation pair, and
/// the snapshot-gate law: party/raid members accepted without possession,
/// everything else still dropped honestly.
/// </summary>
internal static class PartyMemberFactsClinicalChecks
{
    public static void Run()
    {
        // ── Pull builder: u8 flags, u8 count, u64 guids; empty = whole party ──
        Check(MemberFactsWire.BuildMemberFactsBody([]) is [0, 0],
            "empty member-facts pull must be exactly flags=0, count=0");
        byte[] two = MemberFactsWire.BuildMemberFactsBody([0x1122334455667788UL, 9UL]);
        Check(two.Length == 18 && two[0] == 0 && two[1] == 2 &&
              BitConverter.ToUInt64(two, 2) == 0x1122334455667788UL &&
              BitConverter.ToUInt64(two, 10) == 9UL,
            "member-facts pull body layout drift (flags/count/raw little-endian guids)");
        ExpectRefused(() => MemberFactsWire.BuildMemberFactsBody(
            [.. Enumerable.Repeat(1UL, MemberFactsWire.MaximumSubjects + 1)]));

        // ── Member-spells parser: u64 guid, u16 count, u32 ids; exact length ──
        var w = new PacketWriter();
        w.WriteU64(0xABCDEF01UL);
        w.WriteU16(3);
        w.WriteU32(133);
        w.WriteU32(5504);
        w.WriteU32(25286);
        byte[] body = w.ToArray();
        Check(MemberFactsWire.TryParseMemberSpells(body, out ulong guid, out uint[] spells) &&
              guid == 0xABCDEF01UL && spells is [133, 5504, 25286],
            "member-spells parse did not round-trip guid + spell ids");

        var empty = new PacketWriter();
        empty.WriteU64(7);
        empty.WriteU16(0);
        Check(MemberFactsWire.TryParseMemberSpells(empty.ToArray(), out ulong emptyGuid, out uint[] none) &&
              emptyGuid == 7 && none.Length == 0,
            "an empty (but exact) spell list is legal and must parse");

        Check(!MemberFactsWire.TryParseMemberSpells([], out _, out _) &&
              !MemberFactsWire.TryParseMemberSpells(body[..^1], out _, out _) &&
              !MemberFactsWire.TryParseMemberSpells([.. body, 0], out _, out _),
            "member-spells parser must refuse truncated/padded bodies (exact-length wire law)");

        // ── Item move (Phase C v1): builder + result parser, exact lengths ────
        byte[] move = MemberFactsWire.BuildMemberItemMoveBody(0x0102030405060708UL, 9UL, 255, 27);
        Check(move.Length == 19 && move[0] == 0 &&
              BitConverter.ToUInt64(move, 1) == 0x0102030405060708UL &&
              BitConverter.ToUInt64(move, 9) == 9UL && move[17] == 255 && move[18] == 27,
            "member-item-move body layout drift (flags/from/to/bag/slot)");
        var moveResult = new PacketWriter();
        moveResult.WriteU8(MemberFactsWire.ItemMoveTargetFull);
        moveResult.WriteU64(11);
        moveResult.WriteU64(22);
        byte[] resultBody = moveResult.ToArray();
        Check(MemberFactsWire.TryParseMemberItemMoveResult(resultBody,
                  out byte moveCode, out ulong moveFrom, out ulong moveTo) &&
              moveCode == MemberFactsWire.ItemMoveTargetFull && moveFrom == 11 && moveTo == 22 &&
              !MemberFactsWire.TryParseMemberItemMoveResult(resultBody[..^1], out _, out _, out _) &&
              !MemberFactsWire.TryParseMemberItemMoveResult([.. resultBody, 0], out _, out _, out _),
            "member-item-move result parser must round-trip and refuse sloppy lengths");

        // ── Opcodes + capability bits; the rotation pair stays reserved ───────
        Check((ushort)Op.CMSG_SUI_MEMBER_FACTS == 850 &&
              (ushort)Op.SMSG_SUI_MEMBER_SPELLS == 851 &&
              (ushort)Op.CMSG_SUI_MEMBER_ITEM_MOVE == 852 &&
              (ushort)Op.SMSG_SUI_MEMBER_ITEM_MOVE_RESULT == 853 &&
              !Enum.IsDefined((Op)0x0350) && !Enum.IsDefined((Op)0x0351) &&
              SuiCapabilityWire.PartyMemberFactsV1 == 1u << 3 &&
              SuiCapabilityWire.PartyItemMoveV1 == 1u << 4,
            "member-facts opcodes must sit at 850..853 leaving 848/849 for the " +
            "dynamic-combat rotation pair, capability bits 3/4");

        // ── Snapshot-gate law: party members accepted, non-party still dropped ─
        string root = ClientConfig.FindRepoRoot();
        string control = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Control.cs"));
        Check(control.Contains("bool forControlled = source == ControlledGuid;",
                  StringComparison.Ordinal) &&
              control.Contains("bool forSessionPlayer = source == LocalPlayerGuid;",
                  StringComparison.Ordinal) &&
              control.Contains("(!forControlled && !forSessionPlayer && !IsPartyMemberFactsSubject(source)) ||",
                  StringComparison.Ordinal) &&
              control.Contains("snapshot DROPPED", StringComparison.Ordinal) &&
              control.Contains("if (forControlled) ApplyControlledCharacter();",
                  StringComparison.Ordinal),
            "snapshot gate law drift: the session player and party/raid members must be " +
            "accepted without possession, non-party sources still dropped with the honest log, and " +
            "the possessed-body rebuild must stay controlled-only");
        Check(control.Contains("existing.Type is ObjectTypeId.Item or ObjectTypeId.Container",
                  StringComparison.Ordinal) &&
              control.Contains("ReferenceEquals(item, existing)", StringComparison.Ordinal) &&
              control.Contains("_entities.RemoveSynthetic(item);", StringComparison.Ordinal),
            "self inventory snapshots must reuse live items and clean up only their own synthetic instances");

        var entityStore = new EntityStore();
        var staleSynthetic = new WorldEntity { Guid = 42, Type = ObjectTypeId.Item };
        var authoritativeReplacement = new WorldEntity { Guid = 42, Type = ObjectTypeId.Item };
        entityStore.AddSynthetic(staleSynthetic);
        entityStore.AddSynthetic(authoritativeReplacement);
        Check(!entityStore.RemoveSynthetic(staleSynthetic) &&
              entityStore.TryGet(42, out WorldEntity retained) &&
              ReferenceEquals(retained, authoritativeReplacement),
            "snapshot cleanup removed a later authoritative replacement with the same GUID");

        string memberFacts = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.MemberFacts.cs"));
        Check(memberFacts.Contains("MemberFactsWire.TryParseMemberSpells", StringComparison.Ordinal) &&
              memberFacts.Contains("!IsPartyMemberFactsSubject(guid)", StringComparison.Ordinal) &&
              memberFacts.Contains("PopulateBotBar(guid);", StringComparison.Ordinal) &&
              memberFacts.Contains("SuiCapabilityWire.PartyMemberFactsV1", StringComparison.Ordinal) &&
              memberFacts.Contains("if (!_partyMemberFactsAvailable || _net is not { IsInWorld: true }) return false;",
                  StringComparison.Ordinal) &&
              memberFacts.Contains("PartyMemberFactsPullMinIntervalSeconds", StringComparison.Ordinal),
            "member-spells handler / capability-gated rate-limited pull drift");

        string dispatch = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        Check(dispatch.Contains("case Op.SMSG_SUI_MEMBER_SPELLS:", StringComparison.Ordinal) &&
              dispatch.Contains("ApplySuiMemberSpells(body);", StringComparison.Ordinal),
            "SMSG_SUI_MEMBER_SPELLS lost its dispatch case");

        string portals = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.RealPortals.cs"));
        Check(portals.Contains("ApplyPartyMemberFactsCapability(capabilities);",
                  StringComparison.Ordinal),
            "control-ACK trailer parse no longer feeds the member-facts capability");

        // ── Item-move gate law: capability-gated send, no client optimism ─────
        Check(memberFacts.Contains("if (!_partyItemMoveAvailable)", StringComparison.Ordinal) &&
              memberFacts.Contains("_net?.SuiMemberItemMove(from, to, bag, slot);",
                  StringComparison.Ordinal) &&
              dispatch.Contains("case Op.SMSG_SUI_MEMBER_ITEM_MOVE_RESULT:",
                  StringComparison.Ordinal) &&
              dispatch.Contains("ApplySuiMemberItemMoveResult(body);", StringComparison.Ordinal),
            "party item move lost its capability gate or its result dispatch");
        string partyInventory = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Panels", "GameLoop.PartyInventory.cs"));
        Check(partyInventory.Contains("RequestMemberItemMove(_partyGiveFrom, guid,",
                  StringComparison.Ordinal) &&
              partyInventory.Contains("RequestMemberItemMove(_partyDragFrom, ownerGuid,",
                  StringComparison.Ordinal) &&
              partyInventory.Contains("byte wireBag = container == 0 ? (byte)255 : " +
                  "(byte)(19 + container - 1);", StringComparison.Ordinal) &&
              partyInventory.Contains("byte wireSlot = container == 0 ? (byte)(23 + slot) : " +
                  "(byte)slot;", StringComparison.Ordinal),
            "Party Inventory give/drag wire mapping drift (255/23+ backpack, 19+ bags)");

        string inventory = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Panels", "GameLoop.Inventory.cs"));
        string bindings = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Panels", "GameLoop.Bindings.cs"));
        Check(inventory.Contains("BindingDown(GameBinding.OpenPartyInventory)",
                  StringComparison.Ordinal) &&
              bindings.Contains("GameBinding.OpenPartyInventory, \"Party Inventory\", Key.Unknown",
                  StringComparison.Ordinal) &&
              bindings.Contains("new BindingChord(Key.I, Shift: true)", StringComparison.Ordinal),
            "Party Inventory must have a configurable standalone Shift+I default outside Free View");
    }

    private static void ExpectRefused(Action action)
    {
        try { action(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new InvalidDataException("oversized member-facts subject list was accepted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
