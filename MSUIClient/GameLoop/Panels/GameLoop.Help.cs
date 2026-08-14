using System.Numerics;
using ImGuiNET;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _helpOpen;
    private byte _helpTicketType = 1;
    private string _helpTicketText = "";
    private string _helpTicketStatus = "No open ticket.";
    private int _helpPage;

    private void OpenHelp()
    {
        _helpOpen = true; _helpPage = 0; _net?.GmTicketSystemStatus(); _net?.GmTicketGet();
    }

    private void ApplyHelpTicketPacket(Op opcode, byte[] body)
    {
        var r = new PacketReader(body);
        if (opcode == Op.SMSG_GMTICKET_GETTICKET)
        {
            uint status = r.ReadU32();
            if (status == 6 && r.Remaining > 0)
            {
                _helpTicketText = r.ReadCString(); _helpTicketType = r.ReadU8();
                _helpTicketStatus = "Ticket open.";
            }
            else _helpTicketStatus = "No open ticket.";
        }
        else
        {
            uint result = r.Remaining >= 4 ? r.ReadU32() : uint.MaxValue;
            _helpTicketStatus = result == 0 ? "Request completed." : $"Request result: {result}";
            if (opcode != Op.SMSG_GMTICKET_SYSTEMSTATUS) _net?.GmTicketGet();
        }
    }

    private void DrawHelpFrame()
    {
        if (!_helpOpen || _gameplayArt is null) return;
        float s = GameplayUiScale(); Vector2 logicalDisplay = ImGui.GetIO().DisplaySize / s;
        Vector2 logicalOrigin = new((logicalDisplay.X - 640) * .5f, (logicalDisplay.Y - 512) * .5f);
        if (!BeginVanillaWindow("##help", logicalOrigin, new Vector2(640, 512),
                out ImDrawListPtr dl, out Vector2 origin, out s)) { ImGui.End(); return; }
        string[] paths =
        [
            @"Interface\HelpFrame\HelpFrame-TopLeft", @"Interface\HelpFrame\HelpFrame-Top",
            @"Interface\HelpFrame\HelpFrame-TopRight", @"Interface\HelpFrame\HelpFrame-BotLeft",
            @"Interface\HelpFrame\HelpFrame-Bottom", @"Interface\HelpFrame\HelpFrame-BotRight"
        ];
        Vector2[] offsets = [new(0,0),new(256,0),new(512,0),new(0,256),new(256,256),new(512,256)];
        Vector2[] sizes = [new(256),new(256),new(128,256),new(256),new(256),new(128,256)];
        for (int i = 0; i < paths.Length; i++) DrawArt(dl, paths[i], origin + offsets[i] * s, sizes[i], s);
        DrawArt(dl, @"Interface\DialogFrame\UI-DialogBox-Header",
            origin + new Vector2(140, -12) * s, new Vector2(336, 64), s);
        DrawCenteredText(dl, origin + new Vector2(308, 18) * s, "Help Request", 14f * s, VanillaGold);
        if (_helpPage == 0) DrawHelpHome(dl, origin, s);
        else if (_helpPage == 1) DrawHelpCategories(dl, origin, s);
        else DrawOpenTicket(dl, origin, s);
        DrawImageButton(dl, "##help-close", origin + new Vector2(566, 3) * s, new Vector2(32) * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up", @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) _helpOpen = false;
        ImGui.End();
    }

    private void DrawHelpHome(ImDrawListPtr dl, Vector2 origin, float s)
    {
        dl.AddText(ImGui.GetFont(), 18f * s, origin + new Vector2(42, 58) * s, VanillaGold,
            "Petition a Game Master");
        DrawWrappedText(dl,
            "Before submitting a request, please check whether one of the options below can solve the problem immediately.",
            origin + new Vector2(42, 92) * s, 550, 11f * s, s, 0xffffffff);
        string[] headings = ["Character stuck", "Player behavior", "Gameplay or item issue"];
        string[] descriptions =
        [
            "Use the Stuck option if your character cannot move or leave the current location.",
            "Use the harassment category for abusive language or disruptive player behavior.",
            "A Game Master ticket can cover quests, items, NPCs, guilds, and world problems."
        ];
        for (int i = 0; i < headings.Length; i++)
        {
            dl.AddText(ImGui.GetFont(), 12f * s, origin + new Vector2(54, 145 + i * 72) * s, VanillaGold, headings[i]);
            DrawWrappedText(dl, descriptions[i], origin + new Vector2(70, 166 + i * 72) * s,
                500, 10f * s, s, 0xffffffff);
        }
        if (VanillaButton(dl, "##help-open-issues", "Open a Ticket",
                origin + new Vector2(213, 405) * s, new Vector2(214, 24), s)) _helpPage = 1;
        if (VanillaButton(dl, "##help-home-cancel", "Cancel",
                origin + new Vector2(270, 447) * s, new Vector2(100, 22), s)) _helpOpen = false;
    }

    private void DrawHelpCategories(ImDrawListPtr dl, Vector2 origin, float s)
    {
        DrawCenteredText(dl, origin + new Vector2(320, 62) * s,
            "Select the category that best describes the issue", 12f * s, 0xffffffff);
        int type = _helpTicketType;
        string[] types = ["Stuck", "Behavior / Harassment", "Guild", "Item", "Environment", "Other"];
        for (int i = 0; i < types.Length; i++)
            if (VanillaButton(dl, $"##help-category-{i}", types[i],
                    origin + new Vector2(86 + (i % 2) * 250, 105 + (i / 2) * 74) * s,
                    new Vector2(218, 52), s))
            { _helpTicketType = (byte)(i + 1); _helpPage = 2; }
        if (VanillaButton(dl, "##help-category-back", "Back", origin + new Vector2(213, 447) * s,
                new Vector2(100, 22), s)) _helpPage = 0;
        if (VanillaButton(dl, "##help-category-cancel", "Cancel", origin + new Vector2(327, 447) * s,
                new Vector2(100, 22), s)) _helpOpen = false;
    }

    private void DrawOpenTicket(ImDrawListPtr dl, Vector2 origin, float s)
    {
        string[] types = ["Stuck", "Behavior / Harassment", "Guild", "Item", "Environment", "Other"];
        dl.AddText(ImGui.GetFont(), 11f * s, origin + new Vector2(44, 66) * s, 0xffffffff,
            $"Category: {types[Math.Clamp(_helpTicketType - 1, 0, types.Length - 1)]}");
        DrawWrappedText(dl,
            "Describe the problem in detail. A Game Master can review this ticket on the server.",
            origin + new Vector2(44, 91) * s, 548, 10f * s, s, 0xffffffff);
        VanillaInputText(dl,"##ticket-text",ref _helpTicketText,2047,
            origin+new Vector2(44,125)*s,new Vector2(548,265),s,true);
        dl.AddText(ImGui.GetFont(), 10f * s, origin + new Vector2(44, 405) * s, 0xffaaaaaa, _helpTicketStatus);
        bool hasText = !string.IsNullOrWhiteSpace(_helpTicketText);
        if (VanillaButton(dl, "##ticket-back", "Back", origin + new Vector2(251, 441) * s,
                new Vector2(100, 22), s)) _helpPage = 1;
        if (VanillaButton(dl, "##ticket-submit", "Submit", origin + new Vector2(365, 441) * s,
                new Vector2(100, 22), s, hasText))
        {
            uint map = _net?.Player?.Map ?? 0; Vector3 pos = _controller?.Position ?? Vector3.Zero;
            _net?.GmTicketCreate(_helpTicketType, map, pos, _helpTicketText);
        }
        if (VanillaButton(dl, "##ticket-delete", "Delete Ticket", origin + new Vector2(475, 441) * s,
                new Vector2(110, 22), s)) _net?.GmTicketDelete();
    }
}
