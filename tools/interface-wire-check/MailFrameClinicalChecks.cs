using MSUIClient;
using MSUIClient.Engine.UI;
using System.Numerics;

internal static class MailFrameClinicalChecks
{
    public static void Run()
    {
        Check(MailUiLaw.Frame == new MailUiLaw.LogicalRect(0, 0, 384, 512) &&
              MailUiLaw.ShellPieces.Length == 4 &&
              MailUiLaw.ShellPieces[0] == new MailUiLaw.LogicalRect(0, 0, 256, 256) &&
              MailUiLaw.ShellPieces[3] == new MailUiLaw.LogicalRect(256, 256, 128, 256) &&
              MailUiLaw.ComposeShellOffset == new Vector2(2, 1) &&
              MailUiLaw.Portrait == new MailUiLaw.LogicalRect(10, 8, 58, 58) &&
              MailUiLaw.Close == new MailUiLaw.LogicalRect(323, 9, 32, 32) &&
              MailUiLaw.TitleCenter == new Vector2(198, 26) &&
              MailUiLaw.ActionNormalFont == "GameFontNormalSmall" &&
              MailUiLaw.ActionHighlightFont == "GameFontHighlightSmall" &&
              MailUiLaw.ActionDisabledFont == "GameFontDisableSmall" &&
              MailUiLaw.FirstTab(74) == new MailUiLaw.LogicalRect(24, 436, 74, 32) &&
              MailUiLaw.SecondTab(74, 92) == new MailUiLaw.LogicalRect(90, 436, 92, 32),
            "MailFrame shell, portrait, close, title, or tab geometry drift");

        Check(MailUiLaw.InboxRow(0) == new MailUiLaw.LogicalRect(28, 80, 305, 45) &&
              MailUiLaw.InboxRow(6) == new MailUiLaw.LogicalRect(28, 350, 305, 45) &&
              MailUiLaw.RightTooltipSeat(new Vector2(32, 83), new Vector2(37, 37)) ==
                  new MailUiLaw.TooltipSeat(new Vector2(69, 83), new Vector2(0, 1)) &&
              MailUiLaw.InboxBorderLeft.Rect == new MailUiLaw.LogicalRect(0, 0, 42, 48) &&
              MailUiLaw.InboxBorderRight.Rect == new MailUiLaw.LogicalRect(42, 0, 263, 48) &&
              MailUiLaw.InboxDivider == new MailUiLaw.LogicalRect(-8, 43, 322, 2) &&
              MailUiLaw.InboxSenderBox == new MailUiLaw.LogicalRect(47, 4, 200, 16) &&
              MailUiLaw.InboxSubjectBox == new MailUiLaw.LogicalRect(47, 20, 248, 18) &&
              MailUiLaw.InboxExpiryBox == new MailUiLaw.LogicalRect(201, 4, 100, 16) &&
              MailUiLaw.InboxItemButton == new MailUiLaw.LogicalRect(4, 3, 37, 37) &&
              MailUiLaw.InboxItemRing == new MailUiLaw.LogicalRect(-9.5f, -10.5f, 64, 64) &&
              MailUiLaw.InboxPreviousPage == new MailUiLaw.LogicalRect(34, 392, 32, 32) &&
              MailUiLaw.InboxNextPage == new MailUiLaw.LogicalRect(298, 392, 32, 32),
            "Inbox row, divider, item, expiry, or paging geometry drift");

        Check(MailUiLaw.StationeryFrame == new MailUiLaw.LogicalRect(21, 97, 296, 257) &&
              MailUiLaw.HorizontalBarLeft == new MailUiLaw.LogicalRect(15, 350, 256, 16) &&
              MailUiLaw.ComposeRecipient == new MailUiLaw.LogicalRect(105, 46, 122, 20) &&
              MailUiLaw.ComposeSubject == new MailUiLaw.LogicalRect(105, 69, 237, 20) &&
              MailUiLaw.ComposeBody == new MailUiLaw.LogicalRect(41, 107, 270, 200) &&
              MailUiLaw.ComposeAttachment == new MailUiLaw.LogicalRect(30, 368, 37, 37) &&
              MailUiLaw.ComposeSendMoneyRadio ==
                  new MailUiLaw.LogicalRect(252, 362, 16, 16) &&
              MailUiLaw.ComposeCodRadio == new MailUiLaw.LogicalRect(252, 379, 16, 16) &&
              MailUiLaw.ComposeSend == new MailUiLaw.LogicalRect(185, 410, 80, 22) &&
              MailUiLaw.ComposeCancel == new MailUiLaw.LogicalRect(265, 410, 80, 22),
            "SendMail form, stationery, attachment, radio, or action geometry drift");

        Check(MailUiLaw.OpenMailFrame == new MailUiLaw.LogicalRect(0, 0, 384, 512) &&
              MailUiLaw.OpenMailOrigin(new Vector2(384, 104), 1) == new Vector2(758, 104) &&
              MailUiLaw.OpenMailAnchorX == -10 &&
              MailUiLaw.OpenMailIcon == new MailUiLaw.LogicalRect(9, 6, 60, 60) &&
              MailUiLaw.OpenMailTitleCenter == new Vector2(198, 24) &&
              MailUiLaw.OpenMailSenderLabelBox ==
                  new MailUiLaw.LogicalRect(114, 45, 0, 16) &&
              MailUiLaw.OpenMailSenderBox ==
                  new MailUiLaw.LogicalRect(119, 47, 110, 12) &&
              MailUiLaw.OpenMailSubjectLabelBox ==
                  new MailUiLaw.LogicalRect(114, 65, 0, 16) &&
              MailUiLaw.OpenMailSubjectBox ==
                  new MailUiLaw.LogicalRect(119, 69, 225, 16),
            "OpenMailFrame shell, icon, or heading geometry drift");

        Check(MailUiLaw.OpenMailHorizontalBar ==
                  new MailUiLaw.LogicalRect(15, 350, 331, 16) &&
              MailUiLaw.OpenMailScrollFrame ==
                  new MailUiLaw.LogicalRect(21, 97, 296, 257) &&
              MailUiLaw.OpenMailScrollRight == new Vector2(317, 97) &&
              MailUiLaw.OpenMailBody == new MailUiLaw.LogicalRect(31, 107, 276, 240) &&
              MailUiLaw.OpenMailCaptionCenter(true) == new Vector2(124, 389) &&
              MailUiLaw.OpenMailCaptionCenter(false) == new Vector2(187, 389),
            "OpenMailFrame stationery, body, or attachment-caption geometry drift");

        Check(MailUiLaw.OpenMailAttachmentSlot(0) ==
                  new MailUiLaw.LogicalRect(189, 371, 37, 37) &&
              MailUiLaw.OpenMailAttachmentSlot(1) ==
                  new MailUiLaw.LogicalRect(236, 371, 37, 37) &&
              MailUiLaw.OpenMailAttachmentSlot(2) ==
                  new MailUiLaw.LogicalRect(283, 371, 37, 37) &&
              MailUiLaw.OpenMailSlotArt ==
                  new MailUiLaw.LogicalRect(-10.5f, -10.5f, 58, 58) &&
              MailUiLaw.OpenMailSlotCount == new Vector2(35, 25) &&
              MailUiLaw.OpenMailReply == new MailUiLaw.LogicalRect(101, 410, 82, 22) &&
              MailUiLaw.OpenMailDelete == new MailUiLaw.LogicalRect(183, 410, 82, 22) &&
              MailUiLaw.OpenMailBottomClose ==
                  new MailUiLaw.LogicalRect(265, 410, 80, 22) &&
              MailUiLaw.OpenMailTopClose == new MailUiLaw.LogicalRect(321, 9, 32, 32),
            "OpenMailFrame attachment-chain or button geometry drift");

        var plainMoney = MailUiLaw.LayoutConfirmation(false, true, 12);
        var alertMoney = MailUiLaw.LayoutConfirmation(true, true, 36);
        var alertItem = MailUiLaw.LayoutConfirmation(true, false, 36);
        Check(plainMoney.Size == new Vector2(320, 88) &&
              MailUiLaw.ConfirmationOrigin(new Vector2(1920, 1080), 1.5f, plainMoney) ==
                  new Vector2(720, 192) &&
              alertMoney.Size == new Vector2(420, 112) &&
              alertMoney.Alert == new MailUiLaw.LogicalRect(12, 24, 64, 64) &&
              alertMoney.Money == new Vector2(210, 57) &&
              alertMoney.Accept == new MailUiLaw.LogicalRect(76, 76, 128, 20) &&
              alertMoney.Cancel == new MailUiLaw.LogicalRect(217, 76, 128, 20) &&
              alertItem.Size.Y == alertMoney.Size.Y - 16 &&
              alertItem.Accept.Y == alertMoney.Accept.Y - 16,
            "Mail confirmation must reserve wrapped text, money and buttons without overlap");
        Check(MailUiLaw.Clip(new Vector2(10, 20), new Vector2(30, 40)) ==
                  new Vector4(10, 20, 40, 60),
            "Mail screen clip geometry drift");

        string source = SourceText.Read(Path.Combine(ClientConfig.FindRepoRoot(), "MSUIClient",
            "GameLoop", "Panels", "GameLoop.Mail.cs"));
        Check(!MethodBody(source, "private void DrawOpenMailFrame").Contains(
                  "new Vector2", StringComparison.Ordinal) &&
              !MethodBody(source, "private bool DrawOpenMailSlot").Contains(
                  "new Vector2", StringComparison.Ordinal) &&
              !MethodBody(source, "private void DrawMailConfirmation").Contains(
                  "new Vector2", StringComparison.Ordinal) &&
              !MethodBody(source, "private bool DrawMailPopupButton").Contains(
                  "new Vector2", StringComparison.Ordinal) &&
              source.Contains("MailUiLaw.OpenMailAttachmentSlot(slotIndex)",
                  StringComparison.Ordinal) &&
              source.Contains("layout.Accept, s, \"accept\"", StringComparison.Ordinal),
            "Mail satellite renderers must consume rule-owned geometry");

        string inboxRow = MethodBody(source, "private void DrawMailInboxRow");
        string confirmation = MethodBody(source, "private void DrawMailConfirmation");
        Check(confirmation.Contains("MailConfirmationKind.Cod => row!.Cod", StringComparison.Ordinal) &&
              confirmation.Contains("MailConfirmationKind.DeleteMoney => row!.Money", StringComparison.Ordinal) &&
              confirmation.Contains("MailConfirmationKind.SendMoney => MailAmountCopper()", StringComparison.Ordinal) &&
              confirmation.Contains("DrawMailMoneyDisplay(dl, copper", StringComparison.Ordinal) &&
              confirmation.Contains("centered: true", StringComparison.Ordinal) &&
              confirmation.Contains("WrapTooltipText(message, \"GameFontHighlight\"", StringComparison.Ordinal) &&
              confirmation.Contains("lines.Length * pitch", StringComparison.Ordinal) &&
              confirmation.Contains("origin + layout.Money * s", StringComparison.Ordinal),
            "Mail confirmation must show the actual COD, deletion, or sending amount before acceptance");
        string sendSlot = MethodBody(source, "private void DrawMailSendSlot");
        string openSlot = MethodBody(source, "private bool DrawOpenMailSlot");
        Check(inboxRow.Contains("OfferOwnerAnchoredSharedGameTooltip(tooltipOwner,",
                  StringComparison.Ordinal) &&
              inboxRow.Contains("GameTooltipTextTone.White", StringComparison.Ordinal) &&
              !inboxRow.Contains("ImGui.BeginTooltip", StringComparison.Ordinal) &&
              sendSlot.Contains("OfferOwnerAnchoredSharedGameTooltip(tooltipOwner,",
                  StringComparison.Ordinal) &&
              sendSlot.Contains("GameTooltipTextTone.White", StringComparison.Ordinal) &&
              !sendSlot.Contains("ImGui.BeginTooltip", StringComparison.Ordinal) &&
              source.Contains("setTextTone: GameTooltipTextTone.White",
                  StringComparison.Ordinal) &&
              openSlot.Contains("if (setTextTone is { } tone)",
                  StringComparison.Ordinal) &&
              openSlot.Contains("OfferOwnerAnchoredSharedGameTooltip(tooltipOwner,",
                  StringComparison.Ordinal) &&
              openSlot.Contains("OfferPreservedSharedGameTooltipRenderer(tooltipOwner,",
                  StringComparison.Ordinal),
            "Mail SetText tooltips must use the rule-seated shared classic renderer");

        Check(!source.Contains("new Vector2", StringComparison.Ordinal) &&
              !source.Contains("Vector4 clip = new(", StringComparison.Ordinal) &&
              !source.Contains("Vector4 panelClip = new(", StringComparison.Ordinal) &&
              source.Contains("MailUiLaw.Clip", StringComparison.Ordinal) &&
              source.Contains("MailUiLaw.ShellPieces", StringComparison.Ordinal) &&
              source.Contains("MailUiLaw.InboxRow(visible)", StringComparison.Ordinal) &&
              source.Contains("MailUiLaw.InboxSenderBox", StringComparison.Ordinal) &&
              source.Contains("MailUiLaw.InboxSubjectBox", StringComparison.Ordinal) &&
              source.Contains("MailUiLaw.InboxExpiryBox", StringComparison.Ordinal) &&
              source.Contains("GameText.EllipsizeToBox(\"GameFontHighlightSmall\", row.Subject",
                  StringComparison.Ordinal) &&
              source.Contains("MailUiLaw.OpenMailSenderLabelBox", StringComparison.Ordinal) &&
              source.Contains("MailUiLaw.OpenMailSubjectBox", StringComparison.Ordinal) &&
              source.Contains("GameText.EllipsizeToBox(\"GameFontNormalSmall\", row.Subject",
                  StringComparison.Ordinal) &&
              source.Contains("MailUiLaw.ComposeRecipient", StringComparison.Ordinal) &&
              source.Contains("MailUiLaw.ComposeAttachment", StringComparison.Ordinal) &&
              source.Contains("MailUiLaw.RightTooltipSeat(", StringComparison.Ordinal) &&
              source.Contains("normalFont: MailUiLaw.ActionNormalFont",
                  StringComparison.Ordinal) &&
              source.Contains("highlightFont: MailUiLaw.ActionHighlightFont",
                  StringComparison.Ordinal) &&
              source.Contains("disabledFont: MailUiLaw.ActionDisabledFont",
                  StringComparison.Ordinal) &&
              source.Contains("nextWindowPivot: tooltipSeat.Pivot",
                  StringComparison.Ordinal) &&
              source.Contains("ImGui.SetNextWindowPos(tooltipSeat.Anchor, ImGuiCond.Always",
                  StringComparison.Ordinal) &&
              source.Contains("MailUiLaw.ScrollTrackTop", StringComparison.Ordinal),
            "Mail renderers must contain no player-facing local Vector2 geometry");

        string receivedMail = MethodBody(source, "private void ApplyReceivedMail");
        Check(receivedMail.Contains("_mailRefreshPending = true", StringComparison.Ordinal) &&
              receivedMail.Contains("RefreshMailList(force: true)", StringComparison.Ordinal) &&
              receivedMail.Contains("mailboxOpen: false", StringComparison.Ordinal),
            "Received mail must refresh the open inbox without changing its deferred countdown");

        string closeMail = MethodBody(source, "private void CloseMailSession");
        int queryStamp = closeMail.IndexOf(
            "_nextMailSeconds = MailUiLaw.NoMailQueryStamp", StringComparison.Ordinal);
        int querySend = closeMail.IndexOf("_net?.QueryNextMailTime()", StringComparison.Ordinal);
        Check(queryStamp >= 0 && querySend > queryStamp &&
              source.Contains(
                  "private float _nextMailSeconds = MailUiLaw.NoMailQueryStamp",
                  StringComparison.Ordinal) &&
              MailUiLaw.NoMailQueryStamp == -1f,
            "Mail pending refresh must stamp the client-owned -1 before its query attempt");
    }

    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return "missing " + signature;
        int open = source.IndexOf('{', start);
        if (open < 0) return "missing body " + signature;
        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[start..(index + 1)];
        }
        return "unterminated " + signature;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
