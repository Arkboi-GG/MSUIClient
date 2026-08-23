using System.Text;
using System.Numerics;

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
    // The client-side MSG_QUERY_NEXT_MAIL_TIME sender stamps this before the reply arrives.
    // It is distinct from vmangos's -86400 "no unread mail" reply even though both read false.
    public const float NoMailQueryStamp = -1f;
    public const float ConfirmationWidth = 360f;
    public const float ConfirmationHeight = 96f;
    public const float ConfirmationTop = 128f;
    public const float OpenMailAnchorX = -10f;
    public static readonly Vector2 OpenMailOffset = new(374f, 0f);
    public const string ActionNormalFont = "GameFontNormalSmall";
    public const string ActionHighlightFont = "GameFontHighlightSmall";
    public const string ActionDisabledFont = "GameFontDisableSmall";

    public readonly record struct LogicalRect(float X, float Y, float Width, float Height)
    {
        public Vector2 Min => new(X, Y);
        public Vector2 Size => new(Width, Height);
        public Vector2 Center => new(X + Width * .5f, Y + Height * .5f);
        public Vector2 ScaledMin(Vector2 origin, float scale) => origin + Min * scale;
        public Vector2 ScaledSize(float scale) => Size * scale;
    }

    public readonly record struct TextureSlice(LogicalRect Rect, Vector2 UvMin, Vector2 UvMax);
    public readonly record struct TooltipSeat(Vector2 Anchor, Vector2 Pivot);

    // MailFrame's inbox item/expiry, compose attachment, and open-mail attachment controls all
    // use ANCHOR_RIGHT: GameTooltip BOTTOMLEFT to the owner's TOPRIGHT.
    public static TooltipSeat RightTooltipSeat(Vector2 ownerMin, Vector2 ownerSize) =>
        new(ownerMin + new Vector2(ownerSize.X, 0), new Vector2(0, 1));

    // MailFrame.xml main frame, inbox, and compose form. These are immutable authored seats;
    // renderer code may measure text and host hit targets, but it does not invent placement.
    public static readonly LogicalRect Frame = new(0, 0, 384, 512);
    public static readonly LogicalRect[] ShellPieces =
    [
        new(0, 0, 256, 256),
        new(256, 0, 128, 256),
        new(0, 256, 256, 256),
        new(256, 256, 128, 256)
    ];
    public static readonly Vector2 ComposeShellOffset = new(2, 1);
    public static readonly LogicalRect Portrait = new(10, 8, 58, 58);
    public static readonly LogicalRect Close = new(323, 9, 32, 32);
    public static readonly Vector2 TitleCenter = new(198, 26);
    public static readonly Vector2 FirstTabMin = new(24, 436);
    public const float TabHeight = 32f;
    public const float TabOverlap = 8f;

    public static LogicalRect FirstTab(float width) =>
        new(FirstTabMin.X, FirstTabMin.Y, width, TabHeight);

    public static LogicalRect SecondTab(float firstWidth, float width) =>
        new(FirstTabMin.X + firstWidth - TabOverlap, FirstTabMin.Y, width, TabHeight);

    public static readonly Vector2 InboxFirstRowMin = new(28, 80);
    public const float InboxRowPitch = 45f;
    public static readonly Vector2 InboxRowSize = new(305, 45);
    public static readonly TextureSlice InboxBorderLeft = new(
        new(0, 0, 42, 48), Vector2.Zero, new(.1640625f, .75f));
    public static readonly TextureSlice InboxBorderRight = new(
        new(42, 0, 263, 48), new(.1640625f, 0), new(1, .75f));
    public static readonly LogicalRect InboxDivider = new(-8, 43, 322, 2);
    public static readonly LogicalRect InboxSenderBox = new(47, 4, 200, 16);
    public static readonly LogicalRect InboxSubjectBox = new(47, 20, 248, 18);
    public static readonly LogicalRect InboxExpiryBox = new(201, 4, 100, 16);
    public static readonly LogicalRect InboxItemButton = new(4, 3, 37, 37);
    public static readonly LogicalRect InboxItemRing = new(-9.5f, -10.5f, 64, 64);
    public static readonly Vector2 InboxCodCenter = new(18.5f, 30);
    public static readonly LogicalRect InboxPreviousPage = new(34, 392, 32, 32);
    public static readonly LogicalRect InboxNextPage = new(298, 392, 32, 32);
    public static readonly Vector2 PreviousPageLabel = new(32, 9);
    public static readonly Vector2 NextPageLabel = new(0, 9);

    public static LogicalRect InboxRow(int zeroBasedIndex) =>
        new(InboxFirstRowMin.X, InboxFirstRowMin.Y + Math.Max(0, zeroBasedIndex) * InboxRowPitch,
            InboxRowSize.X, InboxRowSize.Y);

    public static readonly LogicalRect StationeryFrame = new(21, 97, 296, 257);
    public static readonly LogicalRect StationeryLeft = new(0, 0, 252, 256);
    public static readonly LogicalRect StationeryRight = new(252, 0, 64, 256);
    public static readonly LogicalRect HorizontalBarLeft = new(15, 350, 256, 16);
    public static readonly TextureSlice HorizontalBarLeftSlice = new(
        new(0, 0, 256, 16), Vector2.Zero, new(1, .25f));
    public static readonly TextureSlice HorizontalBarRightSlice = new(
        new(256, 0, 75, 16), new(0, .25f), new(.29296875f, .5f));
    public static readonly TextureSlice ScrollTrackTop = new(
        new(-2, -5, 31, 256), Vector2.Zero, new(.484375f, 1));
    public static readonly Vector2 ScrollControlUvMin = new(.25f, .25f);
    public static readonly Vector2 ScrollControlUvMax = new(.75f, .75f);

    public static TextureSlice ScrollTrackBottom(float height) => new(
        new(-2, height - 104, 31, 106), new(.515625f, 0), new(1, .4140625f));

    public static LogicalRect ScrollUp(float height) => new(6, 0, 16, 16);
    public static LogicalRect ScrollDown(float height) => new(6, height - 16, 16, 16);
    public static LogicalRect ScrollKnob(float height) => new(6, 16, 16, 16);

    public static readonly Vector2 ComposeToLabelRight = new(93, 50);
    public static readonly LogicalRect ComposeRecipient = new(105, 46, 122, 20);
    public static readonly Vector2 ComposeSubjectLabelRight = new(93, 73);
    public static readonly LogicalRect ComposeSubject = new(105, 69, 237, 20);
    public static readonly LogicalRect ComposeBody = new(41, 107, 270, 200);
    public static readonly Vector2 ComposeCostLabelRight = new(269, 50);
    public static readonly Vector2 ComposeCostRight = new(338, 48);
    public static readonly LogicalRect ComposeAttachment = new(30, 368, 37, 37);
    public static readonly Vector2 ComposeMoneyLabel = new(79, 373);
    public static readonly Vector2 ComposeMoneyInputs = new(82, 386);
    public static readonly LogicalRect ComposeSendMoneyRadio = new(252, 362, 16, 16);
    public static readonly LogicalRect ComposeCodRadio = new(252, 379, 16, 16);
    public static readonly Vector2 ComposeCodError = new(79, 358);
    public static readonly Vector2 ComposePurseRight = new(183, 415);
    public static readonly LogicalRect ComposeSend = new(185, 410, 80, 22);
    public static readonly LogicalRect ComposeCancel = new(265, 410, 80, 22);
    public static readonly Vector2 ComposeErrorCenter = new(192, 445);

    public static readonly LogicalRect AttachmentBackground = new(-2, -2, 39, 39);
    public static readonly Vector2 AttachmentBackgroundUvMax = new(.640625f, .640625f);
    public static readonly Vector2 AttachmentCountRight = new(32, 25);
    public static readonly LogicalRect MoneyGoldInput = new(0, 0, 58, 20);
    public static readonly Vector2 MoneyGoldCoin = new(60, 4);
    public static readonly LogicalRect MoneySilverInput = new(84, 0, 30, 20);
    public static readonly Vector2 MoneySilverCoin = new(106, 4);
    public static readonly LogicalRect MoneyCopperInput = new(130, 0, 30, 20);
    public static readonly Vector2 MoneyCopperCoin = new(152, 4);
    public static readonly Vector2 RadioSize = new(16, 16);
    public static readonly Vector2 RadioLabel = new(18, 2);
    public static readonly Vector2 RadioUncheckedUvMin = Vector2.Zero;
    public static readonly Vector2 RadioUncheckedUvMax = new(.25f, 1);
    public static readonly Vector2 RadioCheckedUvMin = new(.25f, 0);
    public static readonly Vector2 RadioCheckedUvMax = new(.5f, 1);
    public static readonly Vector2 RadioHighlightUvMin = new(.5f, 0);
    public static readonly Vector2 RadioHighlightUvMax = new(.75f, 1);
    public static readonly Vector2 CoinSize = new(13, 13);
    public const float MoneyCoinGap = 4f;

    public static Vector2 At(Vector2 origin, Vector2 logicalPoint, float scale) =>
        origin + logicalPoint * scale;

    public static Vector2 ScreenPoint(float x, float y) => new(x, y);

    public static Vector4 Clip(Vector2 min, Vector2 size) =>
        new(min.X, min.Y, min.X + size.X, min.Y + size.Y);

    public static Vector2 TextLine(Vector2 min, float pitch, int line) =>
        new(min.X, min.Y + Math.Max(0, line) * pitch);

    public static Vector2 CodErrorCoin(Vector2 errorMin, float measuredWidth, float scale) =>
        new(errorMin.X + measuredWidth + scale, errorMin.Y - scale);

    public static Vector2 CoinUvMin(int icon) => new(Math.Max(0, icon) * .25f, 0);
    public static Vector2 CoinUvMax(int icon) => new((Math.Max(0, icon) + 1) * .25f, 1);

    public static LogicalRect Relative(LogicalRect parent, LogicalRect child) =>
        new(parent.X + child.X, parent.Y + child.Y, child.Width, child.Height);

    // MailFrame.xml OpenMailFrame: every player-facing seat is expressed in logical
    // coordinates here; ImGui only hosts the resulting scaled rectangles.
    public static readonly LogicalRect OpenMailFrame = new(0, 0, 384, 512);
    public static readonly LogicalRect OpenMailIcon = new(9, 6, 60, 60);
    public static readonly Vector2 OpenMailTitleCenter = new(198, 24);
    public static readonly LogicalRect OpenMailSenderLabelBox = new(114, 45, 0, 16);
    public static readonly LogicalRect OpenMailSenderBox = new(119, 47, 110, 12);
    public static readonly LogicalRect OpenMailSubjectLabelBox = new(114, 65, 0, 16);
    public static readonly LogicalRect OpenMailSubjectBox = new(119, 69, 225, 16);
    public static readonly LogicalRect OpenMailHorizontalBar = new(15, 350, 331, 16);
    public static readonly LogicalRect OpenMailScrollFrame = new(21, 97, 296, 257);
    public static readonly Vector2 OpenMailScrollRight = new(317, 97);
    public static readonly LogicalRect OpenMailBody = new(31, 107, 276, 240);
    public static readonly Vector2 OpenMailAttachmentCaption = new(124, 389);
    public static readonly Vector2 OpenMailNoAttachmentCaption = new(187, 389);
    public static readonly LogicalRect OpenMailReply = new(101, 410, 82, 22);
    public static readonly LogicalRect OpenMailDelete = new(183, 410, 82, 22);
    public static readonly LogicalRect OpenMailBottomClose = new(265, 410, 80, 22);
    public static readonly LogicalRect OpenMailTopClose = new(321, 9, 32, 32);
    public static readonly Vector2 OpenMailSlotSize = new(37, 37);
    public static readonly LogicalRect OpenMailSlotArt = new(-10.5f, -10.5f, 58, 58);
    public static readonly Vector2 OpenMailSlotCount = new(35, 25);

    public static LogicalRect OpenMailAttachmentSlot(int zeroBasedIndex) =>
        new(189 + Math.Max(0, zeroBasedIndex) * 47, 371, 37, 37);

    public static Vector2 OpenMailCaptionCenter(bool hasAttachments) =>
        hasAttachments ? OpenMailAttachmentCaption : OpenMailNoAttachmentCaption;

    // StaticPopup seats used by COD, destructive-mail, and send-money confirmation.
    public static readonly LogicalRect ConfirmationFrame =
        new(0, 0, ConfirmationWidth, ConfirmationHeight);
    public static readonly LogicalRect ConfirmationAlert = new(12, 8, 64, 64);
    public static readonly Vector2 ConfirmationMessageCenter = new(180, 30);
    public static readonly Vector2 ConfirmationAlertMessageCenter = new(218, 30);
    public static readonly LogicalRect ConfirmationAccept = new(48, 68, 128, 20);
    public static readonly LogicalRect ConfirmationCancel = new(184, 68, 128, 20);
    public static readonly Vector2 ConfirmationButtonUvMax = new(1, .625f);

    public static Vector2 ConfirmationMessagePosition(bool alert) =>
        alert ? ConfirmationAlertMessageCenter : ConfirmationMessageCenter;

    public static Vector2 OpenMailOrigin(Vector2 mailFrameOrigin, float scale) =>
        mailFrameOrigin + OpenMailOffset * scale;

    public static Vector2 ConfirmationSize(float scale) =>
        ConfirmationFrame.ScaledSize(scale);

    public static Vector2 ConfirmationOrigin(Vector2 display, float scale)
    {
        Vector2 size = ConfirmationSize(scale);
        return new Vector2((display.X - size.X) * .5f, ConfirmationTop * scale);
    }

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
