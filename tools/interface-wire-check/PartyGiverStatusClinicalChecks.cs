using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

/// <summary>
/// Party questgiver status (PLAN_20 P5 — the parenthesised numeral over the
/// vanilla !/? art). Verifies the wire law (pull builder + exact-length status
/// parser), the opcode and capability constants including the pair still
/// reserved for P4, the counting law that decides what the numeral says, and
/// the additive law that keeps a vanilla screenshot unchanged.
/// </summary>
internal static class PartyGiverStatusClinicalChecks
{
    public static void Run()
    {
        // ── Pull builder: u8 flags, u8 count, u64 guids ──────────────────────
        byte[] two = GiverStatusWire.BuildGiverStatusBody([0x1122334455667788UL, 9UL]);
        Check(two.Length == 18 && two[0] == 0 && two[1] == 2 &&
              BitConverter.ToUInt64(two, 2) == 0x1122334455667788UL &&
              BitConverter.ToUInt64(two, 10) == 9UL,
            "giver-status pull body layout drift (flags/count/raw little-endian guids)");

        // Unlike the facts pull, an empty list is NOT shorthand for "everything".
        // The server's work must stay proportional to what it was asked about,
        // and a whole-zone sweep is exactly what this wire refuses to become.
        ExpectRefused(() => GiverStatusWire.BuildGiverStatusBody([]),
            "an empty giver-status pull was accepted as whole-zone shorthand");
        ExpectRefused(() => GiverStatusWire.BuildGiverStatusBody(
                [.. Enumerable.Repeat(1UL, GiverStatusWire.MaximumGivers + 1)]),
            "an oversized giver list was accepted");

        // ── Status parser: u8 flags, u16 count, 17-byte entries ──────────────
        var w = new PacketWriter();
        w.WriteU8(0);
        w.WriteU16(3);
        WriteEntry(w, 0xAA, 0x01, GiverStatusWire.DialogAvailable);
        WriteEntry(w, 0xAA, 0x02, GiverStatusWire.DialogReward2);
        WriteEntry(w, 0xBB, 0x01, GiverStatusWire.DialogIncomplete);
        byte[] body = w.ToArray();

        Check(body.Length == GiverStatusWire.StatusHeaderBytes +
                  3 * GiverStatusWire.StatusEntryBytes,
            "giver-status fixture is not header + 3 fixed-stride entries — the stride drifted");
        Check(GiverStatusWire.TryParseGiverStatus(body, out GiverMemberStatus[] entries) &&
              entries.Length == 3 &&
              entries[0] == new GiverMemberStatus(0xAA, 0x01, GiverStatusWire.DialogAvailable) &&
              entries[2] == new GiverMemberStatus(0xBB, 0x01, GiverStatusWire.DialogIncomplete),
            "giver-status entry drift (giver/member/status)");

        // Exact length only — a body one byte off is a different packet.
        Check(!GiverStatusWire.TryParseGiverStatus([.. body, 0], out _),
            "a giver-status body with a trailing byte must be refused");
        Check(!GiverStatusWire.TryParseGiverStatus(body[..^1], out _),
            "a truncated giver-status body must be refused");
        Check(!GiverStatusWire.TryParseGiverStatus([0, 0], out _),
            "a giver-status body shorter than its header must be refused");

        // A zero guid on either side is a malformed row, not an empty one: it
        // would key the client's map under 0 and count toward nothing.
        var zero = new PacketWriter();
        zero.WriteU8(0);
        zero.WriteU16(1);
        WriteEntry(zero, 0, 0x01, GiverStatusWire.DialogAvailable);
        Check(!GiverStatusWire.TryParseGiverStatus(zero.ToArray(), out _),
            "a giver-status row with a zero guid must be refused");

        // ── Opcode and capability constants ──────────────────────────────────
        Check((ushort)Op.CMSG_SUI_GIVER_STATUS == 0x035C &&
              (ushort)Op.SMSG_SUI_GIVER_STATUS == 0x035D,
            "PLAN_20 P5 opcode drift — the core pins 860/861");
        Check(SuiCapabilityWire.PartyGiverStatusV1 == 1u << 8,
            "party-giver-status-v1 is capability bit 8; bit 7 stays reserved for P4's vendor");
        Check(!Enum.IsDefined(typeof(Op), (ushort)0x035A) &&
              !Enum.IsDefined(typeof(Op), (ushort)0x035B),
            "0x035A/0x035B are PLAN_20 P4's reserved vendor pair — P5 shipping first must " +
            "not renumber onto them, because the core reserves the same two indices");

        // ── The counting law ─────────────────────────────────────────────────
        // REWARD_REP (4) draws as a blue question mark but MEANS "available";
        // counting it with the turn-ins it resembles would put the wrong number
        // over every repeatable questgiver in the game.
        Check(QuestMarkerUiLaw.FamilyOf(GiverStatusWire.DialogRewardRep) == QuestMarkerFamily.Take &&
              QuestMarkerUiLaw.FamilyOf(GiverStatusWire.DialogAvailable) == QuestMarkerFamily.Take,
            "DIALOG_STATUS_REWARD_REP and _AVAILABLE both mean 'can take' and must count together");
        Check(QuestMarkerUiLaw.FamilyOf(GiverStatusWire.DialogReward2) == QuestMarkerFamily.TurnIn &&
              QuestMarkerUiLaw.FamilyOf(GiverStatusWire.DialogRewardOld) == QuestMarkerFamily.TurnIn,
            "DIALOG_STATUS_REWARD2 and _REWARD_OLD are the turn-in family");
        foreach (byte quiet in new[] { GiverStatusWire.DialogNone, GiverStatusWire.DialogUnavailable,
                     GiverStatusWire.DialogChat, GiverStatusWire.DialogIncomplete })
            Check(QuestMarkerUiLaw.FamilyOf(quiet) == QuestMarkerFamily.None,
                $"dialog status {quiet} is not actionable and must not be counted");

        // Our own status picks the family when we have business here; when we do
        // not, the numeral speaks for whoever does — that is the case where it
        // earns its keep, because walking past is the alternative.
        Check(QuestMarkerUiLaw.NumeralFamily(GiverStatusWire.DialogReward2, 3, 1)
                  == QuestMarkerFamily.TurnIn,
            "our own turn-in status must pick the turn-in count, not the larger one");
        Check(QuestMarkerUiLaw.NumeralFamily(GiverStatusWire.DialogUnavailable, 0, 2)
                  == QuestMarkerFamily.TurnIn,
            "a grey marker must still speak for members who DO have business here");
        Check(QuestMarkerUiLaw.NumeralFamily(GiverStatusWire.DialogUnavailable, 0, 0)
                  == QuestMarkerFamily.None,
            "nobody with business means no numeral");

        // ── The additive law ─────────────────────────────────────────────────
        // "(1)" over our own available quest is what vanilla already says by
        // drawing the marker at all. Drawing nothing there is what keeps solo
        // play pixel-identical, which is the whole basis of this being additive.
        Check(!QuestMarkerUiLaw.ShowNumeral(GiverStatusWire.DialogAvailable,
                  QuestMarkerFamily.Take, 1),
            "a count of one that is US must draw NO numeral — vanilla already said it");
        Check(QuestMarkerUiLaw.ShowNumeral(GiverStatusWire.DialogAvailable,
                  QuestMarkerFamily.Take, 2),
            "two or more must draw the numeral");
        Check(QuestMarkerUiLaw.ShowNumeral(GiverStatusWire.DialogUnavailable,
                  QuestMarkerFamily.Take, 1),
            "a count of one that is NOT us must draw — nothing else on screen says it");
        Check(QuestMarkerUiLaw.NumeralText(4) == "(4)",
            "owner decision 5 is a PARENTHESISED numeral");
        Check(QuestMarkerUiLaw.NumeralFontObject == "GameFontNormalLarge",
            "decision 5 keeps the vanilla quest font and yellow — GameFontNormalLarge is " +
            "FrizQt/gold/black-shadow, the quest font object itself");

        // The marker art must be untouched: the numeral hangs OVER vanilla's
        // markers and never adds, moves or restyles one.
        Check(QuestMarkerUiLaw.Style(5)?.ModelPath == @"Interface\Buttons\TalkToMe.m2" &&
              QuestMarkerUiLaw.Style(6)?.ModelPath == @"Interface\Buttons\TalkToMeQuestionMark.m2" &&
              QuestMarkerUiLaw.Style(0) is null,
            "P5 must not have disturbed the vanilla marker art mapping");

        // ── Wiring nobody would notice was gone ──────────────────────────────
        string root = ClientConfig.FindRepoRoot();
        string markers = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.QuestMarkers.cs"));
        Check(markers.Contains("if (_uiParityArmed) return;", StringComparison.Ordinal),
            "the numeral must not draw while a UI-parity proof is armed");
        Check(markers.Contains("UpdatePartyGiverStatus(_questMarkerGuids);", StringComparison.Ordinal),
            "the pull must be driven by the markers actually on screen");
        Check(markers.Contains("_questMarkerGuids.Clear();", StringComparison.Ordinal),
            "the marked-giver set must be rebuilt each frame, not accumulated forever");

        string feedback = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.CombatFeedback.cs"));
        Check(feedback.Contains("DrawQuestMarkerNumerals();", StringComparison.Ordinal),
            "the numeral has exactly one draw call and it was not pinned");

        string facts = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.MemberFacts.cs"));
        Check(facts.Contains("ApplyPartyGiverStatusCapability(capabilities);", StringComparison.Ordinal),
            "without the capability apply site the bit is never observed and no numeral ever draws");

        string giver = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.GiverStatus.cs"));
        Check(giver.Contains("if (guid == LocalPlayerGuid) continue;", StringComparison.Ordinal),
            "our own verdict must come from _questStatuses, not from the wire — otherwise the " +
            "numeral and the marker under it can disagree about us");
        Check(giver.Contains("_giverMemberStatuses[entry.Giver] = [];", StringComparison.Ordinal),
            "a giver the server answers for must have its map REPLACED, or a member who " +
            "stops having business there is counted forever at a stale verdict");

        string control = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Control.cs"));
        Check(control.Contains("ResetPartyGiverStatus();", StringComparison.Ordinal),
            "giver status must reset with the rest of the PLAN_20 state on a world change");

        Console.WriteLine("interface-wire-check: PartyGiverStatus PASS");
    }

    private static void WriteEntry(PacketWriter w, ulong giver, ulong member, byte status)
    {
        w.WriteU64(giver);
        w.WriteU64(member);
        w.WriteU8(status);
    }

    private static void ExpectRefused(Action action, string message)
    {
        try { action(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new InvalidDataException(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
