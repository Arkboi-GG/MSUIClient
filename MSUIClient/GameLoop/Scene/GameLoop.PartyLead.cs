using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    /// <summary>
    /// Party lead claim (PLAN_20 P4a). The fleet's own grouping leaves an AiBot
    /// holding the lead, and vanilla gives a non-leader no way to take it back —
    /// so the commander cannot rearrange or break up their own party. This is the
    /// way back, and it only ever takes the lead from a bot.
    /// </summary>
    private bool _partyLeadAvailable;

    private void ApplyPartyLeadCapability(uint capabilities)
    {
        bool available = (capabilities & SuiCapabilityWire.PartyLeadV1) != 0;
        if (available != _partyLeadAvailable)
            Console.WriteLine(available
                ? "[party-lead] server advertised party-lead-v1"
                : "[party-lead] server has no party-lead-v1 advertisement");
        _partyLeadAvailable = available;
    }

    private void ResetPartyLead() => _partyLeadAvailable = false;

    /// <summary>
    /// Ask the server for group leadership. Answered per act with a reason, so a
    /// refusal can say which rule refused it rather than failing silently.
    /// </summary>
    private bool RequestPartyLeadClaim()
    {
        if (_net is not { IsInWorld: true }) return false;
        if (!_partyLeadAvailable)
        {
            ShowUiError("This server has no party-lead support.");
            return false;
        }
        if (LocalPlayerGuid == 0) return false;
        if (_partyMembers.Count == 0)
        {
            ShowUiError("You are not in a group.");
            return false;
        }

        bool sent = _net.SuiPartyLead(PartyLeadWire.ActionClaim, LocalPlayerGuid);
        EmitInterface("party-lead", "claim", sent ? "SENT" : "REFUSED", LocalPlayerGuid, "");
        return sent;
    }

    private void ApplySuiPartyLeadResult(byte[] body)
    {
        if (!PartyLeadWire.TryParsePartyLeadResult(body, out PartyLeadResult result))
        {
            EmitInterface("party-lead", "result", "MALFORMED", 0, $"bytes={body.Length}");
            return;
        }

        string who = ResolveUnitName(result.Subject);
        if (result.Result == PartyLeadWire.ResultOk)
        {
            AddChatMessage($"{who} is now the party leader.");
            Console.WriteLine($"[party-lead] claim granted for 0x{result.Subject:X}");
        }
        else
        {
            // Name the rule. "Failed" with no reason is what sends someone
            // hunting through logs for a refusal the server could have explained.
            ShowUiError($"Could not take the lead: {PartyLeadWire.ResultName(result.Result)}.");
            Console.WriteLine($"[party-lead] claim refused ({result.Result}) " +
                $"for 0x{result.Subject:X}");
        }

        EmitInterface("party-lead", "result",
            result.Result == PartyLeadWire.ResultOk ? "OK" : "REFUSED",
            result.Subject, $"action={result.Action};result={result.Result}");
    }
}
