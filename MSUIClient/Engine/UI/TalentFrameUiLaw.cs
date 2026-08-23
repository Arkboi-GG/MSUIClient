using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Current Benilla TalentFrame geometry, fixed ScrollFrame range, and prerequisite-atlas routing.
/// Renderer code consumes these authored seats; it does not invent window coordinates.
/// </summary>
public static class TalentFrameUiLaw
{
    public readonly record struct LogicalRect(float X, float Y, float Width, float Height)
    {
        public Vector2 Min => new(X, Y);
        public Vector2 Size => new(Width, Height);
        public Vector2 ScaledMin(Vector2 origin, float scale) => origin + Min * scale;
        public Vector2 ScaledSize(float scale) => Size * scale;
    }

    public readonly record struct TextureSlice(LogicalRect Rect, Vector2 UvMin, Vector2 UvMax);
    public readonly record struct TooltipSeat(Vector2 Anchor, Vector2 Pivot);

    public readonly record struct DependencyRoute(
        int DependentRow, int DependentColumn,
        int PrerequisiteRow, int PrerequisiteColumn,
        bool RequirementsMet);

    public readonly record struct ConnectorSprite(
        bool Arrow, LogicalRect Rect, Vector2 Uv0, Vector2 Uv1);

    public const float FrameWidth = 384f;
    public const float FrameHeight = 512f;
    public const int MaximumTalentTiers = 8;
    public const int TalentColumns = 4;
    public const int MaximumTalents = 20;
    public const float TalentPitch = 63f;
    public const float TalentButtonSize = 37f;
    public const float ConnectorSize = 32f;
    public const float ScrollChildHeight = 504f;
    public const float ScrollStep = 20f;
    public const float ScrollMaximum = ScrollChildHeight - 332f;
    public const float TabOverlap = 15f;

    public const string TopLeftArt =
        @"Interface\PaperDollInfoFrame\UI-Character-General-TopLeft";
    public const string TopRightArt =
        @"Interface\PaperDollInfoFrame\UI-Character-General-TopRight";
    public const string BottomLeftArt =
        @"Interface\TalentFrame\UI-TalentFrame-BotLeft";
    public const string BottomRightArt =
        @"Interface\TalentFrame\UI-TalentFrame-BotRight";
    public const string BranchArt = @"Interface\TalentFrame\UI-TalentBranches";
    public const string ArrowArt = @"Interface\TalentFrame\UI-TalentArrows";

    public static readonly LogicalRect Portrait = new(7, 6, 60, 60);
    public static readonly LogicalRect Frame = new(0, 0, FrameWidth, FrameHeight);
    public static readonly LogicalRect ShellTopLeft = new(2, 1, 256, 256);
    public static readonly LogicalRect ShellTopRight = new(258, 1, 128, 256);
    public static readonly LogicalRect ShellBottomLeft = new(2, 257, 256, 256);
    public static readonly LogicalRect ShellBottomRight = new(258, 257, 128, 256);
    public static readonly LogicalRect TreeTopLeft = new(23, 77, 256, 256);
    public static readonly LogicalRect TreeTopRight = new(279, 77, 64, 256);
    public static readonly LogicalRect TreeBottomLeft = new(23, 333, 256, 128);
    public static readonly LogicalRect TreeBottomRight = new(279, 333, 64, 128);
    public static readonly LogicalRect PointsBorder = new(75, 48, 264, 20);
    public static readonly LogicalRect ScrollFrame = new(23, 77, 296, 332);
    public static readonly LogicalRect ScrollBackgroundTop = new(317, 72, 31, 256);
    public static readonly LogicalRect ScrollBackgroundBottom = new(317, 305, 31, 106);
    public static readonly LogicalRect ScrollTrack = new(325, 93, 16, 300);
    public static readonly LogicalRect ScrollUp = new(325, 77, 16, 16);
    public static readonly LogicalRect ScrollDown = new(325, 393, 16, 16);
    public static readonly LogicalRect FirstTab = new(15, 434, 0, 32);
    public static readonly LogicalRect ResetButton = new(95, 409, 120, 22);
    public static readonly LogicalRect CloseButton = new(265, 409, 80, 22);
    public static readonly LogicalRect CloseX = new(324, 9, 32, 32);
    public static readonly Vector2 PortraitUvMin = new(0, 1);
    public static readonly Vector2 PortraitUvMax = new(1, 0);
    public static readonly TextureSlice ScrollBackgroundTopSlice = new(
        ScrollBackgroundTop, Vector2.Zero, new(.484375f, 1));
    public static readonly TextureSlice ScrollBackgroundBottomSlice = new(
        ScrollBackgroundBottom, new(.515625f, 0), new(1, .4140625f));
    public static readonly Vector2 ScrollControlUvMin = new(.25f, .25f);
    public static readonly Vector2 ScrollControlUvMax = new(.75f, .75f);

    // TalentFrameTitleText TOP(0,-18), expressed as the center of its 12px em box.
    public static readonly Vector2 TitleCenter = new(192f, 24f);

    // PointsMiddle TOP is (207,48); SpentPoints TOP adds (0,-5) in y-up FrameXML.
    public const float SpentPointsCenterX = 207f;
    public const float SpentPointsTop = 53f;

    // TalentPointsText BOTTOMRIGHT -> frame BOTTOMLEFT(252,+87).
    public static readonly Vector2 TalentPointsBottomRight = new(252f, 425f);
    public const float TalentPointsLabelGap = 3f;

    public static string SpentPointsPrefix(string treeName) =>
        $"Points spent in {treeName} Talents: ";

    public static LogicalRect TalentButton(int row, int column) => new(
        ScrollFrame.X + 35 + Math.Clamp(column, 0, TalentColumns - 1) * TalentPitch,
        ScrollFrame.Y + 20 + Math.Clamp(row, 0, MaximumTalentTiers - 1) * TalentPitch,
        TalentButtonSize, TalentButtonSize);

    public static LogicalRect TalentSlot(int row, int column)
    {
        LogicalRect button = TalentButton(row, column);
        return new(button.X - 13.5f, button.Y - 13.5f, 64, 64);
    }

    public static LogicalRect TalentNormalRing(int row, int column)
    {
        LogicalRect button = TalentButton(row, column);
        // UI-Quickslot2 is centered on the button with the authored y-up -1 offset.
        return new(button.X - 13.5f, button.Y - 12.5f, 64, 64);
    }

    public static LogicalRect TalentRankBorder(int row, int column)
    {
        LogicalRect button = TalentButton(row, column);
        return new(button.X + 21, button.Y + 21, 32, 32);
    }

    // TalentButtonTemplate OnEnter uses ANCHOR_RIGHT: GameTooltip BOTTOMLEFT to TOPRIGHT.
    public static TooltipSeat TalentTooltipSeat(Vector2 buttonMin, Vector2 buttonSize) =>
        new(buttonMin + Vector2.UnitX * buttonSize.X, Vector2.UnitY);

    public static float ClampScroll(float value) => Math.Clamp(value, 0, ScrollMaximum);

    public static float WheelScroll(float value, float wheel) =>
        ClampScroll(value - Math.Sign(wheel) * ScrollStep);

    public static float ArrowScroll(float value, bool up) =>
        ClampScroll(value + (up ? -ScrollStep : ScrollStep));

    public static float ScrollKnobY(float value) =>
        ScrollTrack.Y + ClampScroll(value) / ScrollMaximum *
            (ScrollTrack.Height - ConnectorSize / 2f);

    public static float ScrollFromKnob(float mouseY) => ClampScroll(
        (mouseY - ScrollTrack.Y - ConnectorSize / 4f) /
        (ScrollTrack.Height - ConnectorSize / 2f) * ScrollMaximum);

    public static Vector2 ScrollOffset(float scroll, float scale) =>
        new(0, -ClampScroll(scroll) * scale);

    public static LogicalRect ScrollKnob(float scroll) =>
        new(ScrollTrack.X, ScrollKnobY(scroll), ScrollTrack.Width, ScrollTrack.Width);

    public static Vector2 SpentTextTop(Vector2 origin, float scale,
        float prefixWidth, float valueWidth) => new(
            origin.X + SpentPointsCenterX * scale - (prefixWidth + valueWidth) * .5f,
            origin.Y + SpentPointsTop * scale);

    public static Vector2 SpentValueTop(Vector2 spentTextTop, float prefixWidth) =>
        new(spentTextTop.X + prefixWidth, spentTextTop.Y);

    public static Vector2 TabMinimum(Vector2 origin, float logicalX, float scale) =>
        new(origin.X + logicalX * scale, origin.Y + FirstTab.Y * scale);

    /// <summary>
    /// Port of TalentFrame_DrawLines plus the branch/arrow pool paint. Rectangles are in
    /// TalentFrame logical coordinates before vertical-scroll subtraction.
    /// </summary>
    public static ConnectorSprite[] BuildConnectors(
        IEnumerable<(int Row, int Column)> occupiedTalents,
        IEnumerable<DependencyRoute> routes)
    {
        var nodes = new Node[MaximumTalentTiers, TalentColumns];
        for (int row = 0; row < MaximumTalentTiers; row++)
            for (int column = 0; column < TalentColumns; column++)
                nodes[row, column] = new Node();

        foreach ((int row, int column) in occupiedTalents)
            if (InGrid(row, column)) nodes[row, column].Occupied = true;

        foreach (DependencyRoute route in routes)
        {
            if (!InGrid(route.DependentRow, route.DependentColumn) ||
                !InGrid(route.PrerequisiteRow, route.PrerequisiteColumn) ||
                route.DependentRow < route.PrerequisiteRow)
                continue;
            DrawRoute(nodes, route, route.RequirementsMet ? 1 : -1);
        }

        var sprites = new List<ConnectorSprite>(60);
        int branches = 0, arrows = 0;
        bool ignoreUp = false;
        for (int row = 0; row < MaximumTalentTiers; row++)
        {
            for (int column = 0; column < TalentColumns; column++)
            {
                Node node = nodes[row, column];
                LogicalRect button = TalentButton(row, column);
                // FrameXML's (+2,-2) TOPLEFT inset becomes (+2,+2) in screen-y coordinates.
                float x = button.X + 2, y = button.Y + 2;

                void Branch(string kind, int state, float bx, float by)
                {
                    if (state == 0 || branches++ >= 30) return;
                    (Vector2 uv0, Vector2 uv1) = BranchUv(kind, state);
                    sprites.Add(new(false, new(bx, by, ConnectorSize, ConnectorSize), uv0, uv1));
                }

                void Arrow(string kind, int state, float ax, float ay)
                {
                    if (state == 0 || arrows++ >= 30) return;
                    (Vector2 uv0, Vector2 uv1) = ArrowUv(kind, state);
                    sprites.Add(new(true, new(ax, ay, ConnectorSize, ConnectorSize), uv0, uv1));
                }

                if (node.Occupied)
                {
                    if (node.Up != 0)
                    {
                        if (!ignoreUp) Branch("up", node.Up, x, y - 32);
                        else ignoreUp = false;
                    }
                    if (node.Down != 0) Branch("down", node.Down, x, y + 31);
                    if (node.Left != 0) Branch("left", node.Left, x - 32, y);
                    if (node.Right != 0)
                    {
                        Node? next = column + 1 < TalentColumns ? nodes[row, column + 1] : null;
                        bool grayOverride = next is { Left: not 0, Down: < 0 };
                        int state = grayOverride ? next!.Down : node.Right;
                        Branch("right", state, x + (grayOverride ? 32 : 33), y);
                    }
                    Arrow("right", node.RightArrow, x + 21, y);
                    Arrow("left", node.LeftArrow, x - 21, y);
                    Arrow("top", node.TopArrow, x, y - 21);
                }
                else if (node.Up != 0 && node.Left != 0 && node.Right != 0)
                    Branch("tup", node.Up, x, y);
                else if (node.Down != 0 && node.Left != 0 && node.Right != 0)
                    Branch("tdown", node.Down, x, y);
                else if (node.Left != 0 && node.Down != 0)
                {
                    Branch("topright", node.Left, x, y);
                    Branch("down", node.Down, x, y + 32);
                }
                else if (node.Left != 0 && node.Up != 0)
                    Branch("bottomright", node.Left, x, y);
                else if (node.Left != 0 && node.Right != 0)
                {
                    Branch("right", node.Right, x + 32, y);
                    Branch("left", node.Left, x + 1, y);
                }
                else if (node.Right != 0 && node.Down != 0)
                {
                    Branch("topleft", node.Right, x, y);
                    Branch("down", node.Down, x, y + 32);
                }
                else if (node.Right != 0 && node.Up != 0)
                    Branch("bottomleft", node.Right, x, y);
                else if (node.Up != 0 && node.Down != 0)
                {
                    Branch("up", node.Up, x, y);
                    Branch("down", node.Down, x, y + 32);
                    ignoreUp = true;
                }
            }
        }
        return [.. sprites];
    }

    private static void DrawRoute(Node[,] nodes, DependencyRoute route, int state)
    {
        int buttonRow = route.DependentRow, buttonColumn = route.DependentColumn;
        int row = route.PrerequisiteRow, column = route.PrerequisiteColumn;
        Node cell = nodes[buttonRow, buttonColumn];

        if (buttonColumn == column)
        {
            if (OccupiedDown(nodes, column, row + 1, buttonRow - 1)) return;
            for (int i = row; i < buttonRow; i++)
            {
                if (i + 1 < buttonRow) LinkDown(nodes, i, column, state);
                else nodes[i, column].Down = state;
            }
            cell.TopArrow = state;
            return;
        }

        if (buttonRow == row)
        {
            int left = Math.Min(buttonColumn, column), right = Math.Max(buttonColumn, column);
            if (OccupiedAcross(nodes, row, left + 1, right - 1)) return;
            for (int i = left; i < right; i++) LinkRight(nodes, row, i, state);
            PointArrowSideways(cell, buttonColumn, column, state);
            return;
        }

        int routeLeft = Math.Min(buttonColumn, column);
        int routeRight = Math.Max(buttonColumn, column);
        int from = routeLeft, to = routeRight;
        if (routeLeft == column) from++;
        else to--;
        if (!OccupiedAcross(nodes, row, from, to))
        {
            nodes[row, buttonColumn].Down = state;
            cell.Up = state;
            for (int i = row; i < buttonRow; i++) LinkDown(nodes, i, buttonColumn, state);
            for (int i = routeLeft; i < routeRight; i++) LinkRight(nodes, row, i, state);
            cell.TopArrow = state;
            return;
        }

        from = routeLeft;
        to = routeRight;
        if (routeLeft == buttonColumn) from++;
        else to--;
        if (OccupiedAcross(nodes, buttonRow, from, to)) return;
        for (int i = row; i < buttonRow; i++)
        {
            nodes[i, column].Up = state;
            nodes[i + 1, column].Down = state;
        }
        PointArrowSideways(cell, buttonColumn, column, state);
    }

    private static bool InGrid(int row, int column) =>
        row is >= 0 and < MaximumTalentTiers && column is >= 0 and < TalentColumns;

    private static void LinkDown(Node[,] nodes, int row, int column, int state)
    {
        nodes[row, column].Down = state;
        nodes[row + 1, column].Up = state;
    }

    private static void LinkRight(Node[,] nodes, int row, int column, int state)
    {
        nodes[row, column].Right = state;
        nodes[row, column + 1].Left = state;
    }

    private static bool OccupiedAcross(Node[,] nodes, int row, int from, int to)
    {
        for (int column = from; column <= to; column++)
            if (InGrid(row, column) && nodes[row, column].Occupied) return true;
        return false;
    }

    private static bool OccupiedDown(Node[,] nodes, int column, int from, int to)
    {
        for (int row = from; row <= to; row++)
            if (InGrid(row, column) && nodes[row, column].Occupied) return true;
        return false;
    }

    private static void PointArrowSideways(Node cell, int buttonColumn, int column, int state)
    {
        if (buttonColumn < column) cell.RightArrow = state;
        else cell.LeftArrow = state;
    }

    private static (Vector2 Uv0, Vector2 Uv1) BranchUv(string kind, int state)
    {
        (int cell, bool flip, int rowHeight) = kind switch
        {
            "down" => (0, false, 31),
            "up" => (1, false, 31),
            "left" or "right" => (2, false, 32),
            "bottomright" => (3, false, 32),
            "bottomleft" => (3, true, 32),
            "topright" => (4, false, 32),
            "topleft" => (4, true, 32),
            "tdown" => (5, false, 32),
            _ => (6, false, 32), // tup
        };
        float u0 = cell * 33f / 256f, u1 = (cell * 33f + 32f) / 256f;
        if (flip) (u0, u1) = (u1, u0);
        float v = rowHeight / 64f;
        return state > 0
            ? (new(u0, 0), new(u1, v))
            : (new(u0, 1 - v), new(u1, 1));
    }

    private static (Vector2 Uv0, Vector2 Uv1) ArrowUv(string kind, int state)
    {
        int cell = kind == "top" ? 0 : 1;
        bool flip = kind == "right";
        float u0 = cell * .5f, u1 = u0 + .5f;
        if (flip) (u0, u1) = (u1, u0);
        return state > 0
            ? (new(u0, 0), new(u1, .5f))
            : (new(u0, .5f), new(u1, 1));
    }

    private sealed class Node
    {
        public bool Occupied;
        public int Up, Down, Left, Right;
        public int TopArrow, LeftArrow, RightArrow;
    }
}
