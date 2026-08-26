namespace MSUIClient.Net;

/// <summary>One party-lead act's outcome.</summary>
public readonly record struct PartyLeadResult(byte Action, ulong Subject, byte Result);

/// <summary>
/// Party lead claim (PLAN_20 P4a).
///
/// The fleet's own grouping makes a bot the leader: <c>BridgeHandleFormGroup</c>
/// calls <c>Group::Create</c> with the bot as leader and adds members to it. Once
/// you are in that group there is no way back — vanilla's
/// <c>HandleGroupSetLeaderOpcode</c> requires you to ALREADY be the leader to
/// promote anyone, and refuses a self-target outright. So the commander cannot
/// rearrange or break up their own party.
///
/// This is the way back, and it is deliberately narrow: leadership may only ever
/// be taken from an <b>AiBot</b> in your own group. Taking it from a real player
/// would be a griefing verb, and the party line is the same authorization the
/// rest of PLAN_20 already uses.
///
/// Exact-length parsing by wire law.
/// </summary>
public static class PartyLeadWire
{
    /// <summary>Take group leadership for the subject (v1: the requester).</summary>
    public const byte ActionClaim = 1;

    // Result vocabulary. Every refusal says WHICH rule refused it — a party act
    // that collapsed its reasons into one "failed" would be worse than none.
    public const byte ResultOk = 0;
    public const byte ResultNotInGroup = 1;
    public const byte ResultAlreadyLeader = 2;
    public const byte ResultLeaderIsPlayer = 3;
    public const byte ResultSubjectNotInGroup = 4;
    public const byte ResultSubjectNotSelf = 5;
    public const byte ResultNoSubject = 6;
    public const byte ResultBadAction = 7;

    /// <summary>u8 action + u64 subject.</summary>
    public const int RequestBytes = 9;

    /// <summary>u8 action + u64 subject + u8 result.</summary>
    public const int ResultBytes = 10;

    public static string ResultName(byte result) => result switch
    {
        ResultOk => "ok",
        ResultNotInGroup => "you are not in a group",
        ResultAlreadyLeader => "you already lead this group",
        ResultLeaderIsPlayer => "the leader is a real player — ask them",
        ResultSubjectNotInGroup => "that character is not in your group",
        ResultSubjectNotSelf => "you can only claim the lead for yourself",
        ResultNoSubject => "no such character",
        ResultBadAction => "unknown party-lead action",
        _ => $"unknown result {result}",
    };

    public static byte[] BuildPartyLeadBody(byte action, ulong subject)
    {
        if (subject == 0)
            throw new ArgumentOutOfRangeException(nameof(subject),
                "a party-lead act must name its subject; there is no implicit self.");
        var w = new PacketWriter(RequestBytes);
        w.WriteU8(action);
        w.WriteU64(subject);
        return w.ToArray();
    }

    /// <summary>
    /// SMSG_SUI_PARTY_LEAD_RESULT: u8 action, u64 subject, u8 result. Exact
    /// length only — a body that is one byte off is a different packet.
    /// </summary>
    public static bool TryParsePartyLeadResult(byte[] body, out PartyLeadResult result)
    {
        result = default;
        if (body.Length != ResultBytes) return false;
        var r = new PacketReader(body);
        byte action = r.ReadU8();
        ulong subject = r.ReadU64();
        byte code = r.ReadU8();
        if (subject == 0) return false;
        result = new PartyLeadResult(action, subject, code);
        return true;
    }
}
