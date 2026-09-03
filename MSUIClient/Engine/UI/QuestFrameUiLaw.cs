using System.Numerics;

namespace MSUIClient.Engine.UI;

public enum QuestNpcPanel { None, Greeting, Detail, Progress, Reward }
public enum QuestGreetingPool { Active, Available }
public enum QuestGreetingAction { Query, Complete }
public enum QuestItemClickAction { None, DressUp, InsertChat, Select }
public readonly record struct QuestCoin(int Denomination, uint Value);
public readonly record struct QuestMoneyCoinSeat(
    Vector2 NumberMin,
    Vector2 IconMin,
    Vector2 FrameSize,
    Vector2 NumberSize,
    Vector2 IconSize,
    float NextX);
public readonly record struct QuestLogicalRect(float X, float Y, float Width, float Height)
{
    public Vector2 Min => new(X, Y);
    public Vector2 Size => new(Width, Height);
    public Vector2 ScaledMin(Vector2 origin, float scale) => origin + Min * scale;
    public Vector2 ScaledSize(float scale) => Size * scale;
}
public readonly record struct QuestFrameArtSeat(string Path, QuestLogicalRect Rect);
public readonly record struct QuestScreenRect(Vector2 Min, Vector2 Max);
public readonly record struct QuestTooltipSeat(Vector2 Anchor, Vector2 Pivot);
public readonly record struct QuestLogHeaderGroup(string Header, IReadOnlyList<int> QuestIndexes);

/// <summary>Authored QuestFrame.xml geometry and bounded panel behavior.</summary>
public static class QuestFrameUiLaw
{
    public const string QuestAddedSound = "QUESTADDED";
    public const float Width = 384f;
    public const float Height = 512f;
    public const float ScrollX = 23f;
    public const float ScrollY = 81f;
    public const float ScrollWidth = 300f;
    public const float ScrollHeight = 334f;
    public const float ScrollStep = 20f;
    public const float ScrollTrackHeight = 302f;
    public const float ScrollThumbHeight = 16f;
    public const float ContentWidth = 300f;
    public const int MaxTitles = 10;
    public const int MaxItems = 6;
    public const float ItemWidth = 147f;
    public const float ItemHeight = 41f;
    public const float ItemIcon = 39f;
    public static readonly Vector2 ItemGridInset = new(-3, 0);
    public static readonly QuestLogicalRect ItemHitRect = new(0, 0, ItemWidth, ItemHeight);
    public static readonly QuestLogicalRect ItemIconRect = new(0, 0, ItemIcon, ItemIcon);
    public static readonly QuestLogicalRect ItemNameFrameRect = new(29, -12, 128, 64);
    public static readonly Vector2 ItemNameTextOffset = new(44, 12);
    public static readonly Vector2 ItemCountAnchor = new(35, 25);
    public static readonly QuestLogicalRect ItemHighlightRect = new(-8, -7, 256, 64);

    // QuestItemTemplate OnEnter uses ANCHOR_RIGHT: GameTooltip BOTTOMLEFT to item TOPRIGHT.
    public static QuestTooltipSeat ItemTooltipSeat(Vector2 itemMin, Vector2 itemSize) =>
        new(itemMin + Vector2.UnitX * itemSize.X, Vector2.UnitY);
    public static readonly QuestLogicalRect FrameRect = new(0, 0, Width, Height);
    public static readonly QuestLogicalRect NpcPortraitRect = new(7, 6, 60, 60);
    public const string NpcNameFont = "GameFontHighlight";
    public static readonly Vector2 NpcNameCenter = new(192, 30);
    public static readonly QuestLogicalRect NpcNameFrameRect = new(42, 16, 300, 14);
    public static readonly QuestLogicalRect NpcNameTextRect = new(74.5f, 20, 235, 20);
    public static readonly QuestLogicalRect NpcCloseRect = new(326, 15, 32, 32);
    public static readonly QuestLogicalRect NpcScrollRect = new(
        ScrollX, ScrollY, ScrollWidth, ScrollHeight);
    public static readonly QuestLogicalRect NpcScrollBarRect = new(329, 81, 16, 334);
    public static readonly QuestLogicalRect NpcScrollUpRect = new(329, 81, 16, 16);
    public static readonly QuestLogicalRect NpcScrollDownRect = new(329, 399, 16, 16);
    public static readonly QuestLogicalRect NpcScrollTrackRect = new(329, 97, 16, 302);
    public static readonly QuestLogicalRect NpcGreetingGoodbyeRect = new(267, 417, 78, 22);
    public static readonly QuestLogicalRect NpcDetailAcceptRect = new(23, 418, 77, 22);
    public static readonly QuestLogicalRect NpcDetailDeclineRect = new(267, 418, 78, 22);
    public static readonly QuestLogicalRect NpcProgressPrimaryRect = new(22, 418, 120, 22);
    public static readonly QuestLogicalRect NpcProgressGoodbyeRect = new(267, 417, 78, 22);
    public static readonly QuestLogicalRect NpcRewardPrimaryRect = new(22, 418, 120, 22);
    public static readonly QuestLogicalRect NpcRewardCancelRect = new(267, 417, 78, 22);
    public const float NpcContentInitialY = 10f;
    public const float NpcContentTextX = 5f;
    public const float NpcContentBodyWidth = 270f;
    public const float NpcRewardBodyWidth = 275f;
    public const float NpcContentTitleTraceWidth = 295f;
    public const float NpcRewardTitleTraceWidth = 285f;
    public const float NpcReceiveTextX = 8f;
    public const float MoneyCoinSize = 13f;
    public const float MoneyCoinGap = 4f;
    public const float MoneyProgressFirstOffset = 10f;
    public const float MoneyRewardFirstOffset = 15f;
    public const float GreetingTextX = 10f;
    public const float GreetingTextWidth = 270f;
    public const float GreetingHeaderTraceWidth = 300f;
    public const float GreetingRowWidth = 285f;
    public const float GreetingTitleX = 20f;
    public const float GreetingTitleWidth = 275f;
    public static readonly QuestLogicalRect GreetingBulletRect = new(0, 0, 16, 16);
    public const int QuestLogRows = 6;
    public const float QuestLogListX = 19f;
    public const float QuestLogListY = 75f;
    public const float QuestLogListWidth = 300f;
    public const float QuestLogRowHeight = 16f;
    public const float QuestLogRowPitch = 15f;
    public static readonly QuestLogicalRect QuestLogListRect = new(QuestLogListX,
        QuestLogListY, QuestLogListWidth, QuestLogRows * QuestLogRowPitch + 1f);
    public static readonly Vector2 QuestLogTitleCenter = new(192, 22);
    public static readonly Vector2 QuestLogRowSize = new(QuestLogListWidth, QuestLogRowHeight);
    public static readonly Vector2 QuestLogFoldIconSize = new(16);
    public static readonly Vector2 QuestLogRadioBaseUvMax = new(.25f, 1f);
    public static readonly Vector2 QuestLogRadioCheckUvMin = new(.25f, 0f);
    public static readonly Vector2 QuestLogRadioCheckUvMax = new(.5f, 1f);
    public static readonly Vector2 QuestLogScrollButtonUvMin = new(.25f);
    public static readonly Vector2 QuestLogScrollButtonUvMax = new(.75f);
    public static readonly QuestLogicalRect QuestLogCloseRect = new(322, 8, 32, 32);
    public static readonly QuestLogicalRect QuestLogCountRightRect = new(329, 41, 8, 20);
    public static readonly QuestLogicalRect QuestLogCollapseFrameRect = new(70, 48, 54, 32);
    public static readonly QuestLogicalRect QuestLogCollapseButtonRect = new(70, 50, 40, 22);
    public static readonly QuestLogicalRect QuestLogCollapseIconRect = new(73, 53, 16, 16);
    public static readonly QuestLogicalRect QuestLogCollapseTitleRect = new(90, 56, 20, 10);
    public static readonly QuestLogicalRect QuestLogCollapseLeftRect = new(64, 40, 8, 32);
    public static readonly QuestLogicalRect QuestLogCollapseMiddleRect = new(72, 40, 38, 32);
    public static readonly QuestLogicalRect QuestLogCollapseRightRect = new(110, 40, 8, 32);
    public static readonly QuestLogicalRect QuestLogTrackRect = new(129, 44, 20, 20);
    public static readonly QuestLogicalRect QuestLogTrackTitleRect = new(149, 49, 76, 12);
    public static readonly QuestLogicalRect QuestLogDetailRect = new(19, 175, 300, 261);
    public const float QuestLogDetailTextX = 24f;
    public const float QuestLogDetailTextWidth = 285f;
    public const float QuestLogDetailTitleY = 180f;
    public const float QuestLogDetailObjectivesTitleY = 207f;
    public const float QuestLogDetailInitialY = 228f;
    public const float QuestLogDetailMoneyX = 190f;
    public static readonly Vector2 QuestLogRewardSpellSize = new(20);
    public static readonly Vector2 QuestLogRewardSpellTextOffset = new(25, 4);
    public static readonly QuestLogicalRect QuestLogDetailScrollBarRect = new(325, 175, 16, 261);
    public static readonly QuestLogicalRect QuestLogDetailScrollUpRect = new(325, 175, 16, 16);
    public static readonly QuestLogicalRect QuestLogDetailScrollDownRect = new(325, 420, 16, 16);
    public static readonly QuestLogicalRect QuestLogDetailScrollTrackRect = new(325, 191, 16, 229);
    public static readonly QuestLogicalRect QuestLogAbandonRect = new(17, 437, 125, 21);
    public static readonly QuestLogicalRect QuestLogShareRect = new(141, 437, 123, 21);
    public static readonly QuestLogicalRect QuestLogExitRect = new(264, 437, 77, 21);
    public static readonly QuestFrameArtSeat[] QuestLogCollapseArt =
    [
        new(@"Interface\QuestFrame\UI-QuestLogSortTab-Left", QuestLogCollapseLeftRect),
        new(@"Interface\QuestFrame\UI-QuestLogSortTab-Middle", QuestLogCollapseMiddleRect),
        new(@"Interface\QuestFrame\UI-QuestLogSortTab-Right", QuestLogCollapseRightRect),
    ];
    public static readonly QuestLogicalRect AbandonPopupRect = new(0, 128, 320, 72);
    public static readonly QuestLogicalRect AbandonPopupTextRect = new(15, 16, 290, 12);
    public static readonly QuestLogicalRect AbandonPopupAcceptRect = new(26, 36, 128, 20);
    public static readonly QuestLogicalRect AbandonPopupCancelRect = new(167, 36, 128, 20);
    public const double AutoQuestWatchSeconds = 300;
    public const int MaxQuestWatchLines = 30;
    public const float QuestWatchNominalWidth = 280f;
    public const float QuestWatchSpacerHeight = 13f;
    public const float QuestWatchInitialGap = 1f;
    public const float QuestWatchTitleGap = 5f;
    public const float QuestWatchObjectiveGap = 1f;

    public static Vector2 WindowOrigin(float scale) => new(0f, 104f * scale);
    public static Vector2 WindowSize(float scale) => new(Width * scale, Height * scale);
    public static QuestLogicalRect CloseRect(bool questLog) =>
        questLog ? QuestLogCloseRect : NpcCloseRect;

    public static IReadOnlyList<QuestFrameArtSeat> PanelArt(bool questLog, bool greeting)
    {
        var seats = new List<QuestFrameArtSeat>(questLog ? 5 : greeting ? 5 : 4);
        if (questLog)
            seats.Add(new(@"Interface\QuestFrame\UI-QuestLog-BookIcon",
                new(4, 4, 64, 64)));
        string stem = questLog ? "UI-QuestLog" : "UI-QuestGreeting";
        seats.Add(new($@"Interface\QuestFrame\{stem}-TopLeft", new(0, 0, 256, 256)));
        seats.Add(new($@"Interface\QuestFrame\{stem}-TopRight", new(256, 0, 128, 256)));
        seats.Add(new($@"Interface\QuestFrame\{stem}-BotLeft", new(0, 256, 256, 256)));
        seats.Add(new($@"Interface\QuestFrame\{stem}-BotRight", new(256, 256, 128, 256)));
        if (!questLog && greeting)
            seats.Add(new(@"Interface\QuestFrame\UI-Quest-BotLeftPatch",
                new(22, 380, 128, 64)));
        return seats;
    }
    public static Vector2 AbandonPopupOrigin(Vector2 display, float scale)
    {
        float width = AbandonPopupRect.Width * scale;
        return new((display.X - width) * .5f, AbandonPopupRect.Y * scale);
    }
    public static Vector2 AbandonPopupTextCenter(Vector2 origin, float scale) =>
        origin + new Vector2(
            AbandonPopupTextRect.X + AbandonPopupTextRect.Width * .5f,
            AbandonPopupTextRect.Y + AbandonPopupTextRect.Height * .5f) * scale;

    /// <summary>The party follow-up has two text lines and can name four companions.</summary>
    public static QuestLogicalRect PartyAbandonPopupRect(float widestLine,
        float displayWidth, float scale)
    {
        float available = Math.Max(AbandonPopupRect.Width, displayWidth / scale - 8f);
        float width = Math.Clamp(widestLine + 30f, AbandonPopupRect.Width, available);
        return new(0, AbandonPopupRect.Y, width, 88f);
    }

    public static Vector2 AbandonPopupOrigin(Vector2 display, float scale,
        QuestLogicalRect frame) =>
        new((display.X - frame.Width * scale) * .5f, frame.Y * scale);

    public static Vector2 PartyAbandonTextCenter(Vector2 origin, float scale,
        QuestLogicalRect frame, bool names) =>
        origin + new Vector2(frame.Width * .5f, names ? 34f : 18f) * scale;

    public static QuestLogicalRect PartyAbandonButtonRect(QuestLogicalRect frame,
        bool accept)
    {
        QuestLogicalRect source = accept ? AbandonPopupAcceptRect : AbandonPopupCancelRect;
        return new(source.X + (frame.Width - AbandonPopupRect.Width) * .5f,
            52f, source.Width, source.Height);
    }
    /// <summary>Managed right stack: tracker follows the minimap, then the shown armor guy.</summary>
    public static Vector2 QuestWatchTopRight(Vector2 display, float scale,
        bool durabilityShown = false) =>
        QuestWatchTopRight(display, scale, 0f, durabilityShown);
    public static Vector2 QuestWatchTopRight(Vector2 display, float scale,
        float questTimerHeight, bool durabilityShown) =>
        new(display.X, (192f + Math.Max(0f, questTimerHeight) +
            (durabilityShown ? DurabilityFrameUiLaw.Height : 0f)) * scale);
    public static float QuestWatchLineTop(float previousBottom, bool title, bool first) =>
        first ? QuestWatchSpacerHeight + QuestWatchInitialGap
            : previousBottom + (title ? QuestWatchTitleGap : QuestWatchObjectiveGap);
    public static string ItemLink(uint itemId, string name, uint quality)
        => UiTextMarkupLaw.ItemLink(itemId, name, quality);
    public static QuestItemClickAction ItemClickAction(bool clicked, bool control,
        bool shift, bool chatOpen, bool selectable, bool linkAvailable)
    {
        if (!clicked) return QuestItemClickAction.None;
        if (control)
            return linkAvailable ? QuestItemClickAction.DressUp : QuestItemClickAction.None;
        if (shift)
            return chatOpen && linkAvailable
                ? QuestItemClickAction.InsertChat
                : QuestItemClickAction.None;
        return selectable ? QuestItemClickAction.Select : QuestItemClickAction.None;
    }
    public static int ClampQuestLogOffset(int offset, int questCount) =>
        Math.Clamp(offset, 0, Math.Max(0, questCount - QuestLogRows));
    public static float ClampQuestLogDetailScroll(float offset, float contentHeight) =>
        Math.Clamp(offset, 0, Math.Max(0, contentHeight - QuestLogDetailRect.Height));
    public static QuestScreenRect QuestLogDetailClip(Vector2 origin, float scale)
    {
        Vector2 min = QuestLogDetailRect.ScaledMin(origin, scale);
        return new(min, min + QuestLogDetailRect.ScaledSize(scale));
    }
    public static Vector2 QuestLogDetailContentOrigin(Vector2 origin, float scroll, float scale) =>
        origin - Vector2.UnitY * Math.Max(0, scroll) * scale;
    public static Vector2 QuestLogDetailTextMin(Vector2 contentOrigin, float y, float scale) =>
        contentOrigin + new Vector2(QuestLogDetailTextX, y) * scale;
    public static Vector2 QuestLogDetailGridOrigin(Vector2 contentOrigin, float scale) =>
        contentOrigin + new Vector2(QuestLogDetailTextX, 0) * scale;
    public static Vector2 QuestLogDetailMoneyMin(Vector2 contentOrigin, float y, float scale) =>
        contentOrigin + new Vector2(QuestLogDetailMoneyX, y) * scale;
    public static Vector2 QuestLogRewardSpellMin(Vector2 contentOrigin, float y, float scale) =>
        QuestLogDetailTextMin(contentOrigin, y, scale);
    public static Vector2 QuestLogRewardSpellTextMin(Vector2 spellMin, float scale) =>
        spellMin + QuestLogRewardSpellTextOffset * scale;
    public static QuestLogicalRect QuestLogCountPillRect(float textWidth)
    {
        float middle = Math.Max(1f, textWidth);
        return new(QuestLogCountRightRect.X - middle - 8f, QuestLogCountRightRect.Y,
            middle + 16f, QuestLogCountRightRect.Height);
    }
    public static float QuestLogDetailThumbY(float offset, float contentHeight)
    {
        float maximum = Math.Max(0, contentHeight - QuestLogDetailRect.Height);
        float fraction = maximum <= 0 ? 0 : Math.Clamp(offset / maximum, 0, 1);
        return QuestLogDetailScrollTrackRect.Y +
            fraction * (QuestLogDetailScrollTrackRect.Height - ScrollThumbHeight);
    }
    public static Vector2 QuestLogRowMin(int visibleRow) =>
        new(QuestLogListX, QuestLogListY + Math.Clamp(visibleRow, 0, QuestLogRows - 1) * QuestLogRowPitch);
    public static Vector2 QuestLogFoldIconMin(int visibleRow) =>
        QuestLogRowMin(visibleRow) + new Vector2(3, 0);
    public static QuestLogicalRect QuestLogRowRect(int visibleRow)
    {
        Vector2 min = QuestLogRowMin(visibleRow);
        return new(min.X, min.Y, QuestLogListWidth, QuestLogRowHeight);
    }
    public static QuestLogicalRect QuestLogFoldIconRect(int visibleRow)
    {
        Vector2 min = QuestLogFoldIconMin(visibleRow);
        return new(min.X, min.Y, 16, 16);
    }
    public static QuestLogicalRect QuestLogWatchCheckRect(int visibleRow, float titleInkWidth)
    {
        Vector2 min = QuestLogRowMin(visibleRow);
        return new(min.X + 9f + Math.Max(0, titleInkWidth), min.Y, 16, 16);
    }
    public static Vector2 QuestLogCountTextMin(QuestLogicalRect pill, float labelWidth,
        float valueWidth) => new(
            QuestLogCountRightRect.X + QuestLogCountRightRect.Width - 6f -
                valueWidth - 3f - labelWidth,
            pill.Y + 4f);
    public static Vector2 QuestLogCountValueMin(Vector2 labelMin, float labelWidth) =>
        labelMin + new Vector2(labelWidth + 3f, 0);
    public static QuestLogicalRect QuestLogDetailThumbRect(float offset, float contentHeight) =>
        new(QuestLogDetailScrollTrackRect.X, QuestLogDetailThumbY(offset, contentHeight),
            QuestLogDetailScrollTrackRect.Width, ScrollThumbHeight);

    /// <summary>Benilla's current section model: ordinal header sort, descriptor order in group.</summary>
    public static IReadOnlyList<QuestLogHeaderGroup> GroupQuestLogHeaders(
        IReadOnlyList<string> questHeaders)
    {
        var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < questHeaders.Count; i++)
        {
            string header = string.IsNullOrEmpty(questHeaders[i]) ? "Quests" : questHeaders[i];
            if (!groups.TryGetValue(header, out List<int>? indexes))
                groups[header] = indexes = [];
            indexes.Add(i);
        }
        return groups.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new QuestLogHeaderGroup(pair.Key, pair.Value))
            .ToArray();
    }

    /// <summary>Frozen SecondsToTime: strict day/hour thresholds and at most two units.</summary>
    public static string SecondsToTime(long seconds, bool noSeconds = false)
    {
        seconds = Math.Max(0, seconds);
        var parts = new List<string>(2);
        void Add(long value, string singular, string plural) =>
            parts.Add($"{value} {(value == 1 ? singular : plural)}");
        if (seconds > 86400)
        {
            long days = seconds / 86400;
            Add(days, "Day", "Days");
            seconds %= 86400;
        }
        if (seconds > 3600)
        {
            long hours = seconds / 3600;
            Add(hours, "Hr", "Hrs");
            seconds %= 3600;
        }
        if (parts.Count < 2 && seconds >= 60)
        {
            long minutes = seconds / 60;
            Add(minutes, "Min", "Mins");
            seconds %= 60;
        }
        if (parts.Count < 2 && seconds > 0 && !noSeconds)
            Add(seconds, "Sec", "Secs");
        return parts.Count == 0 ? "" : string.Join(' ', parts) + " ";
    }

    /// <summary>GetDifficultyColor: red/orange/yellow/green/grey with the shared grey band.</summary>
    public static Vector4 QuestDifficultyColor(uint playerLevel, uint questLevel)
    {
        long difference = (long)questLevel - playerLevel;
        if (difference >= 5) return new(1f, .1f, .1f, 1f);
        if (difference >= 3) return new(1f, .5f, .25f, 1f);
        if (difference >= -2) return new(1f, 1f, 0f, 1f);
        ReadOnlySpan<uint> bands =
            [4, 4, 5, 5, 6, 6, 7, 7, 8, 9, 10, 11, 12, 12, 12, 12, 12, 12, 12, 12];
        uint band = bands[(int)Math.Min(playerLevel / 5, (uint)bands.Length - 1)];
        return playerLevel - questLevel <= band
            ? new(.25f, .75f, .25f, 1f) : new(.5f, .5f, .5f, 1f);
    }

    public static Vector2 CloseMin => NpcCloseRect.Min; // center at TOPRIGHT(-42,-31)
    public static Vector2 ItemGridOffset(int index) =>
        new((index & 1) == 0 ? 0 : ItemWidth + 1f, (index / 2) * (ItemHeight + 2f));
    public static Vector2 ItemGridRowMin(Vector2 origin, float y, int index, float scale) =>
        origin + (ItemGridInset + Vector2.UnitY * y + ItemGridOffset(index)) * scale;
    public static Vector2 ItemNameTextMin(Vector2 itemMin, float scale) =>
        itemMin + ItemNameTextOffset * scale;
    public static Vector2 ItemCountMin(Vector2 itemMin, Vector2 countSize, float scale) =>
        itemMin + ItemCountAnchor * scale - Vector2.UnitX * countSize.X;
    public static float ClampScroll(float value, float contentHeight) =>
        Math.Clamp(value, 0, Math.Max(0, contentHeight - ScrollHeight));
    public static QuestScreenRect NpcScrollClip(Vector2 origin, float scale)
    {
        Vector2 min = NpcScrollRect.ScaledMin(origin, scale);
        return new(min, min + NpcScrollRect.ScaledSize(scale));
    }
    public static Vector2 NpcScrollContentOrigin(Vector2 scrollMin, float offset, float scale) =>
        scrollMin - Vector2.UnitY * Math.Max(0, offset) * scale;
    public static Vector2 NpcScrollContentSize(float contentHeight, float scale) =>
        new Vector2(ScrollWidth, Math.Max(contentHeight, ScrollHeight)) * scale;
    public static Vector2 NpcContentTextMin(Vector2 contentOrigin, float y, float scale) =>
        contentOrigin + new Vector2(NpcContentTextX, y) * scale;
    public static Vector2 NpcReceiveTextMin(Vector2 contentOrigin, float y, float scale) =>
        contentOrigin + new Vector2(NpcReceiveTextX, y) * scale;
    public static Vector2 NpcTraceSize(float logicalWidth, float screenHeight, float scale) =>
        new(logicalWidth * scale, screenHeight);
    public static Vector2 NpcInlineMoneyMin(Vector2 contentOrigin, float y,
        float logicalTextX, float screenTextWidth, float logicalGap, float scale) =>
        contentOrigin + new Vector2(logicalTextX, y) * scale +
            Vector2.UnitX * (screenTextWidth + logicalGap * scale);
    public static Vector2 NpcWrappedLineMin(Vector2 firstLineMin, int line,
        float lineHeight) =>
        firstLineMin + Vector2.UnitY * Math.Max(0, line) * lineHeight;
    public static Vector2 NpcScreenTextSize(float widthPixels, float heightPixels) =>
        new(Math.Max(0, widthPixels), Math.Max(0, heightPixels));
    public static QuestMoneyCoinSeat MoneyCoinSeat(Vector2 rowMin, float x,
        float screenNumberWidth, float scale)
    {
        Vector2 numberMin = new(x, rowMin.Y);
        Vector2 iconMin = numberMin + Vector2.UnitX * screenNumberWidth;
        float coinSize = MoneyCoinSize * scale;
        return new(numberMin, iconMin,
            new Vector2(screenNumberWidth + coinSize, coinSize),
            new Vector2(screenNumberWidth, coinSize),
            new Vector2(coinSize),
            x + screenNumberWidth + (MoneyCoinSize + MoneyCoinGap) * scale);
    }
    public static float MoneyAnchorOffset(int slot, bool progressMoney) => slot == 0
        ? progressMoney ? MoneyProgressFirstOffset : MoneyRewardFirstOffset
        : MoneyCoinGap;
    public static Vector2 GreetingTextMin(Vector2 contentOrigin, float y, float scale) =>
        contentOrigin + new Vector2(GreetingTextX, y) * scale;
    public static QuestLogicalRect GreetingBreakRect(float y) => new(22, y + 10, 256, 32);
    public static Vector2 GreetingRowMin(Vector2 contentOrigin, float y, float scale) =>
        contentOrigin + Vector2.UnitY * y * scale;
    public static Vector2 GreetingRowSize(float textHeight, float scale) =>
        new Vector2(GreetingRowWidth, Math.Max(16, textHeight) + 2) * scale;
    public static Vector2 GreetingTitleMin(Vector2 rowMin, float scale) =>
        rowMin + Vector2.UnitX * GreetingTitleX * scale;
    public static Vector2 GreetingTitleTraceSize(float textHeight, float scale) =>
        new(GreetingTitleWidth * scale, Math.Max(16, textHeight) * scale);
    public static float ScrollThumbY(float value, float contentHeight)
    {
        float range = Math.Max(0, contentHeight - ScrollHeight);
        return range <= 0 ? 0 : value / range * (ScrollTrackHeight - ScrollThumbHeight);
    }
    public static QuestLogicalRect NpcScrollThumbRect(float value, float contentHeight) =>
        new(NpcScrollTrackRect.X, NpcScrollTrackRect.Y + ScrollThumbY(value, contentHeight),
            NpcScrollTrackRect.Width, ScrollThumbHeight);
    public static bool RewardCompleteEnabled(int choiceCount, int selectedChoice) =>
        choiceCount == 0 || selectedChoice >= 0 && selectedChoice < choiceCount;

    /// <summary>The frozen client plays QUESTADDED when a quest enters an established log.
    /// The first descriptor snapshot after login is a baseline, not an accept event.</summary>
    public static bool ShouldPlayQuestAddedSound(bool snapshotKnown,
        IReadOnlySet<uint> previous, IReadOnlySet<uint> current) =>
        snapshotKnown && current.Any(id => !previous.Contains(id));

    public static string ObjectiveItemLabel(string? resolvedName) =>
        string.IsNullOrWhiteSpace(resolvedName) ? "..." : resolvedName;

    /// <summary>The frozen client splits greeting and gossip rows solely by the wire icon.</summary>
    public static QuestGreetingPool GreetingPool(uint icon) => icon is 3 or 4
        ? QuestGreetingPool.Active : QuestGreetingPool.Available;

    /// <summary>Active rows always complete; available icon 0 is the one-click complete path.</summary>
    public static QuestGreetingAction GreetingAction(uint icon) =>
        GreetingPool(icon) == QuestGreetingPool.Active || icon == 0
            ? QuestGreetingAction.Complete : QuestGreetingAction.Query;

    public static IReadOnlyList<QuestCoin> Money(uint copper)
    {
        uint[] values = [copper / 10000, copper / 100 % 100, copper % 100];
        var result = new List<QuestCoin>(3);
        for (int denomination = 0; denomination < values.Length; denomination++)
            if (values[denomination] != 0) result.Add(new QuestCoin(denomination, values[denomination]));
        return result;
    }

    public static string InvalidReasonKey(uint reason) => reason switch
    {
        1 => "ERR_QUEST_FAILED_LOW_LEVEL",
        6 => "ERR_QUEST_FAILED_WRONG_RACE",
        12 => "ERR_QUEST_ONLY_ONE_TIMED",
        13 => "ERR_QUEST_ALREADY_ON",
        20 => "ERR_QUEST_FAILED_MISSING_ITEMS",
        22 => "ERR_QUEST_FAILED_NOT_ENOUGH_MONEY",
        _ => "ERR_QUEST_NEED_PREREQS",
    };

    public static string GiverFailureKey(uint reason) => reason switch
    {
        4 or 50 => "ERR_QUEST_FAILED_BAG_FULL_S",
        17 => "ERR_QUEST_FAILED_MAX_COUNT_S",
        _ => "ERR_QUEST_FAILED_S",
    };
}

/// <summary>Frozen world-state init semantics used by quest/NPC text macros.</summary>
public static class QuestWorldStateLaw
{
    public static void ApplyInit(IDictionary<uint, uint> target,
        IEnumerable<(uint Id, uint Value)> received)
    {
        // The frozen snapshot intentionally does not invent a table clear for the reference's
        // still-unresolved map/zone setup call. Every wire pair, including (0,0), is an ordinary
        // upsert and keys omitted by the new init survive.
        foreach ((uint id, uint value) in received) target[id] = value;
    }
}
