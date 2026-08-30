using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class PetMenuClinicalChecks
{
    public static void Run()
    {
        const ulong player = 0x0102_0304_0506_0708;
        Check(PetMenuUiLaw.Predicates(player, player,
                  PetMenuUiLaw.AbandonFlag | PetMenuUiLaw.RenameFlag) == (true, true) &&
              PetMenuUiLaw.Predicates(player, player, PetMenuUiLaw.AbandonFlag) ==
                  (true, false) &&
              PetMenuUiLaw.Predicates(null, player,
                  PetMenuUiLaw.AbandonFlag | PetMenuUiLaw.RenameFlag) == (false, false),
            "pet menu summoned-by ownership or UNIT_FIELD_FLAGS masks drift");

        Check(UnitPopupUiLaw.VisiblePetRows(true, true, true).SequenceEqual(new[]
              {
                  UnitPopupRow.PetPaperDoll, UnitPopupRow.PetRename,
                  UnitPopupRow.PetAbandon, UnitPopupRow.Cancel,
              }) &&
              UnitPopupUiLaw.VisiblePetRows(true, true, false).SequenceEqual(new[]
              {
                  UnitPopupRow.PetPaperDoll, UnitPopupRow.PetAbandon,
                  UnitPopupRow.Cancel,
              }) &&
              UnitPopupUiLaw.VisiblePetRows(true, false, false).SequenceEqual(new[]
              {
                  UnitPopupRow.PetDismiss, UnitPopupRow.Cancel,
              }) &&
              UnitPopupUiLaw.VisiblePetRows(false, false, false).SequenceEqual(new[]
              {
                  UnitPopupRow.Cancel,
              }) &&
              !UnitPopupUiLaw.ShouldOpen(
                  UnitPopupUiLaw.VisiblePetRows(false, false, false)) &&
              UnitPopupUiLaw.RowText(UnitPopupRow.PetPaperDoll) == "Pet Details" &&
              UnitPopupUiLaw.RowText(UnitPopupRow.PetRename) == "Rename" &&
              UnitPopupUiLaw.RowText(UnitPopupRow.PetAbandon) == "Abandon" &&
              UnitPopupUiLaw.RowText(UnitPopupRow.PetDismiss) == "Dismiss",
            "pet UnitPopup order, hunter/summon fork, or cancel-only refusal drift");

        StaticPopupCoordinatorLaw.Definition rename = PetMenuUiLaw.RenameDefinition;
        Check(PetMenuUiLaw.AbandonText ==
                  "Are you sure you want to permanently abandon your pet?" &&
              PetMenuUiLaw.RenameLabel == "Enter desired name of pet:" &&
              PetMenuUiLaw.RenameConfirmation("Rexxar") == "Name your pet 'Rexxar'?" &&
              rename.HasEditBox && rename.HasOnShow && rename.HasEditBoxEnter &&
              rename.MaxLetters == 12 && rename.HideOnEscape &&
              PetMenuUiLaw.AbandonDefinition.HasAccept &&
              PetMenuUiLaw.AbandonDefinition.HasCancel &&
              PetMenuUiLaw.RenameConfirmDefinition.HasAccept &&
              PetMenuUiLaw.RenameConfirmDefinition.HasCancel &&
              PetMenuUiLaw.DismissWord == 0x0700_0003,
            "pet dialog text, definitions, rename cap, or dismiss word drift");

        // PetFrame.xml, verbatim. These were invented before (128x42 at (10,86), 75x7 bars at
        // (39,12)/(39,21)), which put the status bars eight pixels left and eight to ten high
        // of the recess the frame art paints for them.
        Check(PetFrameUiLaw.Origin == new Vector2(61f, 64f) &&
              PetFrameUiLaw.Size == new Vector2(128f, 53f) &&
              PetFrameUiLaw.TextureOffset == new Vector2(0f, 2f) &&
              PetFrameUiLaw.TextureSize == new Vector2(128f, 64f) &&
              PetFrameUiLaw.PortraitOffset == new Vector2(7f, 6f) &&
              PetFrameUiLaw.PortraitSize == 37f &&
              PetFrameUiLaw.HealthBarOffset == new Vector2(47f, 22f) &&
              PetFrameUiLaw.ManaBarOffset == new Vector2(47f, 29f) &&
              PetFrameUiLaw.BarSize == new Vector2(70f, 8f) &&
              PetFrameUiLaw.NameFont == "GameFontNormalSmall" &&
              PetFrameUiLaw.NameLeft == 50f && PetFrameUiLaw.NameBottom == 20f &&
              PetFrameUiLaw.FrameTexture ==
                  @"Interface\TargetingFrame\UI-SmallTargetingFrame",
            "PetFrame geometry drifted from the shipped PetFrame.xml");

        PetMenuUiLaw.PlainPopupLayout plain = PetMenuUiLaw.PlainLayout(14);
        Check(plain.Width == StaticPopupCoordinatorLaw.BaseWidth &&
              plain.Text == new StaticPopupCoordinatorLaw.Rect(15, 16, 290, 14) &&
              plain.Button1 == new StaticPopupCoordinatorLaw.Rect(26, 38, 128, 20) &&
              plain.Button2 == new StaticPopupCoordinatorLaw.Rect(167, 38, 128, 20) &&
              plain.Height == 74 && plain.Size == new Vector2(320, 74) &&
              PetMenuUiLaw.TextLineCenter(plain, 14, 1) == new Vector2(160, 37),
            "pet plain StaticPopup child geometry drift");

        Check((ushort)Op.CMSG_PET_ABANDON == 0x0176 &&
              (ushort)Op.CMSG_PET_RENAME == 0x0177,
            "pet menu opcode drift");
        byte[] abandon = WorldSession.BuildPetAbandonBody(player);
        byte[] renameBody = WorldSession.BuildPetRenameBody(player, "Rexxar");
        Check(abandon.Length == 8 && BitConverter.ToUInt64(abandon, 0) == player &&
              renameBody.Length == 15 && BitConverter.ToUInt64(renameBody, 0) == player &&
              System.Text.Encoding.UTF8.GetString(renameBody, 8, 6) == "Rexxar" &&
              renameBody[^1] == 0,
            "pet abandon or rename packet body drift");

        string root = ClientConfig.FindRepoRoot();
        string petFrame = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Panels", "GameLoop.Pet.cs"));
        string popup = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.UnitPopup.cs"));
        string modal = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.PetMenu.cs"));
        string coordinator = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.PartyFrames.cs"));
        Check(petFrame.Contains("PetFrameUiLaw.Size * s", StringComparison.Ordinal) &&
              petFrame.Contains("PetFrameUiLaw.HealthBarOffset", StringComparison.Ordinal) &&
              petFrame.Contains("PetFrameUiLaw.ManaBarOffset", StringComparison.Ordinal) &&
              petFrame.Contains("GameText.Draw(dl, PetFrameUiLaw.NameFont", StringComparison.Ordinal) &&
              petFrame.Contains("ImGuiMouseButton.Right", StringComparison.Ordinal) &&
              petFrame.Contains("UnitPopupWhich.Pet", StringComparison.Ordinal) &&
              petFrame.Contains("ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse",
                  StringComparison.Ordinal) &&
              popup.Contains("pet.Fields.SummonedBy == LocalPlayerGuid",
                  StringComparison.Ordinal) &&
              popup.Contains("UnitPopupUiLaw.VisiblePetRows", StringComparison.Ordinal) &&
              popup.Contains("ShowPetAbandonPopup(guid)", StringComparison.Ordinal) &&
              popup.Contains("ShowPetRenamePopup(guid)", StringComparison.Ordinal) &&
              popup.Contains("PetMenuUiLaw.DismissWord", StringComparison.Ordinal) &&
              popup.Contains("OpenCharacterPageThroughUiPanel(requestedTab: 1)",
                  StringComparison.Ordinal),
            "pet frame right-click or UnitPopup predicate/action integration drift");
        Check(modal.Contains("StaticPopupCoordinatorLaw.NarrowEditLayout", StringComparison.Ordinal) &&
              modal.Contains("StaticPopupOrigin(visible.Slot", StringComparison.Ordinal) &&
              modal.Contains("typeStillSame: false", StringComparison.Ordinal) &&
              modal.Contains("PetMenuUiLaw.RenameConfirmDefinition", StringComparison.Ordinal) &&
              modal.Contains("StaticPopupCoordinatorLaw.HideByType", StringComparison.Ordinal) &&
              !modal.Contains("new Vector2", StringComparison.Ordinal) &&
              coordinator.Contains("ApplyPetMenuPopupEffect(effect)", StringComparison.Ordinal),
            "pet rename chain regressed from shared rule-owned StaticPopup slots");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
