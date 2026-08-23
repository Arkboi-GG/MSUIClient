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
              MacroFrameUiLaw.GeneralTabWidth(60) == 85,
            "MacroFrame main-window/account-character geometry drifted");
        string overflowBody = new('x', 255);
        Check(MacroFrameUiLaw.BodyContentHeight("") == 85 &&
              MacroFrameUiLaw.BodyContentHeight("one\ntwo") == 85 &&
              MacroFrameUiLaw.BodyContentHeight(overflowBody) == 84 + 14 &&
              MacroFrameUiLaw.MaximumBodyScroll(overflowBody) == 13 &&
              MacroFrameUiLaw.WheelBodyScroll(0, overflowBody, -1) == 13 &&
              MacroFrameUiLaw.BodyThumbY(13, overflowBody) == 363,
            "MacroFrame body child sizing/scroll/knob law drifted");
        Check(MacroFrameUiLaw.PopupMinimum(new Vector2(0, 104), 1f) ==
              new Vector2(344, 144),
            "MacroPopup TOPLEFT-on-MacroFrame-TOPRIGHT (-40,-40) seat drifted");
        Check(MacroFrameUiLaw.IconButton(0) == new MacroFrameUiLaw.Rect(24, 85, 36, 36) &&
              MacroFrameUiLaw.IconButton(4) == new MacroFrameUiLaw.Rect(208, 85, 36, 36) &&
              MacroFrameUiLaw.IconButton(5) == new MacroFrameUiLaw.Rect(24, 129, 36, 36) &&
              MacroFrameUiLaw.IconButton(19) == new MacroFrameUiLaw.Rect(208, 217, 36, 36),
            "MacroPopup 5x4 icon geometry drifted");
        Check(MacroFrameUiLaw.NameEdit == new MacroFrameUiLaw.Rect(29, 35, 200, 20) &&
              MacroFrameUiLaw.OkayButton == new MacroFrameUiLaw.Rect(128, 263, 78, 22) &&
              MacroFrameUiLaw.CancelButton == new MacroFrameUiLaw.Rect(208, 263, 78, 22),
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
              macro.Contains("MacroPopup-TopLeft", StringComparison.Ordinal) &&
              macro.Contains("UI-ClassTrainer-FilterBorder", StringComparison.Ordinal) &&
              macro.Contains("OpenMacroPopup(MacroPopupMode.New)", StringComparison.Ordinal) &&
              macro.Contains("OpenMacroPopup(MacroPopupMode.Edit)", StringComparison.Ordinal) &&
              macro.Contains("MacroFrameUiLaw.MacroButton(i)", StringComparison.Ordinal) &&
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
              !macro.Contains("Unsupported macro command", StringComparison.Ordinal) &&
              !macro.Contains("Run##macro", StringComparison.Ordinal) &&
              macro.Contains("PlayUiSound(MacroFrameUiLaw.AcceptSound",
                  StringComparison.Ordinal) &&
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
