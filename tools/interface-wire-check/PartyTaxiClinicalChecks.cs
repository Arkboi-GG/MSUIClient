using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

/// <summary>
/// Party flight (PARTY_TAXI v1 — the Command View flies the whole commanded party
/// from a flight master, asking first when someone cannot board) and the direct-
/// control half (a possessed bot's taxi ride drives the controller). Verifies the
/// wire law (request layout and refusals, exact-length result parse, reason and
/// confirm texts), the opcode and capability constants, and the client wiring
/// nobody would notice was gone (capability apply, dispatch, the Command View
/// route in ActivateTaxi, the proxied taxi reply, the ride-ownership law while
/// possessing, the in-flight control hand-over).
/// </summary>
internal static class PartyTaxiClinicalChecks
{
    public static void Run()
    {
        // ── Request: u8 flags, u64 flight master, u8 count, u32 × nodes ─────
        byte[] direct = PartyTaxiWire.BuildRequest(0, 0x1122334455667788UL, [2u, 6u]);
        Check(direct.Length == PartyTaxiWire.RequestBytes(2) && direct.Length == 18 &&
              direct[0] == 0 && BitConverter.ToUInt64(direct, 1) == 0x1122334455667788UL &&
              direct[9] == 2 && BitConverter.ToUInt32(direct, 10) == 2 &&
              BitConverter.ToUInt32(direct, 14) == 6,
            "party-taxi request layout drift (flags/raw guid/count/nodes)");
        byte[] confirmed = PartyTaxiWire.BuildRequest(PartyTaxiWire.FlagConfirmed, 1, [2u, 6u, 9u]);
        Check(confirmed.Length == 22 && confirmed[0] == PartyTaxiWire.FlagConfirmed && confirmed[9] == 3,
            "a confirmed three-hop request is 22 bytes with flag 0x01");
        ExpectRefused(() => PartyTaxiWire.BuildRequest(0, 0, [2u, 6u]), "a request with no flight master was accepted");
        ExpectRefused(() => PartyTaxiWire.BuildRequest(0, 1, [2u]), "a one-node chain was accepted");
        ExpectRefused(() => PartyTaxiWire.BuildRequest(0, 1, [1u, 2u, 3u, 4u, 5u, 6u, 7u, 8u, 9u]), "a nine-node chain was accepted");
        ExpectRefused(() => PartyTaxiWire.BuildRequest(0, 1, [2u, 2u]), "a chain repeating a node was accepted");
        ExpectRefused(() => PartyTaxiWire.BuildRequest(0, 1, [0u, 2u]), "a zero node was accepted");
        ExpectRefused(() => PartyTaxiWire.BuildRequest(0x80, 1, [2u, 6u]), "an unknown flag was accepted");

        // ── Result: u8 result, u64 fm, u32 dest, u8 count, rows — exact ──────
        var w = new PacketWriter();
        w.WriteU8(PartyTaxiWire.ResultConfirmNeeded);
        w.WriteU64(0xABCDEF01UL);
        w.WriteU32(6);
        w.WriteU8(2);
        w.WriteU64(0x11UL); w.WriteU8(PartyTaxiWire.ReasonUnknownNode);
        w.WriteU64(0x22UL); w.WriteU8(PartyTaxiWire.ReasonNoMoney);
        byte[] answer = w.ToArray();
        Check(answer.Length == PartyTaxiWire.ResultHeaderBytes + 2 * PartyTaxiWire.ResultRowBytes &&
              answer.Length == 32,
            "party-taxi result fixture is not the 32-byte body — the constants drifted");
        Check(PartyTaxiWire.TryParseResult(answer, out PartyTaxiResult parsed) &&
              parsed.Result == PartyTaxiWire.ResultConfirmNeeded &&
              parsed.FlightMaster == 0xABCDEF01UL && parsed.Destination == 6 &&
              parsed.Rows.Length == 2 && parsed.Rows[0].Guid == 0x11UL &&
              parsed.Rows[0].Reason == PartyTaxiWire.ReasonUnknownNode &&
              parsed.Rows[1].Reason == PartyTaxiWire.ReasonNoMoney,
            "party-taxi result parse drift");
        Check(!PartyTaxiWire.TryParseResult(answer[..^1], out _) &&
              !PartyTaxiWire.TryParseResult([.. answer, 0], out _) &&
              !PartyTaxiWire.TryParseResult([], out _),
            "a party-taxi result off by one byte was accepted");
        var empty = new PacketWriter();
        empty.WriteU8(PartyTaxiWire.ResultFlying); empty.WriteU64(1); empty.WriteU32(6); empty.WriteU8(0);
        Check(PartyTaxiWire.TryParseResult(empty.ToArray(), out PartyTaxiResult flew) &&
              flew.Result == PartyTaxiWire.ResultFlying && flew.Rows.Length == 0,
            "an all-aboard FLYING result did not parse");

        // ── Texts ────────────────────────────────────────────────────────────
        Check(PartyTaxiWire.RefusalText(PartyTaxiWire.ResultFlying) is null &&
              PartyTaxiWire.RefusalText(PartyTaxiWire.ResultConfirmNeeded) is null &&
              PartyTaxiWire.RefusalText(PartyTaxiWire.ResultDenied) == "There is no taxi vendor nearby!" &&
              PartyTaxiWire.RefusalText(PartyTaxiWire.ResultNoPath) == "There is no direct path to that destination!",
            "party-taxi refusal texts drift from the stock taxi errors");
        string confirmText = PartyTaxiWire.ConfirmText("Ironforge", [("Zuggina", PartyTaxiWire.ReasonUnknownNode), ("Bob", PartyTaxiWire.ReasonTooFar)]);
        Check(confirmText.StartsWith("Not everyone can fly to Ironforge:", StringComparison.Ordinal) &&
              confirmText.Contains("Zuggina hasn't discovered this flight path.", StringComparison.Ordinal) &&
              confirmText.Contains("Bob is too far from the flight master.", StringComparison.Ordinal) &&
              confirmText.EndsWith("Fly with the rest?", StringComparison.Ordinal),
            "party-taxi confirm text drift");
        Check(PartyTaxiWire.FlyingText("Ironforge", []) == "The party takes the flight to Ironforge." &&
              PartyTaxiWire.FlyingText("Ironforge", [("Bob", PartyTaxiWire.ReasonTooFar)]) ==
                  "The party takes the flight to Ironforge; Bob stay behind.",
            "party-taxi flying text drift");
        for (byte reason = 1; reason <= 7; reason++)
            Check(PartyTaxiWire.ReasonText(reason) != PartyTaxiWire.ReasonText(0), $"reason {reason} has no dedicated text");

        // ── Constants ────────────────────────────────────────────────────────
        Check((ushort)Op.CMSG_SUI_PARTY_TAXI == 0x0364 && (ushort)Op.SMSG_SUI_PARTY_TAXI_RESULT == 0x0365,
            "party-taxi opcodes are 868/869 (0x0364/0x0365) on the box");
        Check(SuiCapabilityWire.PartyTaxiV1 == 1u << 11, "PARTY_TAXI v1 is capability bit 11");
        Check(ConfirmPopupUiLaw.PartyFlightPopupType == "SUI_PARTY_FLIGHT" &&
              ConfirmPopupUiLaw.PartyFlightDefinition.HasAccept &&
              ConfirmPopupUiLaw.PartyFlightDefinition.HasCancel &&
              ConfirmPopupUiLaw.PartyFlightDefinition.HideOnEscape &&
              ConfirmPopupUiLaw.Captions(ConfirmPopupUiLaw.PartyFlightPopupType) ==
                  (ConfirmPopupUiLaw.FlyText, ConfirmPopupUiLaw.CancelText),
            "party-flight popup definition drift");

        // ── Ride ownership: a possessed bot's flight drives the controller ───
        Check(ServerRideOwnershipLaw.MayOwnController(false, false, false, possessingEmbodiedBot: true) &&
              !ServerRideOwnershipLaw.MayOwnController(true, false, false, possessingEmbodiedBot: true) &&
              !ServerRideOwnershipLaw.MayOwnController(false, false, false, possessingEmbodiedBot: false) &&
              ServerRideOwnershipLaw.MayOwnController(false, true, true),
            "server-ride ownership while possessing drift");

        // ── Wiring ───────────────────────────────────────────────────────────
        string root = ClientConfig.FindRepoRoot();
        string taxi = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels", "GameLoop.Taxi.cs"));
        string control = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene", "GameLoop.Control.cs"));
        string net = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene", "GameLoop.Net.cs"));
        string portals = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene", "GameLoop.RealPortals.cs"));
        string session = SourceText.Read(Path.Combine(root, "MSUIClient", "Net", "WorldSession.cs"));
        string confirms = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels", "GameLoop.Confirms.cs"));
        string pet = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels", "GameLoop.Pet.cs"));
        Check(taxi.Contains("TryGetInteractionBodyPose(out WorldBodyPose sessionBody)", StringComparison.Ordinal) &&
              !taxi.Contains("TryGetSessionBodyPose(", StringComparison.Ordinal),
            "the taxi map must gate at the interaction body (the driven bot while possessing)");
        Check(taxi.Contains("move.Guid != ControlledGuid", StringComparison.Ordinal) &&
              taxi.Contains("possessingEmbodiedBot: _controlState == ControlState.Possessing", StringComparison.Ordinal),
            "a possessed bot's SMSG_MONSTER_MOVE flight must own the controller");
        Check(taxi.Contains("RequestPartyTaxi(", StringComparison.Ordinal) &&
              taxi.Contains("_freeView && _partyTaxiAvailable", StringComparison.Ordinal) &&
              taxi.Contains("private void ApplyPartyTaxiResult(byte[] body)", StringComparison.Ordinal),
            "ActivateTaxi must route to the party flight from the Command View");
        Check(control.Contains("case Op.SMSG_ACTIVATETAXIREPLY:", StringComparison.Ordinal) &&
              control.Contains("ApplyTaxiReply(inner);", StringComparison.Ordinal),
            "the possessed bot's taxi reply must unwrap through ApplySuiProxy");
        Check(net.Contains("case Op.SMSG_SUI_PARTY_TAXI_RESULT:", StringComparison.Ordinal) &&
              net.Contains("ApplyPartyTaxiResult(body);", StringComparison.Ordinal),
            "SMSG_SUI_PARTY_TAXI_RESULT is not dispatched");
        Check(portals.Contains("ApplyPartyTaxiCapability(capabilities);", StringComparison.Ordinal),
            "the party-taxi capability bit is never applied from the control ACK");
        Check(session.Contains("Op.CMSG_SUI_PARTY_TAXI, PartyTaxiWire.BuildRequest(", StringComparison.Ordinal),
            "the party-taxi request does not send through the wire law");
        Check(confirms.Contains("ConfirmPopupUiLaw.PartyFlightPopupType", StringComparison.Ordinal) &&
              confirms.Contains("DrawConfirmPopup(ConfirmPopupUiLaw.PartyFlightPopupType)", StringComparison.Ordinal),
            "the party-flight confirm popup is not drawn or answered");
        Check(pet.Contains("DiscardServerRideWithoutAck();", StringComparison.Ordinal) &&
              pet.Contains("CloseTaxiMap(playSound: false);", StringComparison.Ordinal),
            "a control change must drop the previous body's ride and taxi map");

        Console.WriteLine("interface-wire-check: PartyTaxi PASS");
    }

    private static void ExpectRefused(Action act, string message)
    {
        try { act(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new InvalidDataException(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
