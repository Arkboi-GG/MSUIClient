using MSUIClient;
using MSUIClient.Net;

/// <summary>
/// Party quest acts (PLAN_20 P3). Verifies the act wire law (builder refusals +
/// exact-length result parser), the opcode/capability constants with P4 still
/// reserved, and the client laws this phase depends on: no whole-party
/// shorthand, per-member reward choice, honest per-member refusals, and the
/// id-addressed abandon that reaches a quest held without a log slot.
/// </summary>
internal static class PartyQuestActsClinicalChecks
{
    public static void Run()
    {
        // ── Builder: header + 9-byte per-subject stride ───────────────────────
        byte[] body = PartyQuestWire.BuildPartyQuestBody(
            PartyQuestWire.ActionTurnIn, 3906, 0x1122334455667788UL,
            [new PartyQuestSubject(9UL, 2), new PartyQuestSubject(10UL, PartyQuestWire.RewardChoiceAuto)]);
        Check(body.Length == PartyQuestWire.RequestHeaderBytes + 2 * PartyQuestWire.SubjectBytes,
            "party-quest request is not header + fixed-stride subjects");
        Check(body[0] == PartyQuestWire.ActionTurnIn &&
              BitConverter.ToUInt32(body, 1) == 3906 &&
              BitConverter.ToUInt64(body, 5) == 0x1122334455667788UL &&
              body[13] == 2 &&
              BitConverter.ToUInt64(body, 14) == 9UL && body[22] == 2 &&
              BitConverter.ToUInt64(body, 23) == 10UL && body[31] == PartyQuestWire.RewardChoiceAuto,
            "party-quest request layout drift (action/quest/npc/count/subjects)");

        // ── The refusals that keep the act honest ─────────────────────────────
        // No whole-party shorthand: who acts on your order is never implicit.
        ExpectRefused(() => PartyQuestWire.BuildPartyQuestBody(
            PartyQuestWire.ActionAccept, 3906, 1, []),
            "an empty subject list was accepted — party acts must always be explicit");
        ExpectRefused(() => PartyQuestWire.BuildPartyQuestBody(
            PartyQuestWire.ActionAccept, 0, 1, [new PartyQuestSubject(9UL, 0)]),
            "a party act with no quest id was accepted");
        ExpectRefused(() => PartyQuestWire.BuildPartyQuestBody(
            9, 3906, 1, [new PartyQuestSubject(9UL, 0)]),
            "an unknown party-quest action was accepted");
        ExpectRefused(() => PartyQuestWire.BuildPartyQuestBody(
            PartyQuestWire.ActionAccept, 3906, 1,
            [.. Enumerable.Repeat(new PartyQuestSubject(1UL, 0), PartyQuestWire.MaximumSubjects + 1)]),
            "an oversized party-quest subject list was accepted");

        // ── Result parser: exact length, known action ─────────────────────────
        var w = new PacketWriter();
        w.WriteU8(PartyQuestWire.ActionAccept);
        w.WriteU32(3906);
        w.WriteU8(2);
        w.WriteU64(9UL);
        w.WriteU8(PartyQuestWire.ResultOk);
        w.WriteU64(10UL);
        w.WriteU8(PartyQuestWire.ResultLogFull);
        byte[] resultBody = w.ToArray();

        Check(PartyQuestWire.TryParsePartyQuestResult(resultBody, out PartyQuestResult result) &&
              result.Action == PartyQuestWire.ActionAccept && result.QuestId == 3906 &&
              result.Outcomes.Length == 2 &&
              result.Outcomes[0].Guid == 9UL && result.Outcomes[0].Result == PartyQuestWire.ResultOk &&
              result.Outcomes[1].Guid == 10UL && result.Outcomes[1].Result == PartyQuestWire.ResultLogFull,
            "party-quest result did not round-trip per-subject outcomes");

        Check(!PartyQuestWire.TryParsePartyQuestResult([], out _) &&
              !PartyQuestWire.TryParsePartyQuestResult(resultBody[..^1], out _) &&
              !PartyQuestWire.TryParsePartyQuestResult([.. resultBody, 0], out _),
            "party-quest result parser must refuse truncated/padded bodies");

        var badAction = new PacketWriter();
        badAction.WriteU8(7);
        badAction.WriteU32(1);
        badAction.WriteU8(0);
        Check(!PartyQuestWire.TryParsePartyQuestResult(badAction.ToArray(), out _),
            "a result naming an unknown action must be refused, not shown to the player");

        // ── Refusals are per member, by name ──────────────────────────────────
        Check(PartyQuestWire.ResultText(PartyQuestWire.ResultLogFull, "Kael").Contains("Kael",
                  StringComparison.Ordinal) &&
              PartyQuestWire.ResultText(PartyQuestWire.ResultTooFar, "Kael").Contains("Kael",
                  StringComparison.Ordinal) &&
              PartyQuestWire.ResultText(PartyQuestWire.ResultRequirements, "Kael") !=
              PartyQuestWire.ResultText(PartyQuestWire.ResultCannotReward, "Kael"),
            "every refusal must name the member and distinguish its reason — a party " +
            "act that says only \"failed\" is worse than no party act");
        Check(PartyQuestWire.Succeeded(PartyQuestWire.ResultOk) &&
              PartyQuestWire.Succeeded(PartyQuestWire.ResultAlreadyHeld) &&
              !PartyQuestWire.Succeeded(PartyQuestWire.ResultRequirements),
            "already-held is a benign success, not a failure the player must act on");

        // ── Opcodes + capability; P4 stays unclaimed ──────────────────────────
        Check((ushort)Op.CMSG_SUI_PARTY_QUEST == 856 &&
              (ushort)Op.SMSG_SUI_PARTY_QUEST_RESULT == 857 &&
              !Enum.IsDefined((Op)0x035A) && !Enum.IsDefined((Op)0x035B) &&
              SuiCapabilityWire.PartyQuestActsV1 == 1u << 6,
            "party-quest-act opcodes must sit at 856/857 with 858-859 reserved for " +
            "the PLAN_20 P4 vendor pair, capability bit 6");

        // ── Client laws ───────────────────────────────────────────────────────
        string root = ClientConfig.FindRepoRoot();
        string acts = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.PartyQuestActs.cs"));
        Check(acts.Contains("SuiCapabilityWire.PartyQuestActsV1", StringComparison.Ordinal) &&
              acts.Contains("if (!_partyQuestActsAvailable)", StringComparison.Ordinal),
            "party quest acts must stay capability-gated");
        Check(acts.Contains("PartyQuestWire.ActionAbandon, questId, 0,", StringComparison.Ordinal) &&
              acts.Contains("new PartyQuestSubject(LocalPlayerGuid,", StringComparison.Ordinal),
            "the id-addressed abandon must address our OWN guid — it is what reaches " +
            "a quest held past the update-field slots");

        string quest = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Quest.cs"));
        Check(quest.Contains("if (_partyQuestActsAvailable) return AbandonQuestById(questId);",
                  StringComparison.Ordinal),
            "abandoning an overflow quest must route through the id-addressed act " +
            "when the server offers it");
        Check(quest.Contains("ResetQuestPartyRail();", StringComparison.Ordinal),
            "closing the questgiver frame must clear the rail's per-member reward " +
            "picks, or they leak into the next quest");

        string rail = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.QuestPartyRail.cs"));
        Check(rail.Contains("if (_uiParityArmed) return;", StringComparison.Ordinal),
            "the rail must not draw while a UI-parity proof is armed");
        Check(rail.Contains("QuestFrameUiLaw.Width + QuestRailGap", StringComparison.Ordinal),
            "the rail must sit BESIDE the 384-wide quest frame — overlapping it would " +
            "move vanilla elements and break the frame's parity element tree");
        Check(rail.Contains("WowSkin.Dialog", StringComparison.Ordinal) &&
              !rail.Contains("UI-QuestBackground", StringComparison.Ordinal),
            "the rail is SuperUI furniture and must wear the SuperUI skin, not " +
            "FrameXML quest parchment it could never justify at parity");
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
