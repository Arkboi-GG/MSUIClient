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
              PetMenuUiLaw.DismissWord == 0x0700_0003 &&
              PetMenuUiLaw.FrameWidth == 128 && PetMenuUiLaw.FrameHeight == 42,
            "pet dialog text, definitions, rename cap, dismiss word, or frame hit law drift");

        PetMenuUiLaw.PlainPopupLayout plain = PetMenuUiLaw.PlainLayout(14);
        Check(plain.Width == StaticPopupCoordinatorLaw.BaseWidth &&
              plain.Text == new StaticPopupCoordinatorLaw.Rect(15, 16, 290, 14) &&
              plain.Button1 == new StaticPopupCoordinatorLaw.Rect(26, 38, 128, 20) &&
              plain.Button2 == new StaticPopupCoordinatorLaw.Rect(167, 38, 128, 20) &&
              plain.Height == 74,
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
        Check(petFrame.Contains("PetMenuUiLaw.FrameWidth", StringComparison.Ordinal) &&
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
              coordinator.Contains("ApplyPetMenuPopupEffect(effect)", StringComparison.Ordinal),
            "pet rename chain regressed from shared rule-owned StaticPopup slots");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
