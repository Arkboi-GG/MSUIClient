using System.Numerics;
using ImGuiNET;

namespace MSUIClient;

public sealed partial class GameLoop
{
    // ImGui packs colors as ABGR (IM_COL32), not CSS/ARGB. Vanilla's normal
    // gold is RGB FF D1 00, hence the seemingly reversed literal below.
    private const uint VanillaGold = 0xff00d1ff;
    private static readonly ImGuiWindowFlags VanillaWindowFlags =
        ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
        ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoBringToFrontOnFocus;

    private Vector2 VanillaPanelOrigin(float scale, float x = 0, float y = 104) =>
        new(x * scale, y * scale);

    private bool BeginVanillaWindow(string id, Vector2 logicalOrigin, Vector2 logicalSize,
        out ImDrawListPtr draw, out Vector2 origin, out float scale)
    {
        scale = GameplayUiScale();
        origin = logicalOrigin * scale;
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(logicalSize * scale, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        bool open = ImGui.Begin(id, VanillaWindowFlags);
        draw = ImGui.GetWindowDrawList();
        return open;
    }

    private bool VanillaButton(ImDrawListPtr draw, string id, string caption,
        Vector2 min, Vector2 logicalSize, float scale, bool enabled = true)
    {
        Vector2 size = logicalSize * scale;
        ImGui.SetCursorScreenPos(min);
        if (!enabled) ImGui.BeginDisabled();
        ImGui.InvisibleButton(id, size);
        bool clicked = enabled && ImGui.IsItemClicked();
        bool active = enabled && ImGui.IsItemActive();
        bool hovered = enabled && ImGui.IsItemHovered();
        if (!enabled) ImGui.EndDisabled();

        string texture = !enabled ? @"Interface\Buttons\UI-Panel-Button-Disabled" :
            active ? @"Interface\Buttons\UI-Panel-Button-Down" :
            @"Interface\Buttons\UI-Panel-Button-Up";
        uint art = _gameplayArt?.Handle(texture) ?? 0;
        if (art != 0)
            draw.AddImage((nint)art, min, min + size, Vector2.Zero, new Vector2(.625f, .6875f));
        if (hovered)
        {
            uint hi = _gameplayArt?.AdditiveHandle(@"Interface\Buttons\UI-Panel-Button-Highlight") ?? 0;
            if (hi != 0)
                draw.AddImage((nint)hi, min, min + size, Vector2.Zero,
                    new Vector2(.625f, .6875f));
        }
        DrawCenteredText(draw, min + size * .5f + new Vector2(0, active ? scale : 0),
            caption, 10f * scale, enabled ? VanillaGold : 0xff777777);
        return clicked;
    }

    private bool VanillaListRow(ImDrawListPtr draw, string id, Vector2 min,
        Vector2 logicalSize, float scale, string text, bool selected, uint color = 0xffffffff,
        string? iconPath = null)
    {
        Vector2 size = logicalSize * scale;
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton(id, size);
        bool hovered = ImGui.IsItemHovered();
        if (selected || hovered)
        {
            uint highlight = _gameplayArt?.Handle(@"Interface\Buttons\UI-Listbox-Highlight2") ?? 0;
            if (highlight != 0)
                draw.AddImage((nint)highlight, min, min + size, Vector2.Zero, Vector2.One,
                    selected ? 0xffffffff : 0x99ffffff);
        }
        float textX = 5f;
        if (!string.IsNullOrWhiteSpace(iconPath))
        {
            uint icon = _gameplayArt?.Handle(iconPath) ?? 0;
            if (icon != 0)
            {
                Vector2 iconMin = min + new Vector2(2) * scale;
                draw.AddImage((nint)icon, iconMin, iconMin + new Vector2(logicalSize.Y - 4) * scale);
                textX = logicalSize.Y + 2;
            }
        }
        draw.AddText(ImGui.GetFont(), 10f * scale,
            min + new Vector2(textX, MathF.Max(1, (logicalSize.Y - 10) * .5f)) * scale,
            color, text);
        return ImGui.IsItemClicked();
    }

    private bool VanillaTab(ImDrawListPtr draw, string id, Vector2 min, string caption,
        float logicalWidth, float scale, bool selected, bool enabled = true)
    {
        Vector2 size = new(logicalWidth, 32);
        string texture = selected
            ? @"Interface\PaperDollInfoFrame\UI-Character-ActiveTab"
            : @"Interface\PaperDollInfoFrame\UI-Character-InActiveTab";
        uint art = _gameplayArt?.Handle(texture) ?? 0;
        if (art != 0)
        {
            // CharacterFrameTabButtonTemplate is a three-piece 128x32 sheet.
            // Stretching the complete sheet (the old implementation) scales both
            // end caps and makes adjacent tabs visibly cut through one another.
            // PanelTemplates_TabResize changes only the 88px middle region.
            float cap = MathF.Min(20f, logicalWidth * .5f);
            float middle = MathF.Max(0f, logicalWidth - cap * 2f);
            Vector2 artMin = min + new Vector2(0, selected ? 5f : 0f) * scale;
            Vector2 artMax = artMin + new Vector2(logicalWidth, 32) * scale;
            draw.AddImage((nint)art, artMin, artMin + new Vector2(cap, 32) * scale,
                new Vector2(0, 0), new Vector2(.15625f, 1));
            if (middle > 0)
                draw.AddImage((nint)art, artMin + new Vector2(cap, 0) * scale,
                    artMax - new Vector2(cap, 0) * scale,
                    new Vector2(.15625f, 0), new Vector2(.84375f, 1));
            draw.AddImage((nint)art, artMax - new Vector2(cap, 0) * scale, artMax,
                new Vector2(.84375f, 0), Vector2.One);
        }
        ImGui.SetCursorScreenPos(min);
        if (!enabled) ImGui.BeginDisabled();
        ImGui.InvisibleButton(id, size * scale);
        bool clicked = enabled && ImGui.IsItemClicked();
        if (!enabled) ImGui.EndDisabled();
        DrawCenteredText(draw, min + new Vector2(logicalWidth * .5f, 14) * scale,
            caption, 10f * scale, !enabled ? 0xff777777 : selected ? 0xffffffff : VanillaGold);
        return clicked;
    }

    // PanelTemplates_TabResize(padding): text width + padding + the two 20px
    // CharacterFrameTabButtonTemplate caps. Lua treats padding=0 as truthy.
    private static float VanillaCharacterTabWidth(string caption, float scale, float padding)
    {
        float textWidth = ImGui.CalcTextSize(caption).X *
            (10f * scale / MathF.Max(1f, ImGui.GetFontSize()));
        return MathF.Ceiling(textWidth / MathF.Max(scale, .001f) + padding + 40f);
    }

    private bool VanillaInsetTab(ImDrawListPtr draw, string id, Vector2 min, string caption,
        float logicalWidth, float scale, bool selected)
    {
        Vector2 size = new(logicalWidth, 32);
        string path = selected ? @"Interface\HelpFrame\HelpFrameTab-Active"
            : @"Interface\HelpFrame\HelpFrameTab-Inactive";
        uint art = _gameplayArt?.Handle(path) ?? 0;
        if (art != 0)
        {
            float cap = MathF.Min(16, logicalWidth * .5f);
            Vector2 artMin = min + new Vector2(0, selected ? 3 : 0) * scale;
            Vector2 artMax = artMin + size * scale;
            draw.AddImage((nint)art, artMin, artMin + new Vector2(cap, 32) * scale,
                Vector2.Zero, new Vector2(.25f, 1));
            draw.AddImage((nint)art, artMin + new Vector2(cap, 0) * scale,
                artMax - new Vector2(cap, 0) * scale,
                new Vector2(.25f, 0), new Vector2(.75f, 1));
            draw.AddImage((nint)art, artMax - new Vector2(cap, 0) * scale, artMax,
                new Vector2(.75f, 0), Vector2.One);
        }
        ImGui.SetCursorScreenPos(min); ImGui.InvisibleButton(id, size * scale);
        DrawCenteredText(draw, min + new Vector2(logicalWidth * .5f, 20) * scale,
            caption, 10 * scale, selected ? 0xffffffff : VanillaGold);
        return ImGui.IsItemClicked();
    }

    private static float VanillaInsetTabWidth(string caption, float scale, float padding = 0)
    {
        float textWidth = ImGui.CalcTextSize(caption).X *
            (10f * scale / MathF.Max(1f, ImGui.GetFontSize()));
        return MathF.Ceiling(textWidth / MathF.Max(scale, .001f) + padding + 32f);
    }

    private bool VanillaCheckButton(ImDrawListPtr draw, string id, Vector2 min,
        string caption, float scale, ref bool value, bool enabled = true)
    {
        Vector2 boxSize = new Vector2(24) * scale;
        ImGui.SetCursorScreenPos(min);
        if (!enabled) ImGui.BeginDisabled();
        ImGui.InvisibleButton(id, new Vector2(24 + MathF.Max(0, caption.Length * 6), 24) * scale);
        bool clicked = enabled && ImGui.IsItemClicked();
        bool active = enabled && ImGui.IsItemActive();
        bool hovered = enabled && ImGui.IsItemHovered();
        if (!enabled) ImGui.EndDisabled();
        if (clicked) value = !value;
        string box = !enabled ? @"Interface\Buttons\UI-CheckBox-Up" : active
            ? @"Interface\Buttons\UI-CheckBox-Down" : @"Interface\Buttons\UI-CheckBox-Up";
        uint art = _gameplayArt?.Handle(box) ?? 0;
        if (art != 0) draw.AddImage((nint)art, min, min + boxSize);
        if (value)
        {
            uint check = _gameplayArt?.Handle(enabled
                ? @"Interface\Buttons\UI-CheckBox-Check"
                : @"Interface\Buttons\UI-CheckBox-Check-Disabled") ?? 0;
            if (check != 0) draw.AddImage((nint)check, min, min + boxSize);
        }
        if (hovered)
        {
            uint hi = _gameplayArt?.AdditiveHandle(@"Interface\Buttons\UI-CheckBox-Highlight") ?? 0;
            if (hi != 0) draw.AddImage((nint)hi, min, min + boxSize);
        }
        draw.AddText(ImGui.GetFont(), 10f * scale, min + new Vector2(27, 7) * scale,
            enabled ? VanillaGold : 0xff777777, caption);
        return clicked;
    }

    private void DrawVanillaScrollBar(ImDrawListPtr draw, string id, Vector2 min,
        float logicalHeight, float scale, int value, int maximum, Action<int> changed)
    {
        Vector2 buttonSize = new Vector2(32) * scale;
        bool canUp = value > 0;
        bool canDown = value < maximum;
        Vector2 upMin = min;
        Vector2 downMin = min + new Vector2(0, logicalHeight - 32) * scale;
        void Arrow(string suffix, Vector2 at, bool enabled, Action click)
        {
            ImGui.SetCursorScreenPos(at);
            if (!enabled) ImGui.BeginDisabled();
            ImGui.InvisibleButton(id + suffix, buttonSize);
            bool pressed = enabled && ImGui.IsItemActive();
            bool hovered = enabled && ImGui.IsItemHovered();
            bool clicked = enabled && ImGui.IsItemClicked();
            if (!enabled) ImGui.EndDisabled();
            string stem = suffix == "-up" ? "UI-ScrollBar-ScrollUpButton" : "UI-ScrollBar-ScrollDownButton";
            string state = !enabled ? "Disabled" : pressed ? "Down" : "Up";
            uint tex = _gameplayArt?.Handle($@"Interface\Buttons\{stem}-{state}") ?? 0;
            if (tex != 0) draw.AddImage((nint)tex, at, at + buttonSize);
            if (hovered)
            {
                uint hi = _gameplayArt?.AdditiveHandle($@"Interface\Buttons\{stem}-Highlight") ?? 0;
                if (hi != 0) draw.AddImage((nint)hi, at, at + buttonSize);
            }
            if (clicked) click();
        }
        Arrow("-up", upMin, canUp, () => changed(value - 1));
        Arrow("-down", downMin, canDown, () => changed(value + 1));

        float trackTop = 30f;
        float trackHeight = MathF.Max(1f, logicalHeight - 60f);
        float fraction = maximum <= 0 ? 0f : Math.Clamp((float)value / maximum, 0f, 1f);
        Vector2 knobMin = min + new Vector2(3, trackTop + fraction * MathF.Max(0, trackHeight - 32)) * scale;
        Vector2 knobSize = new Vector2(24, 32) * scale;
        uint knob = _gameplayArt?.Handle(@"Interface\Buttons\UI-ScrollBar-Knob") ?? 0;
        if (knob != 0) draw.AddImage((nint)knob, knobMin, knobMin + knobSize);
        ImGui.SetCursorScreenPos(min + new Vector2(3, trackTop) * scale);
        ImGui.InvisibleButton(id + "-track", new Vector2(24, trackHeight) * scale);
        if (maximum > 0 && ImGui.IsItemActive())
        {
            float y = ImGui.GetIO().MousePos.Y - (min.Y + trackTop * scale) - knobSize.Y * .5f;
            int next = (int)MathF.Round(Math.Clamp(y / MathF.Max(1, trackHeight * scale - knobSize.Y), 0, 1) * maximum);
            if (next != value) changed(next);
        }
    }

    private void DrawFourPieceShell(ImDrawListPtr draw, Vector2 origin, float scale,
        string topLeft, string topRight, string bottomLeft, string bottomRight)
    {
        DrawArt(draw, topLeft, origin, new Vector2(256), scale);
        DrawArt(draw, topRight, origin + new Vector2(256, 0) * scale,
            new Vector2(128, 256), scale);
        DrawArt(draw, bottomLeft, origin + new Vector2(0, 256) * scale,
            new Vector2(256), scale);
        DrawArt(draw, bottomRight, origin + new Vector2(256, 256) * scale,
            new Vector2(128, 256), scale);
    }

    private void DrawVanillaInputBorder(ImDrawListPtr draw, Vector2 min,
        Vector2 logicalSize, float scale)
    {
        uint border = _gameplayArt?.Handle(@"Interface\Common\Common-Input-Border.blp") ?? 0;
        if (border == 0) return;
        Vector2 size = logicalSize * scale;
        float cap = MathF.Min(8f * scale, size.X * .25f);
        draw.AddImage((nint)border, min, min + new Vector2(cap, size.Y),
            new Vector2(0, 0), new Vector2(.0625f, .625f));
        draw.AddImage((nint)border, min + new Vector2(cap, 0), min + new Vector2(size.X - cap, size.Y),
            new Vector2(.0625f, 0), new Vector2(.9375f, .625f));
        draw.AddImage((nint)border, min + new Vector2(size.X - cap, 0), min + size,
            new Vector2(.9375f, 0), new Vector2(1, .625f));
    }

    private bool VanillaInputText(ImDrawListPtr draw, string id, byte[] buffer,
        Vector2 min, Vector2 logicalSize, float scale)
    {
        DrawVanillaInputBorder(draw, min, logicalSize, scale);
        ImGui.SetCursorScreenPos(min + new Vector2(6, 2) * scale);
        ImGui.SetNextItemWidth((logicalSize.X - 12) * scale);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
        bool changed = ImGui.InputText(id, buffer, (uint)buffer.Length);
        ImGui.PopStyleColor(4);
        return changed;
    }

    private bool VanillaInputText(ImDrawListPtr draw, string id, ref string value, uint capacity,
        Vector2 min, Vector2 logicalSize, float scale, bool multiline = false)
    {
        DrawVanillaInputBorder(draw, min, logicalSize, scale);
        ImGui.SetCursorScreenPos(min + new Vector2(6, 3) * scale);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
        bool changed;
        if (multiline)
            changed = ImGui.InputTextMultiline(id, ref value, capacity,
                (logicalSize - new Vector2(12, 6)) * scale);
        else
        {
            ImGui.SetNextItemWidth((logicalSize.X - 12) * scale);
            changed = ImGui.InputText(id, ref value, capacity);
        }
        ImGui.PopStyleColor(4);
        return changed;
    }

    private bool VanillaInputTextMultiline(ImDrawListPtr draw, string id, byte[] buffer,
        Vector2 min, Vector2 logicalSize, float scale)
    {
        string value = ReadBuffer(buffer);
        bool changed = VanillaInputText(draw, id, ref value, (uint)buffer.Length,
            min, logicalSize, scale, true);
        if (changed) WriteBuffer(buffer, value);
        return changed;
    }

    private bool VanillaInputInt(ImDrawListPtr draw, string id, ref int value,
        Vector2 min, Vector2 logicalSize, float scale)
    {
        DrawVanillaInputBorder(draw, min, logicalSize, scale);
        ImGui.SetCursorScreenPos(min + new Vector2(6, 2) * scale);
        ImGui.SetNextItemWidth((logicalSize.X - 12) * scale);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
        bool changed = ImGui.InputInt(id, ref value, 0, 0);
        ImGui.PopStyleColor(4);
        return changed;
    }

    private float DrawWrappedText(ImDrawListPtr draw, string text, Vector2 min,
        float logicalWidth, float fontSize, float scale, uint color, int maxLines = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        float width = logicalWidth * scale;
        float lineHeight = MathF.Ceiling(fontSize * 1.18f);
        int lines = 0;
        foreach (string paragraph in text.Replace("\r", "").Split('\n'))
        {
            string current = "";
            foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                Vector2 measured = ImGui.CalcTextSize(candidate) *
                    (fontSize / MathF.Max(1f, ImGui.GetFontSize()));
                if (current.Length > 0 && measured.X > width)
                {
                    draw.AddText(ImGui.GetFont(), fontSize, min + new Vector2(0, lines * lineHeight), color, current);
                    if (++lines >= maxLines) return lines * lineHeight;
                    current = word;
                }
                else current = candidate;
            }
            if (current.Length > 0)
            {
                draw.AddText(ImGui.GetFont(), fontSize, min + new Vector2(0, lines * lineHeight), color, current);
                if (++lines >= maxLines) return lines * lineHeight;
            }
        }
        return lines * lineHeight;
    }
}
