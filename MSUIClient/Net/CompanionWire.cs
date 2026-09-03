using System.Text;

namespace MSUIClient.Net;

/// <summary>
/// One character of the account in the companions list (SMSG_SUI_COMPANION kind 2).
/// </summary>
/// <param name="Guid">Raw character guid (the summon/dismiss subject).</param>
/// <param name="Race">Vanilla race id.</param>
/// <param name="Class">Vanilla class id.</param>
/// <param name="Gender">0 male, 1 female.</param>
/// <param name="Level">Character level.</param>
/// <param name="State">One of the <c>CompanionWire.State*</c> codes.</param>
/// <param name="Name">The character name.</param>
public readonly record struct CompanionRow(
    ulong Guid, byte Race, byte Class, byte Gender, byte Level, byte State, string Name)
{
    public bool Summonable => State == CompanionWire.StateSummonable;
    public bool IsCompanion => State == CompanionWire.StateCompanion;
    public bool IsLoading => State == CompanionWire.StateLoading;
    public bool IsPlaying => State == CompanionWire.StatePlaying;
}

/// <summary>One summon/dismiss verdict (SMSG_SUI_COMPANION kind 1).</summary>
public readonly record struct CompanionResult(byte Action, ulong Guid, byte Result);

/// <summary>
/// Companions wire. A player summons their OWN other characters (alts on the same
/// account) into the world as AI-driven party members, and dismisses them again.
/// The server holds all authority: it answers every act with a kind-1 result and
/// pushes a fresh kind-2 list after every result, whenever a companion finishes
/// entering the world or leaves, and in answer to the list request.
///
/// Request (CMSG_SUI_COMPANION): u8 action, u64 guid — exactly 9 bytes; guid 0 for list.
/// Reply (SMSG_SUI_COMPANION): u8 kind, then
///   kind 1: u8 action, u64 guid, u8 result (exactly 11 bytes),
///   kind 2: u8 count, count × { u64 guid, u8 race, u8 class, u8 gender, u8 level,
///           u8 state, cstring name } (variable length; parsed defensively, no trailing bytes).
/// The client must not send the request until capability bit 7 (COMPANIONS v1) has been
/// observed in the SMSG_SUI_CONTROL_ACK capability trailer.
/// </summary>
public static class CompanionWire
{
    // CMSG actions.
    public const byte ActionSummon = 1;
    public const byte ActionDismiss = 2;
    public const byte ActionList = 3;

    // SMSG kinds.
    public const byte KindResult = 1;
    public const byte KindList = 2;

    // Result codes (kind 1).
    public const byte ResultOk = 0;
    public const byte ResultDenied = 1;          // not a character on your account / unknown
    public const byte ResultAlreadyInWorld = 2;
    public const byte ResultOwnerState = 3;      // must be in world, alive, outdoors, not on taxi/transport, not teleporting, not driving a bot
    public const byte ResultLimit = 4;           // max companions reached
    public const byte ResultNotACompanion = 5;   // dismiss target is not one of your summoned companions
    public const byte ResultFailed = 6;          // server could not load the character
    public const byte ResultPartyFull = 7;       // owner's party has no free slot (convert to raid)

    // Row states (kind 2).
    public const byte StateSummonable = 0;       // offline / summonable
    public const byte StateCompanion = 1;        // online as your companion
    public const byte StateLoading = 2;          // summon in progress
    public const byte StatePlaying = 3;          // the character you are playing
    public const byte StateUnavailable = 4;

    /// <summary>Server-side ceiling on simultaneous companions.</summary>
    public const int MaxCompanions = 9;

    /// <summary>u8 action + u64 guid.</summary>
    public const int RequestBytes = 9;

    /// <summary>u8 kind + u8 action + u64 guid + u8 result.</summary>
    public const int ResultBytes = 11;

    /// <summary>u8 kind + u8 count.</summary>
    public const int ListHeaderBytes = 2;

    /// <summary>u64 guid + race/class/gender/level/state bytes + the name's NUL.</summary>
    public const int ListRowMinBytes = 14;

    /// <summary>
    /// CMSG_SUI_COMPANION body. Summon and dismiss must name a character; only the
    /// list request carries guid 0. An unknown action is refused here rather than sent.
    /// </summary>
    public static byte[] BuildRequest(byte action, ulong guid)
    {
        if (action is not (ActionSummon or ActionDismiss or ActionList))
            throw new ArgumentOutOfRangeException(nameof(action), $"unknown companion action {action}");
        if (action != ActionList && guid == 0)
            throw new ArgumentOutOfRangeException(nameof(guid),
                "a companion summon/dismiss must name its character; there is no implicit subject.");
        var w = new PacketWriter(RequestBytes);
        w.WriteU8(action);
        w.WriteU64(guid);
        return w.ToArray();
    }

    /// <summary>The leading kind byte, or false for an empty body.</summary>
    public static bool TryReadKind(byte[] body, out byte kind)
    {
        kind = 0;
        if (body.Length < 1) return false;
        kind = body[0];
        return true;
    }

    /// <summary>
    /// Kind 1: u8 kind(1), u8 action, u64 guid, u8 result. Exact length only — a body
    /// that is one byte off is a different packet.
    /// </summary>
    public static bool TryParseResult(byte[] body, out CompanionResult result)
    {
        result = default;
        if (body.Length != ResultBytes || body[0] != KindResult) return false;
        var r = new PacketReader(body);
        r.Skip(1);
        byte action = r.ReadU8();
        ulong guid = r.ReadU64();
        byte code = r.ReadU8();
        result = new CompanionResult(action, guid, code);
        return true;
    }

    /// <summary>
    /// Kind 2: u8 kind(2), u8 count, then count rows each ending in a NUL-terminated
    /// name. Variable length, so any underrun or trailing byte makes it not this packet.
    /// Rows keep the server's order; the caller decides presentation order.
    /// </summary>
    public static bool TryParseList(byte[] body, out CompanionRow[] rows)
    {
        rows = [];
        if (body.Length < ListHeaderBytes || body[0] != KindList) return false;
        try
        {
            var r = new PacketReader(body);
            r.Skip(1);
            int count = r.ReadU8();
            if (r.Remaining < count * ListRowMinBytes) return false;
            var parsed = new CompanionRow[count];
            for (int i = 0; i < count; i++)
            {
                ulong guid = r.ReadU64();
                byte race = r.ReadU8();
                byte cls = r.ReadU8();
                byte gender = r.ReadU8();
                byte level = r.ReadU8();
                byte state = r.ReadU8();
                // PacketReader.ReadCString tolerates a missing terminator (it returns the
                // tail); this packet does not — a name without its NUL is a truncated body.
                int nameStart = r.Position;
                int nul = Array.IndexOf(body, (byte)0, nameStart);
                if (nul < 0) return false;
                string name = Encoding.UTF8.GetString(body, nameStart, nul - nameStart);
                r.Skip(nul - nameStart + 1);
                if (guid == 0) return false;
                parsed[i] = new CompanionRow(guid, race, cls, gender, level, state, name);
            }
            if (r.HasMore) return false;   // trailing bytes → not this packet
            rows = parsed;
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    /// <summary>The status word a row wears in the Companions window.</summary>
    public static string StateWord(byte state) => state switch
    {
        StateSummonable => "Summonable",
        StateCompanion => "Companion",
        StateLoading => "Summoning…",
        StatePlaying => "You",
        StateUnavailable => "Unavailable",
        _ => $"State {state}",
    };

    public static bool IsSuccess(byte result) => result == ResultOk;

    /// <summary>
    /// Player-facing text for a result. Successes name the act and the character;
    /// every refusal says which rule refused it.
    /// </summary>
    public static string DescribeResult(byte action, byte result, string name)
    {
        string who = string.IsNullOrEmpty(name) ? "That character" : name;
        return result switch
        {
            ResultOk => action switch
            {
                ActionSummon => $"Summoning {who}…",
                ActionDismiss => $"{who} dismissed.",
                ActionList => "Companion list refreshed.",
                _ => "Done.",
            },
            ResultDenied => $"{who} is not a character on your account.",
            ResultAlreadyInWorld => $"{who} is already in the world.",
            ResultOwnerState => "You must be in the world, alive and outdoors to summon.",
            ResultLimit => $"Companion limit reached ({MaxCompanions}).",
            ResultNotACompanion => $"{who} is not one of your companions.",
            ResultFailed => $"The server could not load {who}.",
            ResultPartyFull => "Your party is full. Convert it to a raid to summon more.",
            _ => $"Companion request failed (code {result}).",
        };
    }
}
