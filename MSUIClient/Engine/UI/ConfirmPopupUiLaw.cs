namespace MSUIClient.Engine.UI;

/// <summary>
/// The two server-asked yes/no StaticPopups that had no client half: CONFIRM_SUMMON
/// (warlock Ritual of Summoning / meeting stones — SMSG_SUMMON_REQUEST, answered by
/// CMSG_SUMMON_RESPONSE, declined by silence) and QUEST_ACCEPT (an escort/party-accept quest
/// a party member started — SMSG_QUEST_CONFIRM_ACCEPT, answered by CMSG_QUEST_CONFIRM_ACCEPT,
/// declined by silence). Both ride the shared StaticPopup coordinator like the duel request.
/// </summary>
public static class ConfirmPopupUiLaw
{
    public const string SummonPopupType = "CONFIRM_SUMMON";
    public const string QuestAcceptPopupType = "QUEST_ACCEPT";
    public const string AcceptText = "Accept";
    public const string DeclineText = "Decline";
    /// <summary>vmangos MAX_PLAYER_SUMMON_DELAY: the server auto-declines after two minutes.</summary>
    public const int SummonTimeoutSeconds = 120;
    public const int QuestAcceptTimeoutSeconds = 60;

    public static readonly StaticPopupCoordinatorLaw.Definition SummonDefinition = new(
        SummonPopupType, HideOnEscape: true, HasAccept: true, HasCancel: true,
        TimeoutSeconds: SummonTimeoutSeconds, EntrySound: "igPlayerInvite");

    public static readonly StaticPopupCoordinatorLaw.Definition QuestAcceptDefinition = new(
        QuestAcceptPopupType, HideOnEscape: true, HasAccept: true, HasCancel: true,
        TimeoutSeconds: QuestAcceptTimeoutSeconds, EntrySound: "igPlayerInvite");

    public const string ReadyCheckPopupType = "READY_CHECK";
    public const string ReadyText = "Ready";
    public const string NotReadyText = "Not Ready";
    /// <summary>The reference's READY_CHECK StaticPopup: 30 s, then it counts as not ready.</summary>
    public const int ReadyCheckTimeoutSeconds = 30;

    public static readonly StaticPopupCoordinatorLaw.Definition ReadyCheckDefinition = new(
        ReadyCheckPopupType, HideOnEscape: true, HasAccept: true, HasCancel: true,
        TimeoutSeconds: ReadyCheckTimeoutSeconds, EntrySound: "ReadyCheck");

    public static bool IsConfirmPopup(string type) =>
        type is SummonPopupType or QuestAcceptPopupType or ReadyCheckPopupType;

    public static (string Accept, string Decline) Captions(string type) =>
        type == ReadyCheckPopupType ? (ReadyText, NotReadyText) : (AcceptText, DeclineText);

    /// <summary>GlobalStrings READY_CHECK_MESSAGE with the starter filled.</summary>
    public static string ReadyCheckText(string format, string starter)
    {
        string who = string.IsNullOrWhiteSpace(starter) ? "The leader" : starter.Trim();
        return format.Replace("%s", who, StringComparison.Ordinal);
    }

    /// <summary>GlobalStrings CONFIRM_SUMMON with the summoner and the destination zone filled.</summary>
    public static string SummonText(string format, string summoner, string zone)
    {
        string who = string.IsNullOrWhiteSpace(summoner) ? "Someone" : summoner.Trim();
        string where = string.IsNullOrWhiteSpace(zone) ? "an unknown location" : zone.Trim();
        return FillTwo(format, who, where);
    }

    /// <summary>GlobalStrings QUEST_ACCEPT with the starter and the quest title filled.</summary>
    public static string QuestAcceptText(string format, string starter, string title)
    {
        string who = string.IsNullOrWhiteSpace(starter) ? "A party member" : starter.Trim();
        return FillTwo(format, who, title);
    }

    private static string FillTwo(string format, string first, string second)
    {
        int at = format.IndexOf("%s", StringComparison.Ordinal);
        if (at < 0) return $"{format} {first} {second}".Trim();
        string once = format[..at] + first + format[(at + 2)..];
        int again = once.IndexOf("%s", StringComparison.Ordinal);
        return again < 0 ? once : once[..again] + second + once[(again + 2)..];
    }

    public static (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? Visible(
        StaticPopupCoordinatorLaw.Slots slots, string type)
    {
        if (slots.First is { } first &&
            string.Equals(first.Definition.Type, type, StringComparison.Ordinal))
            return (1, first);
        if (slots.Second is { } second &&
            string.Equals(second.Definition.Type, type, StringComparison.Ordinal))
            return (2, second);
        return null;
    }
}
