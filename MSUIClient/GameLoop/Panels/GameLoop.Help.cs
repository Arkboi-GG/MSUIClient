using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
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
        float s = GameplayUiScale();
        Vector2 logicalDisplay = ImGui.GetIO().DisplaySize / s;
        HelpFrameUiLaw.LogicalRect frame = HelpFrameUiLaw.Frame(logicalDisplay);
        if (!BeginVanillaWindow("##help", frame.Min, frame.Size,
                out ImDrawListPtr dl, out Vector2 origin, out s)) { ImGui.End(); return; }
        foreach (HelpFrameUiLaw.ArtSeat seat in HelpFrameUiLaw.Art)
            DrawArt(dl, seat.Path, origin + seat.Rect.Min * s, seat.Rect.Size, s);
        DrawArt(dl, @"Interface\DialogFrame\UI-DialogBox-Header",
            origin + HelpFrameUiLaw.Header.Min * s, HelpFrameUiLaw.Header.Size, s);
        DrawCenteredText(dl, origin + HelpFrameUiLaw.TitleCenter * s,
            "Help Request", 14f * s, VanillaGold);
        if (_helpPage == 0) DrawHelpHome(dl, origin, s);
        else if (_helpPage == 1) DrawHelpCategories(dl, origin, s);
        else DrawOpenTicket(dl, origin, s);
        DrawImageButton(dl, "##help-close", origin + HelpFrameUiLaw.Close.Min * s,
            HelpFrameUiLaw.Close.Size * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up", @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) _helpOpen = false;
        ImGui.End();
    }

    private void DrawHelpHome(ImDrawListPtr dl, Vector2 origin, float s)
    {
        GameText.DrawPlain(dl, "Petition a Game Master",
            origin + HelpFrameUiLaw.HomeHeading * s, 18f, s, VanillaGold);
        DrawWrappedText(dl,
            "Before submitting a request, please check whether one of the options below can solve the problem immediately.",
            origin + HelpFrameUiLaw.HomeIntroduction.Min * s,
            HelpFrameUiLaw.HomeIntroduction.Width, 11f * s, s, 0xffffffff);
        string[] headings = ["Character stuck", "Player behavior", "Gameplay or item issue"];
        string[] descriptions =
        [
            "Use the Stuck option if your character cannot move or leave the current location.",
            "Use the harassment category for abusive language or disruptive player behavior.",
            "A Game Master ticket can cover quests, items, NPCs, guilds, and world problems."
        ];
        for (int i = 0; i < headings.Length; i++)
        {
            GameText.DrawPlain(dl, headings[i],
                origin + HelpFrameUiLaw.HomeIssueHeading(i) * s, 12f, s, VanillaGold);
            HelpFrameUiLaw.LogicalRect description = HelpFrameUiLaw.HomeIssueDescription(i);
            DrawWrappedText(dl, descriptions[i], origin + description.Min * s,
                description.Width, 10f * s, s, 0xffffffff);
        }
        if (VanillaButton(dl, "##help-open-issues", "Open a Ticket",
                origin + HelpFrameUiLaw.HomeOpenTicket.Min * s,
                HelpFrameUiLaw.HomeOpenTicket.Size, s)) _helpPage = 1;
        if (VanillaButton(dl, "##help-home-cancel", "Cancel",
                origin + HelpFrameUiLaw.HomeCancel.Min * s,
                HelpFrameUiLaw.HomeCancel.Size, s)) _helpOpen = false;
    }

    private void DrawHelpCategories(ImDrawListPtr dl, Vector2 origin, float s)
    {
        DrawCenteredText(dl, origin + HelpFrameUiLaw.CategoryHeadingCenter * s,
            "Select the category that best describes the issue", 12f * s, 0xffffffff);
        int type = _helpTicketType;
        string[] types = ["Stuck", "Behavior / Harassment", "Guild", "Item", "Environment", "Other"];
        for (int i = 0; i < types.Length; i++)
            if (VanillaButton(dl, $"##help-category-{i}", types[i],
                    origin + HelpFrameUiLaw.CategoryButton(i).Min * s,
                    HelpFrameUiLaw.CategoryButton(i).Size, s))
            { _helpTicketType = (byte)(i + 1); _helpPage = 2; }
        if (VanillaButton(dl, "##help-category-back", "Back",
                origin + HelpFrameUiLaw.CategoryBack.Min * s,
                HelpFrameUiLaw.CategoryBack.Size, s)) _helpPage = 0;
        if (VanillaButton(dl, "##help-category-cancel", "Cancel",
                origin + HelpFrameUiLaw.CategoryCancel.Min * s,
                HelpFrameUiLaw.CategoryCancel.Size, s)) _helpOpen = false;
    }

    private void DrawOpenTicket(ImDrawListPtr dl, Vector2 origin, float s)
    {
        string[] types = ["Stuck", "Behavior / Harassment", "Guild", "Item", "Environment", "Other"];
        GameText.DrawPlain(dl,
            $"Category: {types[Math.Clamp(_helpTicketType - 1, 0, types.Length - 1)]}",
            origin + HelpFrameUiLaw.TicketCategory * s, 11f, s, 0xffffffff);
        DrawWrappedText(dl,
            "Describe the problem in detail. A Game Master can review this ticket on the server.",
            origin + HelpFrameUiLaw.TicketInstructions.Min * s,
            HelpFrameUiLaw.TicketInstructions.Width, 10f * s, s, 0xffffffff);
        VanillaInputText(dl,"##ticket-text",ref _helpTicketText,2047,
            origin + HelpFrameUiLaw.TicketInput.Min * s,
            HelpFrameUiLaw.TicketInput.Size, s, true);
        GameText.DrawPlain(dl, _helpTicketStatus, origin + HelpFrameUiLaw.TicketStatus * s,
            10f, s, 0xffaaaaaa);
        bool hasText = !string.IsNullOrWhiteSpace(_helpTicketText);
        if (VanillaButton(dl, "##ticket-back", "Back",
                origin + HelpFrameUiLaw.TicketBack.Min * s,
                HelpFrameUiLaw.TicketBack.Size, s)) _helpPage = 1;
        if (VanillaButton(dl, "##ticket-submit", "Submit",
                origin + HelpFrameUiLaw.TicketSubmit.Min * s,
                HelpFrameUiLaw.TicketSubmit.Size, s, hasText))
        {
            uint map = _net?.Player?.Map ?? 0;
            Vector3 pos = TryGetSessionBodyPose(out WorldBodyPose sessionBody)
                ? sessionBody.Position : Vector3.Zero;
            _net?.GmTicketCreate(_helpTicketType, map, pos, _helpTicketText);
        }
        if (VanillaButton(dl, "##ticket-delete", "Delete Ticket",
                origin + HelpFrameUiLaw.TicketDelete.Min * s,
                HelpFrameUiLaw.TicketDelete.Size, s)) _net?.GmTicketDelete();
    }
}
