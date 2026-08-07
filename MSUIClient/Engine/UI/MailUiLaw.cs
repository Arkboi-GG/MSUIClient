namespace MSUIClient.Engine.UI;

public static class MailUiLaw
{
    public const int InboxItemsPerPage = 7;
    public const uint CheckedRead = 0x1;
    public const uint CheckedReturned = 0x2;
    public const uint CheckedCopied = 0x4;
    public const uint PostageCopper = 30;
    public const uint MaxCodCopper = 10_000u * 10_000u;
    public const float InteractionDistance = 5f;
    public const double InboxThrottleSeconds = 60d;

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
        !pending && recipient.Trim().Length > 0 && subject.Length > 0 &&
        (!codMode || (hasAttachment && amount <= MaxCodCopper));

    public static bool HasNewMail(float seconds) => float.IsFinite(seconds) && seconds >= 0f && seconds <= .01f;

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
