using System.Text;

namespace MSUIClient.Engine.UI;

public static class MailUiLaw
{
    public const int InboxItemsPerPage = 7;
    public const uint CheckedRead = 0x1;
    public const uint CheckedReturned = 0x2;
    public const uint CheckedCopied = 0x4;
    public const uint PostageCopper = 30;
    public const uint MaxCodCopper = 10_000u * 10_000u;
    public const float InteractionDistance = NpcSessionUiLaw.ServiceRange;
    public const double InboxThrottleSeconds = 60d;
    public const int MaxRecipientLetters = 12;
    public const int MaxSubjectLetters = 64;
    public const int MaxBodyLetters = 500;
    // f32::from_bits(0x34800000): the strict, symmetric HasNewMail threshold.
    public const float NewMailEpsilon = 2.3841858e-7f;

    public static int PageCount(int itemCount) =>
        Math.Max(1, (Math.Max(0, itemCount) + InboxItemsPerPage - 1) / InboxItemsPerPage);

    public static int ClampPage(int page, int itemCount) =>
        Math.Clamp(page, 1, PageCount(itemCount));

    public static int FirstIndex(int page, int itemCount) =>
        (ClampPage(page, itemCount) - 1) * InboxItemsPerPage;

    public static string ExpiryText(float days) => days >= 1f
        ? $"{MathF.Floor(days):0} {(MathF.Floor(days) == 1f ? "Day" : "Days")}"
        : "< 1 day";

    public static bool IsRead(uint checkedFlags) => (checkedFlags & CheckedRead) != 0;
    public static bool IsReturned(uint checkedFlags) => (checkedFlags & CheckedReturned) != 0;
    public static bool IsCopied(uint checkedFlags) => (checkedFlags & CheckedCopied) != 0;

    public static bool CanReply(byte messageType, uint checkedFlags, bool senderResolved) =>
        messageType == 0 && !IsReturned(checkedFlags) && senderResolved;

    // This is the observable 1.12 return/delete split used by the reference snapshot: a not-yet-
    // returned player mail that still carries an attachment returns; all other mail deletes.
    public static bool CanDelete(byte messageType, uint checkedFlags, bool hasItem, uint money) =>
        IsReturned(checkedFlags) || messageType != 0 || (!hasItem && money == 0);

    public static bool CanSend(string recipient, string subject, bool codMode,
        uint amount, bool hasAttachment, bool pending) =>
        !pending && recipient.Length > 0 && subject.Length > 0 &&
        (!codMode || (hasAttachment && amount <= MaxCodCopper));

    public static bool HasNewMail(float seconds) =>
        float.IsFinite(seconds) && MathF.Abs(seconds) < NewMailEpsilon;

    public static float StepNewMailCountdown(float countdown, float deltaSeconds)
    {
        if (!(countdown > 0f)) return countdown;
        double difference = (double)countdown - deltaSeconds;
        return difference > 0d ? (float)difference : 0f;
    }

    public static float ApplyReceivedMailCountdown(float current, float delay, bool mailboxOpen)
    {
        if (mailboxOpen) return current;
        if (double.IsFinite(delay) && Math.Abs((double)delay) < NewMailEpsilon) return delay;
        return current < 0f || delay < current ? delay : current;
    }

    public static string TruncateLetters(string value, int maximumLetters)
    {
        if (maximumLetters <= 0 || string.IsNullOrEmpty(value)) return "";
        var result = new StringBuilder(Math.Min(value.Length, maximumLetters));
        int count = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (count++ >= maximumLetters) break;
            result.Append(rune);
        }
        return result.ToString();
    }

    public readonly record struct MoneyDenomination(int Icon, uint Value);

    // Visual order is highest-to-lowest; zero denominations collapse. Copper remains for total 0.
    public static IReadOnlyList<MoneyDenomination> Money(uint copper)
    {
        uint gold = copper / 10_000;
        uint silver = copper / 100 % 100;
        uint coin = copper % 100;
        var result = new List<MoneyDenomination>(3);
        if (gold > 0) result.Add(new(0, gold));
        if (silver > 0) result.Add(new(1, silver));
        if (coin > 0 || result.Count == 0) result.Add(new(2, coin));
        return result;
    }

    public static string ErrorText(uint error) => error switch
    {
        1 => "Inventory is full.",
        2 => "You can't send mail to yourself.",
        3 => "You don't have enough money.",
        4 => "Cannot find mail recipient.",
        5 => "That player is not part of your alliance.",
        6 => "Internal mail database error.",
        14 => "Trial accounts cannot perform that action.",
        15 => "You have reached the in-game cap of unique mail recipients",
        _ => $"Mail failed ({error})."
    };
}
