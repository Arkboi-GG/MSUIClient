namespace MSUIClient.Net;

/// <summary>One member the server could not (or did not) board (SMSG_SUI_PARTY_TAXI_RESULT row).</summary>
public readonly record struct PartyTaxiRow(ulong Guid, byte Reason);

/// <summary>The server's answer to a party-flight request.</summary>
public readonly record struct PartyTaxiResult(
    byte Result, ulong FlightMaster, uint Destination, PartyTaxiRow[] Rows);

/// <summary>
/// Party flight wire (owner decision 2026-09-03, Command View only). The commander picks
/// a destination on the flight-master map and the whole party they command takes that
/// flight: their own character plus every group member that is a bot for them. Members
/// that cannot board are reported; unless the request is CONFIRMED nobody flies and the
/// client asks "fly with the rest?".
///
/// Request (CMSG_SUI_PARTY_TAXI): u8 flags, u64 flightMaster, u8 nodeCount, u32 × nodes —
/// exactly 10 + 4·count bytes; the node chain is the CMSG_ACTIVATETAXIEXPRESS chain
/// (source first, destination last), 2..8 nodes.
/// Reply (SMSG_SUI_PARTY_TAXI_RESULT): u8 result, u64 flightMaster, u32 destination,
/// u8 count, count × { u64 guid, u8 reason } — exactly 14 + 9·count bytes.
/// The client must not send the request until capability bit 11 (PARTY_TAXI v1) has been
/// observed in the SMSG_SUI_CONTROL_ACK capability trailer.
/// </summary>
public static class PartyTaxiWire
{
    public const byte FlagConfirmed = 0x01;

    // Results.
    public const byte ResultFlying = 0;          // flights started; rows = members left behind
    public const byte ResultConfirmNeeded = 1;   // nobody flew; rows = members that cannot board
    public const byte ResultDenied = 2;          // bad request / flight master out of reach
    public const byte ResultNoPath = 3;          // the node chain is not a taxi route

    // Per-member reasons.
    public const byte ReasonUnknownNode = 1;
    public const byte ReasonNoMoney = 2;
    public const byte ReasonTooFar = 3;
    public const byte ReasonBusy = 4;
    public const byte ReasonInFlight = 5;
    public const byte ReasonOtherMap = 6;
    public const byte ReasonRefused = 7;

    public const int MinNodes = 2;
    public const int MaxNodes = 8;
    public const int RequestHeaderBytes = 10;    // u8 flags + u64 flight master + u8 count
    public const int ResultHeaderBytes = 14;     // u8 result + u64 flight master + u32 dest + u8 count
    public const int ResultRowBytes = 9;         // u64 guid + u8 reason

    public static int RequestBytes(int nodeCount) => RequestHeaderBytes + 4 * nodeCount;

    /// <summary>
    /// CMSG_SUI_PARTY_TAXI body. The chain must name a flight master and 2..8 distinct
    /// consecutive nodes; anything else is refused here rather than sent.
    /// </summary>
    public static byte[] BuildRequest(byte flags, ulong flightMaster, IReadOnlyList<uint> nodes)
    {
        if ((flags & ~FlagConfirmed) != 0)
            throw new ArgumentOutOfRangeException(nameof(flags), $"unknown party-taxi flags 0x{flags:X2}");
        if (flightMaster == 0)
            throw new ArgumentOutOfRangeException(nameof(flightMaster), "a party flight needs its flight master");
        if (nodes.Count is < MinNodes or > MaxNodes)
            throw new ArgumentOutOfRangeException(nameof(nodes), $"a party flight chain has {MinNodes}..{MaxNodes} nodes, got {nodes.Count}");
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] == 0)
                throw new ArgumentOutOfRangeException(nameof(nodes), "a taxi node id is never 0");
            if (i > 0 && nodes[i] == nodes[i - 1])
                throw new ArgumentOutOfRangeException(nameof(nodes), "a chain never repeats a node back to back");
        }
        var w = new PacketWriter(RequestBytes(nodes.Count));
        w.WriteU8(flags);
        w.WriteU64(flightMaster);
        w.WriteU8((byte)nodes.Count);
        foreach (uint node in nodes) w.WriteU32(node);
        return w.ToArray();
    }

    /// <summary>Exact length only — a body one byte off is a different packet.</summary>
    public static bool TryParseResult(byte[] body, out PartyTaxiResult result)
    {
        result = default;
        if (body.Length < ResultHeaderBytes) return false;
        var r = new PacketReader(body);
        byte code = r.ReadU8();
        ulong flightMaster = r.ReadU64();
        uint destination = r.ReadU32();
        int count = r.ReadU8();
        if (body.Length != ResultHeaderBytes + ResultRowBytes * count) return false;
        var rows = new PartyTaxiRow[count];
        for (int i = 0; i < count; i++)
        {
            ulong guid = r.ReadU64();
            byte reason = r.ReadU8();
            rows[i] = new PartyTaxiRow(guid, reason);
        }
        result = new PartyTaxiResult(code, flightMaster, destination, rows);
        return true;
    }

    public static string ReasonText(byte reason) => reason switch
    {
        ReasonUnknownNode => "hasn't discovered this flight path",
        ReasonNoMoney => "can't afford the fare",
        ReasonTooFar => "is too far from the flight master",
        ReasonBusy => "is busy",
        ReasonInFlight => "is already flying",
        ReasonOtherMap => "is on another map",
        ReasonRefused => "was refused by the flight master",
        _ => "can't board",
    };

    /// <summary>The refusal text for a result that flew nobody, or null when it flew.</summary>
    public static string? RefusalText(byte result) => result switch
    {
        ResultFlying => null,
        ResultConfirmNeeded => null,
        ResultDenied => "There is no taxi vendor nearby!",
        ResultNoPath => "There is no direct path to that destination!",
        _ => "UNSPECIFIED TAXI SERVER ERROR",
    };

    /// <summary>The "fly with the rest?" confirm body: one line per member that cannot board.</summary>
    public static string ConfirmText(string destination, IReadOnlyList<(string Name, byte Reason)> rows)
    {
        var text = new System.Text.StringBuilder();
        text.Append("Not everyone can fly to ").Append(destination).Append(':');
        foreach ((string name, byte reason) in rows)
            text.Append('\n').Append(name).Append(' ').Append(ReasonText(reason)).Append('.');
        text.Append("\n\nFly with the rest?");
        return text.ToString();
    }

    /// <summary>Chat line after a confirmed or fully-eligible flight started.</summary>
    public static string FlyingText(string destination, IReadOnlyList<(string Name, byte Reason)> leftBehind)
    {
        if (leftBehind.Count == 0) return $"The party takes the flight to {destination}.";
        string names = string.Join(", ", leftBehind.Select(row => row.Name));
        return $"The party takes the flight to {destination}; {names} stay behind.";
    }
}
