using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

/// <summary>Current TrainerFrame window identity, top-level seat, portrait, money, and sound law.</summary>
public static class TrainerFrameUiLaw
{
    public readonly record struct LogicalRect(float X, float Y, float Width, float Height)
    {
        public Vector2 Min => new(X, Y);
        public Vector2 Size => new(Width, Height);
        public Vector2 Max => new(X + Width, Y + Height);
    }

    public readonly record struct ArtPiece(string Element, string Path, LogicalRect Rect);

    public const int VisibleRows = 11;
    public const byte AvailableState = 0;
    public const byte UsedState = 2;
    public const uint TradeskillTrainerType = 2;
    public const uint MountTrainerType = 1;
    public const uint KnownMountGroup = uint.MaxValue;
    public const float Width = 384f;
    public const float Height = 512f;
    public const float Top = 104f;
    public const string FallbackTitle = "Trainer";
    public const string OpenSound = "igCharacterInfoOpen";
    public const string CloseSound = "igCharacterInfoClose";
    public const string SoundCategory = "ui.trainer";
    public const string GreetingFont = "GameFontHighlight";
    public const string RowNameFont = "GameFontNormal";
    public const string RowSubtextFont = "GameFontNormalSmall";
    public const string DetailNameFont = "GameFontNormal";
    public const string DetailRequirementFont = "GameFontHighlightSmall";
    public const string DetailDescriptionFont = "GameFontHighlightSmall";
    public const string DetailCostFont = "GameFontNormalSmall";
    public const string CollapseAllLabel = "All";
    public const string CollapseAllFont = "GameFontNormalSmall";
    public const string CollapseAllDisabledFont = "GameFontDisableSmall";
    public const string CollapseAllMinusPath = @"Interface\Buttons\UI-MinusButton-Up";
    public const string CollapseAllPlusPath = @"Interface\Buttons\UI-PlusButton-Up";
    public const string CollapseAllHighlightPath = @"Interface\Buttons\UI-PlusButton-Hilight";
    public static readonly Vector2 PortraitOffset = new(7, 6);
    public const float PortraitSize = 60f;
    public const float TitleTop = 17f;
    public static readonly Vector2 PurseRightTop = new(180, 413);
    public static readonly Vector2 DetailCostLabel = new(30, 340);
    public const float MoneyGap = 4f;
    public const float MoneyIconSize = 13f;
    public const int GreetingMaxLines = 2;
    public const float ScrollHeight = 196f;
    public const int DetailNameMaxLines = 2;
    public const int DetailRequirementMaxLines = 2;
    public const int DetailDescriptionMaxLines = 3;
    public static readonly LogicalRect Greeting = new(76, 38, 260, 26);
    public static readonly LogicalRect CollapseAll = new(23, 72, 40, 22);
    public static readonly LogicalRect CollapseAllIcon = new(23, 75, 16, 16);
    public static readonly Vector2 CollapseAllLabelCenter = new(52, 83);
    public static readonly ArtPiece[] CollapseAllTabArt =
    [
        new("TrainerExpandTabLeft",
            @"Interface\ClassTrainerFrame\UI-ClassTrainer-ExpandTab-Left",
            new(15, 70, 8, 32)),
        new("TrainerExpandTabMiddle",
            @"Interface\QuestFrame\UI-QuestLogSortTab-Middle",
            new(23, 64, 38, 32)),
        new("TrainerExpandTabRight",
            @"Interface\QuestFrame\UI-QuestLogSortTab-Right",
            new(61, 64, 8, 32)),
    ];
    public static readonly LogicalRect Filter = new(245, 65, 96, 22);
    public static readonly LogicalRect ListWheel = new(22, 96, 293, 184);
    public static readonly Vector2 HeaderTextOffset = new(22, 2);
    public static readonly Vector2 ScrollOrigin = new(310, 91);
    public static readonly LogicalRect HorizontalBarLeft = new(15, 275, 256, 16);
    public static readonly LogicalRect HorizontalBarRight = new(271, 275, 75, 16);
    public static readonly Vector2 HorizontalBarRightUvMin = new(0, .25f);
    public static readonly Vector2 HorizontalBarRightUvMax = new(.29296875f, .5f);
    public static readonly LogicalRect DetailIcon = new(27, 294, 37, 37);
    public static readonly LogicalRect DetailIconRing = new(14, 307, 64, 64);
    public static readonly LogicalRect DetailNameBox = new(68, 293, 244, 24);
    public static readonly LogicalRect DetailRequirementBox = new(68, 309, 244, 20);
    public static readonly LogicalRect DetailDescriptionBox = new(30, 360, 290, 30);
    public static readonly LogicalRect Train = new(184, 409, 80, 22);
    public static readonly LogicalRect Exit = new(265, 409, 80, 22);
    public static readonly LogicalRect Close = new(322, 8, 32, 32);
    public static readonly LogicalRect FilterMenu = new(224, 88, 126, 58);
    public static readonly Vector2 FilterRowTextOffset = new(2, 1);
    public static readonly ArtPiece[] ShellArt =
    [
        new("ClassTrainerFrame/Texture",
            @"Interface\ClassTrainerFrame\UI-ClassTrainer-TopLeft",
            new(0, 0, 256, 256)),
        new("ClassTrainerFrame/Texture#2",
            @"Interface\ClassTrainerFrame\UI-ClassTrainer-TopRight",
            new(256, 0, 128, 256)),
        new("ClassTrainerFrameBottomLeft",
            @"Interface\ClassTrainerFrame\UI-ClassTrainer-BotLeft",
            new(0, 256, 256, 256)),
        new("ClassTrainerFrameBottomRight",
            @"Interface\ClassTrainerFrame\UI-ClassTrainer-BotRight",
            new(256, 256, 128, 256)),
    ];

    public static Vector2 FrameOrigin(float scale) => new(0, Top * scale);
    public static Vector2 FrameSize(float scale) => new(Width * scale, Height * scale);

    public static (Vector2 Minimum, Vector2 Maximum) DetailTooltipOwnerBounds(
        Vector2 origin, float scale)
    {
        Vector2 minimum = origin + DetailIcon.Min * scale;
        return (minimum, minimum + DetailIcon.Size * scale);
    }

    public static string Title(string? npcName) =>
        string.IsNullOrWhiteSpace(npcName) ? FallbackTitle : npcName.Trim();

    public static Vector2 TitleCenter(float fontEm) => new(Width * .5f, TitleTop + fontEm * .5f);

    public static LogicalRect Row(int visible) =>
        new(22, 100 + Math.Clamp(visible, 0, VisibleRows - 1) * 16, 293, 16);

    public static LogicalRect HeaderIcon(LogicalRect row) =>
        new(row.X + 3, row.Y, 16, 16);

    public static Vector2 RowNameMinimum(Vector2 rowMinimum, float rowHeight, float textEm) =>
        new(rowMinimum.X + 22, rowMinimum.Y + (rowHeight - textEm) * .5f);

    public static Vector2 RowSubtextMinimum(Vector2 nameMinimum, float nameWidth, float scale) =>
        new(nameMinimum.X + nameWidth + 10 * scale, nameMinimum.Y);

    public static Vector2 TextLineMinimum(Vector2 origin, LogicalRect box, float scale,
        int line, float pitch) =>
        origin + box.Min * scale + Vector2.UnitY * Math.Max(0, line) * pitch;

    public static IReadOnlyList<string> WrapText(string? text, float width, int maximumLines,
        Func<string, float> measure)
    {
        if (string.IsNullOrWhiteSpace(text) || width <= 0 || maximumLines <= 0) return [];
        var lines = new List<string>();
        foreach (string paragraph in text.Replace("\r", "").Split('\n'))
        {
            string current = "";
            foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && measure(candidate) > width)
                {
                    lines.Add(current);
                    if (lines.Count == maximumLines) return lines;
                    current = word;
                }
                else current = candidate;
            }
            if (current.Length > 0)
            {
                lines.Add(current);
                if (lines.Count == maximumLines) return lines;
            }
        }
        return lines;
    }

    public static uint RowNameColor(byte state, bool selected) => selected ? 0xffffffff :
        state == AvailableState ? 0xff00ff00 : state == UsedState ? 0xff808080 : 0xff0000e6;

    public static uint RowSubtextColor(byte state, bool selected, bool hovered) =>
        selected || hovered ? 0xffffffff : state == AvailableState ? 0xff009900 :
        state == UsedState ? 0xff808080 : 0xff000099;

    public static LogicalRect FilterRow(int index) =>
        new(3, 3 + Math.Clamp(index, 0, 2) * 17, 120, 16);

    public static Vector2 DetailMoneyAt(float labelWidth, float scale) =>
        DetailCostLabel + new Vector2(labelWidth / scale + MoneyGap, 0);

    public static Vector2 MoneyPoint(float x, float y) => new(x, y);

    public static bool StateVisible(byte state, bool available, bool unavailable, bool used) =>
        state == AvailableState ? available : state == UsedState ? used : unavailable;

    public static uint TaughtSpell(in SpellInfo wire)
    {
        if (wire.EffectIds is null || wire.EffectTriggerSpells is null) return wire.Id;
        int count = Math.Min(wire.EffectIds.Length, wire.EffectTriggerSpells.Length);
        for (int i = 0; i < count; i++)
            if (wire.EffectIds[i] is 36 or 57 && wire.EffectTriggerSpells[i] != 0)
                return wire.EffectTriggerSpells[i];
        return wire.Id;
    }

    public static (uint Key, string Name) ServiceGroup(uint trainerType, byte state,
        in SpellInfo wire, SkillLineCatalog? skillLines)
    {
        if (trainerType == TradeskillTrainerType)
            return wire.EffectIds?.Contains(44u) == true
                ? (1u, "Development Skills") : (2u, "Recipes");
        if (trainerType == MountTrainerType && state == UsedState)
            return (KnownMountGroup, "My Talents");
        uint line = skillLines?.SpellLine(TaughtSpell(wire)) ?? 0;
        if (line == 0) return (0, "");
        return (line, skillLines?.TryGet(line, out SkillLineInfo info) == true
            ? info.Name : $"Skill {line}");
    }

    public readonly record struct ServiceNode(int ServiceIndex, uint GroupKey, string GroupName,
        string Name, byte State, byte RequiredLevel);
    public readonly record struct TreeRow(bool Header, uint GroupKey, string Text,
        int ServiceIndex, byte State, bool Expanded);

    public static IReadOnlyList<TreeRow> BuildTree(IEnumerable<ServiceNode> services,
        uint trainerType, IReadOnlySet<uint> collapsed, bool available, bool unavailable, bool used)
    {
        var groups = services
            .Where(s => s.GroupKey != 0 && StateVisible(s.State, available, unavailable, used))
            .GroupBy(s => new { s.GroupKey, s.GroupName })
            .Select(group => new
            {
                Key = group.Key.GroupKey,
                Name = group.Key.GroupName,
                Services = group.OrderBy(s => s.RequiredLevel)
                    .ThenBy(s => s.State == AvailableState ? 0 : s.State == UsedState ? 2 : 1)
                    .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            });
        groups = trainerType == TradeskillTrainerType
            ? groups.OrderBy(g => g.Key)
            : groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Key);

        var rows = new List<TreeRow>();
        foreach (var group in groups)
        {
            bool expanded = !collapsed.Contains(group.Key);
            rows.Add(new(true, group.Key, group.Name, -1, 0, expanded));
            if (!expanded) continue;
            rows.AddRange(group.Services.Select(service => new TreeRow(false, group.Key,
                service.Name, service.ServiceIndex, service.State, false)));
        }
        return rows;
    }
}
