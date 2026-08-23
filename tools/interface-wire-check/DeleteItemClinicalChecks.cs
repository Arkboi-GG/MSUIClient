using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class DeleteItemClinicalChecks
{
    public static void Run()
    {
        Check((ushort)Op.CMSG_DESTROYITEM == 0x0111 &&
              WorldSession.BuildDestroyItemBody(255, 23, 0)
                  .SequenceEqual(new byte[] { 255, 23, 0, 0, 0, 0 }) &&
              WorldSession.BuildDestroyItemBody(19, 3, 5)
                  .SequenceEqual(new byte[] { 19, 3, 5, 0, 0, 0 }),
            "CMSG_DESTROYITEM six-byte body drift");
        Check(DeleteItemUiLaw.Width == 420 && DeleteItemUiLaw.TextWidth == 290 &&
              DeleteItemUiLaw.ScreenTop == 128 && DeleteItemUiLaw.AlertSize == 64 &&
              DeleteItemUiLaw.Height(12) == 72 &&
              DeleteItemUiLaw.ButtonOneX(420) == 76 &&
              DeleteItemUiLaw.ButtonTwoX(420) == 217 &&
              DeleteItemUiLaw.Text("Tough Jerky") ==
                  "Do you want to destroy Tough Jerky?" &&
              DeleteItemUiLaw.Definition.WhileDead &&
              DeleteItemUiLaw.Definition.HideOnEscape &&
              DeleteItemUiLaw.Definition.ShowAlert,
            "DELETE_ITEM StaticPopup law drift");
        DeleteItemUiLaw.PopupLayout layout = DeleteItemUiLaw.Layout(12);
        Check(layout.Size == new System.Numerics.Vector2(420, 72) &&
              layout.Text == new StaticPopupCoordinatorLaw.Rect(65, 16, 290, 12) &&
              layout.Alert == new StaticPopupCoordinatorLaw.Rect(12, 4, 64, 64) &&
              layout.Button1 == new StaticPopupCoordinatorLaw.Rect(76, 36, 128, 20) &&
              layout.Button2 == new StaticPopupCoordinatorLaw.Rect(217, 36, 128, 20) &&
              DeleteItemUiLaw.TextLineCenter(layout, 12, 0) ==
                  new System.Numerics.Vector2(210, 22),
            "DELETE_ITEM showAlert child layout drift");
        Check(StaticPopupCoordinatorLaw.ScreenOrigin(
                  new System.Numerics.Vector2(1920, 1080), 420, 1.5f, 1, 72) ==
                  new System.Numerics.Vector2(645, 192) &&
              StaticPopupCoordinatorLaw.ScreenOrigin(
                  new System.Numerics.Vector2(1920, 1080), 420, 1.5f, 2, 72) ==
                  new System.Numerics.Vector2(645, 312),
            "StaticPopup centered first/second slot screen geometry drift");

        StaticPopupCoordinatorLaw.Plan shown = StaticPopupCoordinatorLaw.Show(
            StaticPopupCoordinatorLaw.Slots.Empty, DeleteItemUiLaw.Definition,
            playerDeadOrGhost: true, dataToken: "Tough Jerky");
        Check(shown.Outcome == StaticPopupCoordinatorLaw.Outcome.Shown && shown.Slot == 1 &&
              DeleteItemUiLaw.Visible(shown.Slots)?.Instance.DataToken == "Tough Jerky",
            "DELETE_ITEM while-dead/show coordinator drift");
        Check(StaticPopupCoordinatorLaw.Click(shown.Slots, 1, 1).Effects
                  .Any(effect => effect.Kind == StaticPopupCoordinatorLaw.EffectKind.Accept) &&
              StaticPopupCoordinatorLaw.Escape(shown.Slots).Effects
                  .Any(effect => effect.Kind == StaticPopupCoordinatorLaw.EffectKind.CancelClicked),
            "DELETE_ITEM Yes/Escape callback route drift");

        string root = ClientConfig.FindRepoRoot();
        string flow = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.DeleteItem.cs"));
        string inventory = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Inventory.cs"));
        Check(flow.Contains("ImGui.IsMouseReleased(ImGuiMouseButton.Left)", StringComparison.Ordinal) &&
              flow.Contains("ImGui.IsAnyItemHovered()", StringComparison.Ordinal) &&
              flow.Contains("_net.DestroyItem(wire.Bag, wire.Slot, pending.Count)",
                  StringComparison.Ordinal) &&
              flow.Contains("AddPendingBagLock(pending.Container, pending.Slot",
                  StringComparison.Ordinal) &&
              flow.Contains("StaticPopupOrigin", StringComparison.Ordinal) &&
              flow.Contains("StaticPopupCoordinatorLaw.ScreenOrigin", StringComparison.Ordinal) &&
              flow.Contains("DeleteItemUiLaw.Layout", StringComparison.Ordinal) &&
              !flow.Contains("new Vector2", StringComparison.Ordinal) &&
              !flow.Contains("return new(", StringComparison.Ordinal) &&
              !flow.Contains("BeginVanillaWindow", StringComparison.Ordinal) &&
              inventory.Contains("TryOpenDeleteItemConfirmation()", StringComparison.Ordinal),
            "world-drop confirm, pending lock, or law-positioned modal is unwired");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
