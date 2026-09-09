using System.Numerics;

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

    /// <summary>Command View party flight (owner 2026-09-03): the server reported members who
    /// cannot board this flight — fly with the rest, or cancel and keep the map open.</summary>
    public const string PartyFlightPopupType = "SUI_PARTY_FLIGHT";
    public const string FlyText = "Fly";
    public const int PartyFlightTimeoutSeconds = 30;

    public static readonly StaticPopupCoordinatorLaw.Definition PartyFlightDefinition = new(
        PartyFlightPopupType, HideOnEscape: true, HasAccept: true, HasCancel: true,
        TimeoutSeconds: PartyFlightTimeoutSeconds, EntrySound: "igPlayerInvite");

    /// <summary>Command View: a right-clicked NPC that offers MORE THAN ONE thing — a quest giver
    /// who is also a flight master / vendor / trainer / banker / innkeeper, a vendor who also
    /// trains... The lowest-bit-wins cursor ladder would silently take one of them and the rest
    /// could never be reached from the sky (owner, 2026-09-02: Thor's flight path; 2026-09-03:
    /// "apply the flight master fix to any NPC that has more than one option, or also has
    /// quests, so that we don't get stuck"). A small chooser lists every option; nothing walks
    /// or opens until one is picked. Every option button is the popup's ACCEPT (the coordinator
    /// knows two buttons; the picked index is recorded before the click). No timeout.</summary>
    public const string GiverChoicePopupType = "CV_GIVER_CHOICE";
    public const string GiverChoiceQuestsText = "Quests";
    public const float GiverChoiceButtonRowGap = 4;

    public static readonly StaticPopupCoordinatorLaw.Definition GiverChoiceDefinition = new(
        GiverChoicePopupType, HideOnEscape: true, HasAccept: true, HasCancel: true);

    /// <summary>Where a chooser option leads. CommanderQuests is the party quest window (Model
    /// B); Gossip is the stock greeting, through which the trainer list, the inn, the spirit
    /// healer, petitions, tabards, battlegrounds and stables all open.</summary>
    public enum NpcServiceRoute { CommanderQuests, Gossip, Vendor, Taxi, Bank, Auction }

    public readonly record struct NpcOption(string Caption, NpcServiceRoute Route);

    /// <summary>Every distinct thing the NPC offers, in the cursor ladder's order, ONE entry per
    /// route: the services that all open through the gossip greeting collapse into a single
    /// entry captioned after the first of them, so no two buttons do the same thing.
    /// <paramref name="commanderQuests"/>: the quest entry is the commander quest window (not
    /// driving anyone, server advertises it); otherwise it is the stock quest greeting. The
    /// gossip bit alone never makes an entry — it is the greeting, not a service — and it only
    /// adds "Talk" when a chooser is raised anyway, so nothing said in it becomes unreachable.
    /// Fewer than two entries = no chooser; the click routes as before.</summary>
    public static List<NpcOption> NpcOptions(uint npcFlags, bool commanderQuests)
    {
        var options = new List<NpcOption>();
        void Add(string caption, NpcServiceRoute route)
        {
            foreach (NpcOption existing in options)
                if (existing.Route == route) return;
            options.Add(new(caption, route));
        }
        if ((npcFlags & WorldCursorUiLaw.Questgiver) != 0)
            Add(GiverChoiceQuestsText,
                commanderQuests ? NpcServiceRoute.CommanderQuests : NpcServiceRoute.Gossip);
        if ((npcFlags & WorldCursorUiLaw.Vendor) != 0) Add("Browse Goods", NpcServiceRoute.Vendor);
        if ((npcFlags & WorldCursorUiLaw.FlightMaster) != 0) Add("Flight Map", NpcServiceRoute.Taxi);
        if ((npcFlags & WorldCursorUiLaw.Trainer) != 0) Add("Training", NpcServiceRoute.Gossip);
        if ((npcFlags & (WorldCursorUiLaw.SpiritHealer | WorldCursorUiLaw.SpiritGuide)) != 0)
            Add("Talk", NpcServiceRoute.Gossip);
        if ((npcFlags & WorldCursorUiLaw.Innkeeper) != 0) Add("Innkeeper", NpcServiceRoute.Gossip);
        if ((npcFlags & WorldCursorUiLaw.Banker) != 0) Add("Bank", NpcServiceRoute.Bank);
        if ((npcFlags & (WorldCursorUiLaw.Petitioner | WorldCursorUiLaw.TabardDesigner |
                         WorldCursorUiLaw.Battlemaster)) != 0)
            Add("Talk", NpcServiceRoute.Gossip);
        if ((npcFlags & WorldCursorUiLaw.Auctioneer) != 0) Add("Auction House", NpcServiceRoute.Auction);
        if ((npcFlags & WorldCursorUiLaw.StableMaster) != 0) Add("Stable", NpcServiceRoute.Gossip);
        if (options.Count >= 2 && (npcFlags & WorldCursorUiLaw.Gossip) != 0)
            Add("Talk", NpcServiceRoute.Gossip);
        return options;
    }

    public static bool NeedsChooser(IReadOnlyList<NpcOption> options) => options.Count >= 2;

    /// <summary>"Thor: quests, flight map, or talk?"</summary>
    public static string GiverChoiceText(string npcName, IReadOnlyList<NpcOption> options)
    {
        string who = string.IsNullOrWhiteSpace(npcName) ? "This NPC" : npcName.Trim();
        var lower = new List<string>(options.Count);
        foreach (NpcOption option in options) lower.Add(option.Caption.ToLowerInvariant());
        string list = lower.Count switch
        {
            0 => "what",
            1 => lower[0],
            2 => $"{lower[0]} or {lower[1]}",
            _ => string.Join(", ", lower.GetRange(0, lower.Count - 1)) + ", or " + lower[^1],
        };
        return $"{who}: {list}?";
    }

    /// <summary>Option buttons fill the popup's two button columns, row by row.</summary>
    public static Vector2 GiverChoiceButtonMin(int index, float textHeight) =>
        new(index % 2 == 0 ? DuelFrameUiLaw.PopupButtonOneX : DuelFrameUiLaw.PopupButtonTwoX,
            DuelFrameUiLaw.PopupButtonTop(textHeight) +
            index / 2 * (DuelFrameUiLaw.PopupButtonHeight + GiverChoiceButtonRowGap));

    public static float GiverChoiceButtonsHeight(int optionCount)
    {
        int rows = Math.Max(1, (optionCount + 1) / 2);
        return rows * DuelFrameUiLaw.PopupButtonHeight + (rows - 1) * GiverChoiceButtonRowGap;
    }

    public static Vector2 GiverChoicePopupSize(float textHeight, int optionCount) =>
        new(DuelFrameUiLaw.PopupWidth,
            StaticPopupCoordinatorLaw.Height(textHeight, GiverChoiceButtonsHeight(optionCount)));

    /// <summary>The control guide's Disable button asks here (a stock yes/no StaticPopup, not an
    /// ImGui modal - owner 2026-09-03). Accept turns the guide off for the session.</summary>
    public const string DisableControlGuidePopupType = "CV_DISABLE_CONTROL_GUIDE";
    public const string DisableControlGuideText =
        "Disable the Control Guide completely?\nYou will not see it again this session unless re-enabled.";
    public const string DisableText = "Disable";
    public const string CancelText = "Cancel";

    public static readonly StaticPopupCoordinatorLaw.Definition DisableControlGuideDefinition = new(
        DisableControlGuidePopupType, HideOnEscape: true, HasAccept: true, HasCancel: true);

    /// <summary>The Macro Book's Delete asks first (owner 2026-09-05): a stock yes/no
    /// StaticPopup whose data token is the macro's name. Sections are not asked about - deleting
    /// one only ungroups its macros.</summary>
    public const string DeleteMacroPopupType = "SUI_DELETE_MACRO";
    public const string DeleteText = "Delete";

    public static string DeleteMacroText(string name) =>
        $"Delete the macro \"{(string.IsNullOrWhiteSpace(name) ? "New Macro" : name.Trim())}\"?\n" +
        "Any hotbar button using it will go empty.";

    public static readonly StaticPopupCoordinatorLaw.Definition DeleteMacroDefinition = new(
        DeleteMacroPopupType, HideOnEscape: true, HasAccept: true, HasCancel: true);

    public static bool IsConfirmPopup(string type) =>
        type is SummonPopupType or QuestAcceptPopupType or ReadyCheckPopupType or GiverChoicePopupType
            or DisableControlGuidePopupType or DeleteMacroPopupType;

    public static (string Accept, string Decline) Captions(string type) =>
        type == ReadyCheckPopupType ? (ReadyText, NotReadyText)
        : type == DisableControlGuidePopupType ? (DisableText, CancelText)
        : type == DeleteMacroPopupType ? (DeleteText, CancelText)
        : type == PartyFlightPopupType ? (FlyText, CancelText)
        : (AcceptText, DeclineText);

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
