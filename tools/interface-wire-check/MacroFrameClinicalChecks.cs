using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;

internal static class MacroFrameClinicalChecks
{
    public static void Run()
    {
        CheckGeometryAndState();
        CheckArchiveCatalogLaw();
        CheckRuntimeSourceFence();
    }

    private static void CheckGeometryAndState()
    {
        Check(MacroFrameUiLaw.PopupWidth == 297f &&
              MacroFrameUiLaw.PopupHeight == 298f &&
              MacroFrameUiLaw.VisibleIcons == 20 &&
              MacroFrameUiLaw.IconsPerRow == 5 &&
              MacroFrameUiLaw.VisibleRows == 4 &&
              MacroFrameUiLaw.NameCapacity == 16,
            "MacroPopup frozen size/grid/name constants drifted");
        Check(MacroFrameUiLaw.FrameWidth == 384 && MacroFrameUiLaw.FrameHeight == 512 &&
              MacroFrameUiLaw.FrameSize == new Vector2(384, 512) &&
              MacroFrameUiLaw.TitleFont == "GameFontNormal" &&
              MacroFrameUiLaw.TitleCenter == new Vector2(192, 23) &&
              MacroFrameUiLaw.FrameArt.Count == 4 &&
              MacroFrameUiLaw.FrameArt[1].Rect ==
                  new MacroFrameUiLaw.Rect(256, 0, 128, 256) &&
              MacroFrameUiLaw.TotalMacros == 36 && MacroFrameUiLaw.MacrosPerSet == 18 &&
              MacroFrameUiLaw.MacroButton(0) == new MacroFrameUiLaw.Rect(42, 83, 36, 36) &&
              MacroFrameUiLaw.MacroButton(5) == new MacroFrameUiLaw.Rect(287, 83, 36, 36) &&
              MacroFrameUiLaw.MacroButton(17) == new MacroFrameUiLaw.Rect(287, 175, 36, 36) &&
              MacroFrameUiLaw.SelectedBackground == new MacroFrameUiLaw.Rect(16, 228, 64, 64) &&
              MacroFrameUiLaw.BodyBackground == new MacroFrameUiLaw.Rect(18, 305, 322, 95) &&
              MacroFrameUiLaw.BodyEditor == new MacroFrameUiLaw.Rect(27, 310, 286, 85) &&
              MacroFrameUiLaw.BodyScrollUp == new MacroFrameUiLaw.Rect(319, 310, 16, 16) &&
              MacroFrameUiLaw.BodyScrollTrack == new MacroFrameUiLaw.Rect(319, 326, 16, 53) &&
              MacroFrameUiLaw.BodyScrollDown == new MacroFrameUiLaw.Rect(319, 379, 16, 16) &&
              MacroFrameUiLaw.ChangeButton == new MacroFrameUiLaw.Rect(67, 258, 170, 22) &&
              MacroFrameUiLaw.DeleteButton == new MacroFrameUiLaw.Rect(17, 411, 80, 22) &&
              MacroFrameUiLaw.NewButton == new MacroFrameUiLaw.Rect(182, 411, 80, 22) &&
              MacroFrameUiLaw.ExitButton == new MacroFrameUiLaw.Rect(263, 411, 80, 22) &&
              MacroFrameUiLaw.CloseButton == new MacroFrameUiLaw.Rect(323, 8, 32, 32) &&
              MacroFrameUiLaw.SetBase(false) == 0 && MacroFrameUiLaw.SetBase(true) == 18 &&
              MacroFrameUiLaw.AbsoluteIndex(true, 17) == 35 &&
              MacroFrameUiLaw.InSet(true, 18) && !MacroFrameUiLaw.InSet(true, 17) &&
              // TabButtonTemplate: 16 px caps, padding -15 -> 60 - 15 + 32.
              MacroFrameUiLaw.GeneralTabWidth(60) == 77 &&
              // The character tab caps its TEXT at 150 (PanelTemplates_TabResize maxWidth).
              MacroFrameUiLaw.CharacterTabWidth(200, 77) == 150 - 15 + 32 &&
              MacroFrameUiLaw.TabFont == "GameFontNormalSmall",
            "MacroFrame main-window/account-character geometry drifted");
        // MacroFrameButtonTemplate seats (Blizzard_MacroUI.xml, 2026-09-03): socket and icon
        // CENTER (0,-1) = one pixel down; the name a 36x10 box at BOTTOM (0,2); the character
        // counter BOTTOM (-15,105) of a 10 px font.
        Check(MacroFrameUiLaw.MacroSocket == new MacroFrameUiLaw.Rect(-14, -13, 64, 64) &&
              MacroFrameUiLaw.IconOffset == new Vector2(0, 1) &&
              MacroFrameUiLaw.MacroNameCenter == new Vector2(18, 29) &&
              MacroFrameUiLaw.MacroNameWidth == 36f &&
              MacroFrameUiLaw.CharacterLimitCenter == new Vector2(177, 402),
            "MacroFrame button-template seats drifted");
        string overflowBody = new('x', 255);
        Check(MacroFrameUiLaw.BodyContentHeight("") == 85 &&
              MacroFrameUiLaw.BodyContentHeight("one\ntwo") == 85 &&
              MacroFrameUiLaw.BodyContentHeight(overflowBody) == 84 + 14 &&
              MacroFrameUiLaw.MaximumBodyScroll(overflowBody) == 13 &&
              MacroFrameUiLaw.WheelBodyScroll(0, overflowBody, -1) == 13 &&
              MacroFrameUiLaw.BodyThumbY(13, overflowBody) == 363,
            "MacroFrame body child sizing/scroll/knob law drifted");
        Check(MacroFrameUiLaw.BodyScrollKnob(13, overflowBody) ==
                  new MacroFrameUiLaw.Rect(319, 363, 16, 16) &&
              MacroFrameUiLaw.DragPreviewOffset == new Vector2(10) &&
              MacroFrameUiLaw.DragPreviewSize == new Vector2(32) &&
              MacroFrameUiLaw.CharacterTabOffset(85) == new Vector2(85, 0),
            "MacroFrame dynamic child geometry drifted");
        Check(MacroFrameUiLaw.PopupMinimum(new Vector2(0, 104), 1f) ==
              new Vector2(344, 144),
            "MacroPopup TOPLEFT-on-MacroFrame-TOPRIGHT (-40,-40) seat drifted");
        Check(MacroFrameUiLaw.IconButton(0) == new MacroFrameUiLaw.Rect(24, 85, 36, 36) &&
              MacroFrameUiLaw.IconButton(4) == new MacroFrameUiLaw.Rect(208, 85, 36, 36) &&
              MacroFrameUiLaw.IconButton(5) == new MacroFrameUiLaw.Rect(24, 129, 36, 36) &&
              MacroFrameUiLaw.IconButton(19) == new MacroFrameUiLaw.Rect(208, 217, 36, 36),
            "MacroPopup 5x4 icon geometry drifted");
        Check(MacroFrameUiLaw.NameEdit == new MacroFrameUiLaw.Rect(29, 35, 200, 20) &&
              MacroFrameUiLaw.NameInput == new MacroFrameUiLaw.Rect(32, 35, 194, 20) &&
              MacroFrameUiLaw.OkayButton == new MacroFrameUiLaw.Rect(128, 263, 78, 22) &&
              MacroFrameUiLaw.CancelButton == new MacroFrameUiLaw.Rect(208, 263, 78, 22) &&
              MacroFrameUiLaw.PopupSize == new Vector2(297, 298) &&
              MacroFrameUiLaw.PopupArt.Count == 4 &&
              MacroFrameUiLaw.PopupArt[3].Rect ==
                  new MacroFrameUiLaw.Rect(256, 256, 64, 64) &&
              MacroFrameUiLaw.NameBorderSlices[1].Rect ==
                  new MacroFrameUiLaw.Rect(30, 35, 175, 29) &&
              // A 16x16 thumb travelling the bar between the two 16 px buttons.
              MacroFrameUiLaw.PopupScrollKnob(2, 4) ==
                  new MacroFrameUiLaw.Rect(264, 156.5f, 16, 16) &&
              MacroFrameUiLaw.PopupScrollTrackTop == new MacroFrameUiLaw.Rect(255, 65, 30, 120) &&
              MacroFrameUiLaw.PopupScrollTrackBottom == new MacroFrameUiLaw.Rect(255, 140, 30, 123) &&
              MacroFrameUiLaw.PopupNameText == "Enter Macro Name (Max 16 Characters):" &&
              MacroFrameUiLaw.PopupIconText == "Choose an Icon:",
            "MacroPopup name or Okay/Cancel seats drifted");
        Check(MacroFrameUiLaw.MaximumRowOffset(0) == 0 &&
              MacroFrameUiLaw.MaximumRowOffset(20) == 0 &&
              MacroFrameUiLaw.MaximumRowOffset(21) == 1 &&
              MacroFrameUiLaw.MaximumRowOffset(517) == 100 &&
              MacroFrameUiLaw.CatalogIndex(2, 3, 100) == 13 &&
              MacroFrameUiLaw.CatalogIndex(19, 19, 100) == -1,
            "MacroPopup faux-scroll row/index projection drifted");
        Check(!MacroFrameUiLaw.OkayEnabled(MacroPopupMode.New, "", 0, false) &&
              !MacroFrameUiLaw.OkayEnabled(MacroPopupMode.New, "Name", -1, false) &&
              MacroFrameUiLaw.OkayEnabled(MacroPopupMode.New, "Name", 0, false) &&
              MacroFrameUiLaw.OkayEnabled(MacroPopupMode.Edit, "Name", -1, true),
            "MacroPopup Okay enable law drifted");
        Check(MacroFrameUiLaw.RunnableLines("/cast Fireball\r\n\r\n  /say pew  \n")
                  .SequenceEqual(["/cast Fireball", "/say pew"]) &&
              SpellCatalog.SplitCastName("Fireball(Rank 1)") == ("Fireball", "Rank 1") &&
              SpellCatalog.SplitCastName("Fireball (Rank 2)") == ("Fireball", "Rank 2") &&
              SpellCatalog.SplitCastName("Fireball") == ("Fireball", null),
            "macro EXECUTE_CHAT_LINE tokenization or cast-name/subtext law drifted");
        const string vanillaStore = "MACRO 3 \"ns\" Ability_BackStab\n/say three\nEND\n" +
            "MACRO 1 \"say \"hi\"\" Ability_Ambush\n/say one\nEND\n" +
            "MACRO bad \"skip\" Ability_Ambush\nEND\n" +
            "MACRO 2 \"bare\" \nEND\n";
        IReadOnlyList<MacroFrameUiLaw.StoredMacro> stored =
            MacroFrameUiLaw.ParseStore(vanillaStore);
        Check(stored.Count == 3 && stored[0].Name == "say \"hi\"" &&
              stored[0].IconPath == @"Interface\Icons\Ability_Ambush" &&
              stored[1].Name == "bare" && stored[1].IconPath == "" &&
              stored[2].Name == "ns" && stored[2].Body == "/say three" &&
              MacroFrameUiLaw.ParseStore(MacroFrameUiLaw.WriteStore(stored))
                  .SequenceEqual(stored) &&
              MacroFrameUiLaw.StoreFileToken("Hydraxian Waterlords/a") ==
                  "Hydraxian_Waterlords_a" &&
              MacroFrameUiLaw.StoreFileToken("") == "unknown",
            "vanilla-compatible macro store parse/write/scope-token law drifted");
    }

    private static void CheckArchiveCatalogLaw()
    {
        string[] listed =
        [
            @"Interface\Icons\Spell_Fire.blp",
            @"interface/icons/ability_Z.tga",
            @"Interface\Icons\Ability_Z.blp",
            @"Interface\Icons\Ability_Druid_Mangle.tga.blp",
            @"Interface\Icons\INV_Sword_04.blp",
            @"Interface\Icons\Sub\Spell_Bad.blp",
            @"Interface\Icons\Spell_NotTexture.txt",
            @"Other\Icons\Ability_Bad.blp",
        ];
        IReadOnlyList<string> icons = MacroIconCatalog.Build(listed);
        Check(icons.SequenceEqual(
            [
                @"Interface\Icons\Ability_Druid_Mangle.tga",
                @"Interface\Icons\ability_Z",
                @"Interface\Icons\Spell_Fire",
            ], StringComparer.OrdinalIgnoreCase),
            "Macro chooser did not apply the archive prefix/extension/subdirectory/filter/sort/dedup law");
    }

    private static void CheckRuntimeSourceFence()
    {
        string client = Path.Combine(ClientConfig.FindRepoRoot(), "MSUIClient");
        string macro = File.ReadAllText(Path.Combine(client, "GameLoop", "Panels",
            "GameLoop.Macro.cs"));
        string mount = File.ReadAllText(Path.Combine(client, "Formats", "MpqMount.cs"));
        Check(macro.Contains("MacroFrameUiLaw.PopupMinimum(macroOrigin, scale)",
                  StringComparison.Ordinal) &&
              macro.Contains("UiPanelFrameLogicalOrigin(UiPanelOwnershipRegistry[15])",
                  StringComparison.Ordinal) &&
              macro.Contains("MacroFrameUiLaw.IconButton(visible)",
                  StringComparison.Ordinal) &&
              macro.Contains("MacroFrameUiLaw.OkayButton", StringComparison.Ordinal) &&
              macro.Contains("MacroFrameUiLaw.CancelButton", StringComparison.Ordinal) &&
              macro.Contains("MacroIconCatalog.Load(_mpq)", StringComparison.Ordinal) &&
              macro.Contains("MacroFrameUiLaw.PopupArt", StringComparison.Ordinal) &&
              macro.Contains("UI-ClassTrainer-FilterBorder", StringComparison.Ordinal) &&
              macro.Contains("UI-ClassTrainer-ScrollBar", StringComparison.Ordinal) &&
              macro.Contains("VanillaInsetTab(dl, \"##macro-general-tab\"", StringComparison.Ordinal) &&
              !macro.Contains("VanillaTab(dl, \"##macro-general-tab\"", StringComparison.Ordinal) &&
              macro.Contains("MacroFrameUiLaw.IconOffset", StringComparison.Ordinal) &&
              // The editor buffers may only be committed back once they mirror a selected
              // macro; a commit from empty buffers wiped macro 1's name and body (2026-09-03).
              macro.Contains("if (!_macrosLoaded || !_macroEditorBound ||", StringComparison.Ordinal) &&
              macro.Contains("OpenMacroPopup(MacroPopupMode.New)", StringComparison.Ordinal) &&
              macro.Contains("OpenMacroPopup(MacroPopupMode.Edit)", StringComparison.Ordinal) &&
              macro.Contains("MacroFrameUiLaw.MacroButton(i)", StringComparison.Ordinal) &&
              macro.Contains("MacroFrameUiLaw.Portrait.LogicalSize", StringComparison.Ordinal) &&
              macro.Contains("MacroFrameUiLaw.TitleFont", StringComparison.Ordinal) &&
              macro.Contains("MacroFrameUiLaw.BodyEditor", StringComparison.Ordinal) &&
              macro.Contains("MacroFrameUiLaw.BodyScrollTrack", StringComparison.Ordinal) &&
              macro.Contains("MacroFrameUiLaw.BodyContentHeight(_macroBody)",
                  StringComparison.Ordinal) &&
              macro.Contains("ImGuiWindowFlags.NoScrollbar", StringComparison.Ordinal) &&
              macro.Contains("DrawMacroBodyScrollBar(dl, origin, s)",
                  StringComparison.Ordinal) &&
              macro.Contains("SwitchMacroSet(false)", StringComparison.Ordinal) &&
              macro.Contains("SwitchMacroSet(true)", StringComparison.Ordinal) &&
              macro.Contains("MacroFrameUiLaw.TotalMacros", StringComparison.Ordinal) &&
              macro.Contains("MacroFrameUiLaw.RunnableLines", StringComparison.Ordinal) &&
              macro.Contains("SubmitChatLine(line)", StringComparison.Ordinal) &&
              macro.Contains("MacroFrameUiLaw.ParseStore", StringComparison.Ordinal) &&
              macro.Contains("MacroFrameUiLaw.WriteStore", StringComparison.Ordinal) &&
              macro.Contains("\"account.txt\"", StringComparison.Ordinal) &&
              macro.Contains("FileOptions.WriteThrough", StringComparison.Ordinal) &&
              macro.Contains("File.Move(temporary, path, overwrite: true)",
                  StringComparison.Ordinal) &&
              !macro.Contains("Unsupported macro command", StringComparison.Ordinal) &&
              !macro.Contains("Run##macro", StringComparison.Ordinal) &&
              macro.Contains("PlayUiSound(MacroFrameUiLaw.AcceptSound",
                  StringComparison.Ordinal) &&
              !macro.Contains("new Vector2", StringComparison.Ordinal) &&
              !macro.Contains("BeginPopupModal", StringComparison.Ordinal) &&
              !macro.Contains("ImGui.OpenPopup", StringComparison.Ordinal) &&
              mount.Contains("archive.ReadFile(\"(listfile)\")", StringComparison.Ordinal),
            "Macro popup escaped law-owned positioning/state or the actual archive icon catalog");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
