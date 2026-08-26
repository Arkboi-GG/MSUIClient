namespace MSUIClient.Engine.UI;

/// <summary>
/// The SuperUI commander's party-lead verb (PLAN_20 P4a).
///
/// Deliberately NOT folded into <see cref="GroupSlashCommandLaw"/>: that law is a
/// parity surface listing vanilla's own GlobalStrings aliases, and putting a
/// SuperUI-only verb in it would make the parity tables assert something the 1.12
/// client never had. Same reason this is not a row in the unit popup — that menu
/// mirrors Benilla's UnitPopup.xml exactly.
/// </summary>
public static class PartyLeadCommandLaw
{
    /// <summary>Aliases for "take the lead back from a bot".</summary>
    public static bool IsClaimLead(string command) => command.ToLowerInvariant() switch
    {
        "/claimlead" or "/takelead" or "/leadme" => true,
        _ => false,
    };

    public const string Usage =
        "/claimlead — take group leadership from a companion bot so you can rearrange the party.";
}
