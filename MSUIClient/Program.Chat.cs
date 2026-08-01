using System.Numerics;
using ImGuiNET;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly List<string> _chatMessages = [];
    private int _chatScroll;

    private void AddChatMessage(string text)
    {
        string cleaned = string.Join(' ', text.Replace('|', ' ').Split(' ',
            StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (cleaned.Length == 0) return;
        if (_chatMessages.Count == 128) _chatMessages.RemoveAt(0);
        _chatMessages.Add(cleaned);
        _chatScroll = 0;
    }

    private void DrawChatFrame()
    {
        if (_gameplayArt is null) return;
        float s = GameplayUiScale();
        Vector2 logicalDisplay = ImGui.GetIO().DisplaySize / s;
        Vector2 root = new(32, logicalDisplay.Y - 95 - 120);
        Vector2 rootPx = root * s;
        ImDrawListPtr dl = ImGui.GetBackgroundDrawList();

        if (_uiParityArmed && _uiParityPanel == "chat-frame")
        {
            BeginUiParityFrame(rootPx, s);
            CollectUiParity("ChatFrame1", "ScrollingMessageFrame", rootPx, new Vector2(430, 120) * s,
                parent: "", point: "BOTTOMLEFT", offsetX: "32", offsetY: "95", strata: "BACKGROUND");
        }

        DrawChatTexture(dl, root + new Vector2(-2, -3), new(430, 120),
            @"Interface\ChatFrame\ChatFrameBackground", Vector2.Zero, Vector2.One);
        if (_uiParityArmed && _uiParityPanel == "chat-frame")
        {
            CollectUiParity("ChatFrame1/FontString", "FontString", rootPx, new Vector2(430, 120) * s,
                parent: "ChatFrame1", font: "ChatFontNormal", fontPath: @"Fonts\ARIALN.TTF",
                fontSize: "14", color: "#FFFFFFFF", strata: "BACKGROUND");
            CollectUiParity("ChatFrame1Background", "Texture", (root + new Vector2(-2, -3)) * s,
                new Vector2(430, 120) * s, parent: "ChatFrame1", point: "TOPLEFT",
                offsetX: "-2", offsetY: "3", texture: @"Interface\ChatFrame\ChatFrameBackground",
                layer: "BACKGROUND", strata: "BACKGROUND");
        }

        DrawChatMessages(dl, root, s);
        DrawChatScrollButton(dl, root + new Vector2(-32, 30), "ScrollUp", () => _chatScroll++);
        DrawChatScrollButton(dl, root + new Vector2(-32, 62), "ScrollDown", () => _chatScroll = Math.Max(0, _chatScroll - 1));
        DrawChatScrollButton(dl, root + new Vector2(-32, 92), "ScrollEnd", () => _chatScroll = 0);
        DrawChatTab(dl, root, s);
        DrawChatEditBox(dl, root, s);

        if (_uiParityArmed && _uiParityPanel == "chat-frame") MarkUiParityFrameComplete();
    }

    private void DrawChatMessages(ImDrawListPtr dl, Vector2 root, float s)
    {
        float fontSize = 14 * s;
        int last = Math.Max(0, _chatMessages.Count - _chatScroll);
        int first = Math.Max(0, last - 7);
        float y = root.Y + 104;
        for (int i = last - 1; i >= first; i--, y -= 15)
        {
            string line = _chatMessages[i];
            if (line.Length > 78) line = line[..78];
            dl.AddText(ImGui.GetFont(), fontSize, new Vector2(root.X + 2, y) * s,
                0xffffffff, line);
        }
    }

    private void DrawChatScrollButton(ImDrawListPtr dl, Vector2 logicalMin, string direction, Action click)
    {
        float s = GameplayUiScale();
        string texture = $@"Interface\ChatFrame\UI-ChatIcon-{direction}-Up";
        Vector2 min = logicalMin * s, size = new Vector2(32) * s;
        uint handle = _gameplayArt?.Handle(texture) ?? 0;
        if (handle != 0) dl.AddImage((nint)handle, min, min + size);
        if (ImGui.IsMouseHoveringRect(min, min + size, false) && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) click();
        if (!_uiParityArmed || _uiParityPanel != "chat-frame") return;
        string name = direction switch { "ScrollUp" => "ChatFrame1UpButton", "ScrollDown" => "ChatFrame1DownButton", _ => "ChatFrame1BottomButton" };
        string point = direction == "ScrollUp" ? "BOTTOM" : direction == "ScrollDown" ? "BOTTOM" : "BOTTOMLEFT";
        string relative = direction == "ScrollUp" ? "ChatFrame1DownButton" : direction == "ScrollDown" ? "ChatFrame1BottomButton" : "";
        string relativePoint = direction == "ScrollUp" || direction == "ScrollDown" ? "TOP" : "";
        string ox = direction == "ScrollEnd" ? "-32" : direction == "ScrollDown" ? "0" : "";
        string oy = direction == "ScrollEnd" ? "-4" : direction == "ScrollDown" ? "-2" : "";
        CollectUiParity(name, "Button", min, size, parent: "ChatFrame1", point: point,
            relativeTo: relative, relativePoint: relativePoint, offsetX: ox, offsetY: oy,
            texture: texture, strata: "BACKGROUND");
        CollectUiParity(name + "/NormalTexture", "NormalTexture", min, size, parent: name,
            texture: texture, strata: "BACKGROUND");
    }

    private void DrawChatTab(ImDrawListPtr dl, Vector2 root, float s)
    {
        Vector2 min = root + new Vector2(0, -35);
        string art = @"Interface\ChatFrame\ChatFrameTab";
        DrawChatTexture(dl, min, new(16, 32), art, new(0, 0), new(.25f, 1));
        DrawChatTexture(dl, min + new Vector2(16, 0), new(44, 32), art, new(.25f, 0), new(.75f, 1));
        DrawChatTexture(dl, min + new Vector2(60, 0), new(16, 32), art, new(.75f, 0), Vector2.One);
        float fontSize = 10 * s;
        dl.AddText(ImGui.GetFont(), fontSize, (min + new Vector2(20, 10)) * s, UiGoldU32(), "General");
    }

    private void DrawChatEditBox(ImDrawListPtr dl, Vector2 root, float s)
    {
        Vector2 min = root + new Vector2(-5, 122);
        DrawChatTexture(dl, min, new(256, 32), @"Interface\ChatFrame\UI-ChatInputBorder-Left", Vector2.Zero, Vector2.One);
        DrawChatTexture(dl, min + new Vector2(256, 0), new(168, 32), @"Interface\ChatFrame\UI-ChatInputBorder-Right",
            Vector2.Zero, new(.9375f, 1));
        DrawChatTexture(dl, min + new Vector2(424, 0), new(16, 32), @"Interface\ChatFrame\UI-ChatInputBorder-Right",
            new(.9375f, 0), Vector2.One);
        dl.AddText(ImGui.GetFont(), 14 * s, (min + new Vector2(13, 8)) * s, 0xffffffff, "Say:");
    }

    private void DrawChatTexture(ImDrawListPtr dl, Vector2 logicalMin, Vector2 logicalSize,
        string texture, Vector2 uv0, Vector2 uv1)
    {
        uint handle = _gameplayArt?.Handle(texture) ?? 0;
        if (handle == 0) return;
        float s = GameplayUiScale();
        Vector2 min = logicalMin * s;
        uint tint = texture.EndsWith("ChatFrameBackground", StringComparison.OrdinalIgnoreCase)
            ? 0x00ffffffu : 0xffffffffu;
        dl.AddImage((nint)handle, min, min + logicalSize * s, uv0, uv1, tint);
    }
}
