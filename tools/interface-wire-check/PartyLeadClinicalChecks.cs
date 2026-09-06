using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

/// <summary>
/// Party lead claim (PLAN_20 P4a — take the lead back from a companion bot).
/// Verifies the wire law, the opcode/capability constants including the pair and
/// bit still reserved for P4's vendor slice, the self-only rule, and the parity
/// law that keeps this SuperUI verb out of vanilla's menus and alias tables.
/// </summary>
internal static class PartyLeadClinicalChecks
{
    public static void Run()
    {
        // ── Request: u8 action, u64 subject ──────────────────────────────────
        byte[] body = PartyLeadWire.BuildPartyLeadBody(
            PartyLeadWire.ActionClaim, 0x1122334455667788UL);
        Check(body.Length == PartyLeadWire.RequestBytes && body.Length == 9 &&
              body[0] == PartyLeadWire.ActionClaim &&
              BitConverter.ToUInt64(body, 1) == 0x1122334455667788UL,
            "party-lead request layout drift (action/raw little-endian subject)");

        // There is no implicit self: the subject is always stated, so the packet
        // says who it is about rather than depending on who happened to send it.
        ExpectRefused(() => PartyLeadWire.BuildPartyLeadBody(PartyLeadWire.ActionClaim, 0),
            "a party-lead request with no subject was accepted");

        // ── Result: u8 action, u64 subject, u8 result ────────────────────────
        var w = new PacketWriter();
        w.WriteU8(PartyLeadWire.ActionClaim);
        w.WriteU64(0xABCDEF01UL);
        w.WriteU8(PartyLeadWire.ResultLeaderIsPlayer);
        byte[] answer = w.ToArray();

        Check(answer.Length == PartyLeadWire.ResultBytes && answer.Length == 10,
            "party-lead result fixture is not the 10-byte body — the constants drifted");
        Check(PartyLeadWire.TryParsePartyLeadResult(answer, out PartyLeadResult parsed) &&
              parsed.Action == PartyLeadWire.ActionClaim && parsed.Subject == 0xABCDEF01UL &&
              parsed.Result == PartyLeadWire.ResultLeaderIsPlayer,
            "party-lead result drift (action/subject/result)");
        Check(!PartyLeadWire.TryParsePartyLeadResult([.. answer, 0], out _),
            "a party-lead result with a trailing byte must be refused");
        Check(!PartyLeadWire.TryParsePartyLeadResult(answer[..^1], out _),
            "a truncated party-lead result must be refused");

        // Every refusal must be able to SAY which rule refused it — a party act
        // that collapses its reasons into one "failed" is worse than none.
        foreach (byte code in new[] { PartyLeadWire.ResultNotInGroup,
                     PartyLeadWire.ResultAlreadyLeader, PartyLeadWire.ResultLeaderIsPlayer,
                     PartyLeadWire.ResultSubjectNotInGroup, PartyLeadWire.ResultSubjectNotSelf,
                     PartyLeadWire.ResultNoSubject, PartyLeadWire.ResultBadAction })
            Check(!PartyLeadWire.ResultName(code).StartsWith("unknown result",
                      StringComparison.Ordinal),
                $"party-lead result {code} has no human-readable reason");

        // ── Opcode and capability constants ──────────────────────────────────
        Check((ushort)Op.CMSG_SUI_PARTY_LEAD == 0x035E &&
              (ushort)Op.SMSG_SUI_PARTY_LEAD_RESULT == 0x035F,
            "PLAN_20 P4a opcode drift — the core pins 862/863");
        Check(SuiCapabilityWire.PartyLeadV1 == 1u << 9,
            "party-lead-v1 is capability bit 9");
        Check(!Enum.IsDefined(typeof(Op), (ushort)0x035A) &&
              !Enum.IsDefined(typeof(Op), (ushort)0x035B),
            "0x035A/0x035B stay reserved for P4's vendor slice — narrowed by the owner, " +
            "not abandoned, so a live design still names them");

        // ── SuperUI furniture: slash aliases plus the party-member popup row ──
        Check(PartyLeadCommandLaw.IsClaimLead("/claimlead") &&
              PartyLeadCommandLaw.IsClaimLead("/TakeLead"),
            "the claim-lead aliases must resolve, case-insensitively");
        Check(GroupSlashCommandLaw.Resolve("/claimlead") is null &&
              GroupSlashCommandLaw.Resolve("/takelead") is null,
            "GroupSlashCommandLaw is a PARITY surface listing vanilla's own GlobalStrings " +
            "aliases; a SuperUI-only verb in it makes the parity tables assert a command " +
            "the 1.12 client never had");
        Check(Enum.IsDefined(UnitPopupRow.ClaimLead) &&
              UnitPopupUiLaw.RowText(UnitPopupRow.ClaimLead) == "Claim Party Lead",
            "the SuperUI claim-lead party popup row is missing or renamed");

        // ── Wiring nobody would notice was gone ──────────────────────────────
        string root = ClientConfig.FindRepoRoot();
        string facts = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.MemberFacts.cs"));
        Check(facts.Contains("ApplyPartyLeadCapability(capabilities);", StringComparison.Ordinal),
            "without the capability apply site the bit is never observed and every claim refuses");

        string net = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        Check(net.Contains("case Op.SMSG_SUI_PARTY_LEAD_RESULT:", StringComparison.Ordinal) &&
              net.Contains("ApplySuiPartyLeadResult(body);", StringComparison.Ordinal),
            "without the result dispatch a refusal is silently discarded and the claim " +
            "looks like it did nothing");

        string chat = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Chat.cs"));
        Check(chat.Contains("PartyLeadCommandLaw.IsClaimLead(command)", StringComparison.Ordinal) &&
              chat.Contains("RequestPartyLeadClaim();", StringComparison.Ordinal),
            "the slash verb has exactly one dispatch site and it was not pinned");

        string lead = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.PartyLead.cs"));
        Check(lead.Contains("_net.SuiPartyLead(PartyLeadWire.ActionClaim, LocalPlayerGuid)",
                  StringComparison.Ordinal),
            "v1 claims the lead for OURSELVES only — promoting one bot over another is a " +
            "separate decision with its own failure modes and must not be smuggled in here");

        string control = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Control.cs"));
        Check(control.Contains("ResetPartyLead();", StringComparison.Ordinal),
            "party-lead state must reset with the rest of the PLAN_20 state on a world change");

        Console.WriteLine("interface-wire-check: PartyLead PASS");
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
