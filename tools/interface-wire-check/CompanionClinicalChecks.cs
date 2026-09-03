using MSUIClient;
using MSUIClient.Net;

/// <summary>
/// Companions (COMPANIONS v1 — summon/dismiss the account's own alts as AI party
/// members). Verifies the wire law (request layout, kind-1 result, kind-2 list with
/// NUL-terminated names, exact/defensive lengths), the opcode and capability
/// constants, the friendly result texts, the roster flag, and the wiring nobody
/// would notice was gone (capability apply, dispatch, send guard, slash verb, reset).
/// </summary>
internal static class CompanionClinicalChecks
{
    public static void Run()
    {
        // ── Request: u8 action, u64 guid — exactly 9 bytes ──────────────────
        byte[] summon = CompanionWire.BuildRequest(CompanionWire.ActionSummon, 0x1122334455667788UL);
        Check(summon.Length == CompanionWire.RequestBytes && summon.Length == 9 &&
              summon[0] == CompanionWire.ActionSummon &&
              BitConverter.ToUInt64(summon, 1) == 0x1122334455667788UL,
            "companion request layout drift (action/raw little-endian guid)");
        byte[] list = CompanionWire.BuildRequest(CompanionWire.ActionList, 0);
        Check(list.Length == 9 && list[0] == CompanionWire.ActionList &&
              BitConverter.ToUInt64(list, 1) == 0,
            "the list request carries action 3 and guid 0");
        ExpectRefused(() => CompanionWire.BuildRequest(CompanionWire.ActionSummon, 0),
            "a summon with no character was accepted");
        ExpectRefused(() => CompanionWire.BuildRequest(CompanionWire.ActionDismiss, 0),
            "a dismiss with no character was accepted");
        ExpectRefused(() => CompanionWire.BuildRequest(9, 1),
            "an unknown companion action was accepted");

        // ── Result: u8 kind(1), u8 action, u64 guid, u8 result — exactly 11 ─
        var w = new PacketWriter();
        w.WriteU8(CompanionWire.KindResult);
        w.WriteU8(CompanionWire.ActionSummon);
        w.WriteU64(0xABCDEF01UL);
        w.WriteU8(CompanionWire.ResultLimit);
        byte[] answer = w.ToArray();
        Check(answer.Length == CompanionWire.ResultBytes && answer.Length == 11,
            "companion result fixture is not the 11-byte body — the constants drifted");
        Check(CompanionWire.TryReadKind(answer, out byte kind) && kind == CompanionWire.KindResult,
            "kind byte must lead the reply");
        Check(CompanionWire.TryParseResult(answer, out CompanionResult parsed) &&
              parsed.Action == CompanionWire.ActionSummon && parsed.Guid == 0xABCDEF01UL &&
              parsed.Result == CompanionWire.ResultLimit,
            "companion result drift (action/guid/result)");
        Check(!CompanionWire.TryParseResult([.. answer, 0], out _),
            "a companion result with a trailing byte must be refused");
        Check(!CompanionWire.TryParseResult(answer[..^1], out _),
            "a truncated companion result must be refused");
        Check(!CompanionWire.TryParseList(answer, out _),
            "a kind-1 body must not parse as a list");

        // ── List: u8 kind(2), u8 count, rows{u64,u8×5,cstring} ──────────────
        var l = new PacketWriter();
        l.WriteU8(CompanionWire.KindList);
        l.WriteU8(3);
        WriteRow(l, 0x10UL, 1, 8, 0, 60, CompanionWire.StatePlaying, "Mainchar");
        WriteRow(l, 0x20UL, 4, 11, 1, 42, CompanionWire.StateSummonable, "Altdruid");
        WriteRow(l, 0x30UL, 6, 7, 0, 7, CompanionWire.StateCompanion, "");
        byte[] listBody = l.ToArray();
        Check(CompanionWire.TryParseList(listBody, out CompanionRow[] rows) && rows.Length == 3,
            "companion list did not parse / wrong row count");
        Check(rows[0] is { Guid: 0x10UL, Race: 1, Class: 8, Gender: 0, Level: 60, Name: "Mainchar" } &&
              rows[0].IsPlaying && !rows[0].Summonable,
            "companion list row 0 field drift");
        Check(rows[1] is { Guid: 0x20UL, Race: 4, Class: 11, Gender: 1, Level: 42, Name: "Altdruid" } &&
              rows[1].Summonable,
            "companion list row 1 field drift");
        Check(rows[2] is { Guid: 0x30UL, Name: "" } && rows[2].IsCompanion,
            "an empty (NUL-only) name must still parse as a row");
        Check(!CompanionWire.TryParseList([.. listBody, 0], out _),
            "a list with a trailing byte must be refused");
        Check(!CompanionWire.TryParseList(listBody[..^1], out _),
            "a list missing its final NUL must be refused");
        Check(!CompanionWire.TryParseList([CompanionWire.KindList, 1, 0, 0], out _),
            "a list whose row is shorter than the fixed prefix must be refused");
        Check(CompanionWire.TryParseList([CompanionWire.KindList, 0], out CompanionRow[] none) &&
              none.Length == 0,
            "an empty list (count 0) is valid");
        Check(!CompanionWire.TryParseList([], out _) && !CompanionWire.TryReadKind([], out _),
            "an empty body is not a companion reply");

        // ── Vocabulary: every state has a word, every result a reason ────────
        foreach (byte state in new[] { CompanionWire.StateSummonable, CompanionWire.StateCompanion,
                     CompanionWire.StateLoading, CompanionWire.StatePlaying,
                     CompanionWire.StateUnavailable })
            Check(!CompanionWire.StateWord(state).StartsWith("State ", StringComparison.Ordinal),
                $"companion state {state} has no status word");
        Check(CompanionWire.StateWord(CompanionWire.StatePlaying) == "You" &&
              CompanionWire.StateWord(CompanionWire.StateSummonable) == "Summonable" &&
              CompanionWire.StateWord(CompanionWire.StateCompanion) == "Companion" &&
              CompanionWire.StateWord(CompanionWire.StateUnavailable) == "Unavailable" &&
              CompanionWire.StateWord(CompanionWire.StateLoading).StartsWith("Summoning", StringComparison.Ordinal),
            "companion status words drifted from the spec");
        foreach (byte code in new[] { CompanionWire.ResultDenied, CompanionWire.ResultAlreadyInWorld,
                     CompanionWire.ResultOwnerState, CompanionWire.ResultLimit,
                     CompanionWire.ResultNotACompanion, CompanionWire.ResultFailed,
                     CompanionWire.ResultPartyFull })
            Check(!CompanionWire.DescribeResult(CompanionWire.ActionSummon, code, "X")
                      .StartsWith("Companion request failed", StringComparison.Ordinal),
                $"companion result {code} has no human-readable reason");
        Check(CompanionWire.DescribeResult(CompanionWire.ActionSummon, CompanionWire.ResultOwnerState, "")
                  == "You must be in the world, alive and outdoors to summon." &&
              CompanionWire.DescribeResult(CompanionWire.ActionSummon, CompanionWire.ResultLimit, "")
                  == "Companion limit reached (9).",
            "the spec'd friendly texts for result 3 / 4 drifted");
        Check(CompanionWire.ResultPartyFull == 7 &&
              CompanionWire.DescribeResult(CompanionWire.ActionSummon, CompanionWire.ResultPartyFull, "")
                  == "Your party is full. Convert it to a raid to summon more.",
            "result 7 (party full) drifted from the spec");
        Check(CompanionWire.MaxCompanions == 9, "companion ceiling is 9");

        // ── Opcode and capability constants ──────────────────────────────────
        Check((ushort)Op.CMSG_SUI_COMPANION == 0x0362 && (ushort)Op.SMSG_SUI_COMPANION == 0x0363,
            "COMPANIONS v1 opcode drift — the core pins 866/867");
        Check(SuiCapabilityWire.CompanionsV1 == 1u << 7, "companions-v1 is capability bit 7");

        // ── Wiring nobody would notice was gone ──────────────────────────────
        string root = ClientConfig.FindRepoRoot();
        string facts = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.MemberFacts.cs"));
        Check(facts.Contains("ApplyCompanionsCapability(capabilities);", StringComparison.Ordinal),
            "without the capability apply site bit 7 is never observed and every act refuses");

        string net = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        Check(net.Contains("case Op.SMSG_SUI_COMPANION:", StringComparison.Ordinal) &&
              net.Contains("ApplySuiCompanion(body);", StringComparison.Ordinal),
            "without the dispatch every verdict and list is silently discarded");

        string roster = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.CompanionRoster.cs"));
        Check(roster.Contains("if (!_companionsAvailable)", StringComparison.Ordinal) &&
              roster.Contains("_net.SuiCompanion(action, guid)", StringComparison.Ordinal),
            "the one send site must refuse before capability bit 7 has been observed — " +
            "older cores close the socket on an opcode beyond their table");

        string control = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Control.cs"));
        Check(control.Contains("SuiRosterOwnCompanion = 0x08", StringComparison.Ordinal) &&
              control.Contains("private bool IsOwnCompanion(ulong guid)", StringComparison.Ordinal) &&
              control.Contains("ResetCompanions();", StringComparison.Ordinal),
            "roster flag 0x08 / IsOwnCompanion / world-change reset must be pinned");

        string chat = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Chat.cs"));
        Check(chat.Contains("command is \"/companions\" or \"/comp\"", StringComparison.Ordinal) &&
              chat.Contains("ToggleCompanionsPanel();", StringComparison.Ordinal),
            "the slash verb has exactly one dispatch site and it was not pinned");

        string panel = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Companions.cs"));
        Check(panel.Contains("DrawVanillaPanelChrome(\"Companions\"", StringComparison.Ordinal) &&
              panel.Contains("GameText.Draw(", StringComparison.Ordinal) &&
              panel.Contains("VanillaButton(", StringComparison.Ordinal) &&
              panel.Contains("This server does not support companions.", StringComparison.Ordinal),
            "Companions panel must render via DrawVanillaPanelChrome/GameText/VanillaButton and " +
            "say so when the server lacks the capability");

        string feedback = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.CombatFeedback.cs"));
        Check(feedback.Contains("DrawCompanionsPanel();", StringComparison.Ordinal),
            "the panel is only visible if the HUD draw sequence calls it");

        Console.WriteLine("interface-wire-check: Companions PASS");
    }

    private static void WriteRow(PacketWriter w, ulong guid, byte race, byte cls, byte gender,
        byte level, byte state, string name)
    {
        w.WriteU64(guid);
        w.WriteU8(race);
        w.WriteU8(cls);
        w.WriteU8(gender);
        w.WriteU8(level);
        w.WriteU8(state);
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(name)) w.WriteU8(b);
        w.WriteU8(0);
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
