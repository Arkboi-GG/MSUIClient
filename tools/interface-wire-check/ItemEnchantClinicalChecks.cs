using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class ItemEnchantClinicalChecks
{
    public static void Run()
    {
        var writer = new PacketWriter();
        writer.WriteU64(0x1122_3344_5566_7788);
        writer.WriteU32(1);
        writer.WriteU32(90);
        writer.WriteU64(0x8877_6655_4433_2211);
        ItemEnchantTimePacket packet = ItemEnchantTimePackets.Parse(writer.ToArray());
        Check(packet == new ItemEnchantTimePacket(0x1122_3344_5566_7788, 1, 90,
                0x8877_6655_4433_2211),
            "item enchant time 24-byte body drift");
        CheckThrows(() => ItemEnchantTimePackets.Parse(new byte[23]));
        CheckThrows(() => ItemEnchantTimePackets.Parse(new byte[25]));

        var timers = new ItemEnchantTimerState();
        timers.Set(10, 1, 90, 100);
        Check(timers.RemainingMilliseconds(10, 1, 90, 130) == 60_000,
            "temporary enchant deadline integration drift");
        timers.Set(10, 1, 0, 130);
        Check(timers.RemainingMilliseconds(10, 1, 0, 130) == 0 &&
              timers.RemainingMilliseconds(11, 1, 90, 130) == 0 &&
              timers.RemainingMilliseconds(11, 1, 0, 130) is null,
            "temporary enchant elapsed/field-before-packet timer shape drift");

        Check(ItemEnchantUiLaw.Countdown("Rockbiter", 1_900) == "Rockbiter (1 sec)" &&
              ItemEnchantUiLaw.Countdown("Rockbiter", 241_000) == "Rockbiter (5 min)" &&
              ItemEnchantUiLaw.Countdown("Rockbiter", 3_600_001) == "Rockbiter (2 hrs)" &&
              ItemEnchantUiLaw.Countdown("Rockbiter", 86_400_001) == "Rockbiter (2 days)" &&
              ItemEnchantUiLaw.Text("Poison", null, 1) == "Poison (1 Charge)" &&
              ItemEnchantUiLaw.Color(0, 1) == new Vector4(0, 1, 0, 1) &&
              ItemEnchantUiLaw.Color(1, -1) == new Vector4(1, 0, 0, 1) &&
              ItemEnchantUiLaw.Color(2, -1) == Vector4.One,
            "item enchant tooltip color/countdown/charge law drift");

        Check(WorldCursorUiLaw.ItemTargeting(pointerOverUi: true) ==
                  new WorldCursorState(WorldCursorKind.Cast, Unable: false) &&
              WorldCursorUiLaw.ItemTargeting(pointerOverUi: false) ==
                  new WorldCursorState(WorldCursorKind.Cast, Unable: true) &&
              WorldCursorUiLaw.ItemTargeting(pointerOverUi: false).Stem == "UnableCast",
            "item-target Enchanting cursor mode drift");

        string root = ClientConfig.FindRepoRoot();
        string dispatch = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string inventory = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Inventory.cs"));
        string worldCursor = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.WorldCursor.cs"));
        string actionBars = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.ActionBars.cs"));
        Check(dispatch.Contains("case Op.SMSG_ITEM_ENCHANT_TIME_UPDATE:", StringComparison.Ordinal) &&
              dispatch.Contains("ApplyItemEnchantTime(body)", StringComparison.Ordinal) &&
              inventory.Contains("ItemEnchantmentCharges(slot)", StringComparison.Ordinal) &&
              inventory.Contains("ItemEnchantmentDuration(slot)", StringComparison.Ordinal) &&
              inventory.Contains("enchant.HidesTooltipName", StringComparison.Ordinal) &&
              inventory.Contains("ItemEnchantUiLaw.Color(slot, signedId)", StringComparison.Ordinal) &&
              worldCursor.Contains("WorldCursorUiLaw.ItemTargeting(pointerOverUi).Stem",
                  StringComparison.Ordinal) &&
              !actionBars.Contains("\"Select item\"", StringComparison.Ordinal),
            "temporary enchant receive or item-tooltip renderer is unwired");

        StaticPopupCoordinatorLaw.Plan bind = StaticPopupCoordinatorLaw.Show(
            StaticPopupCoordinatorLaw.Slots.Empty, EnchantConfirmUiLaw.BindDefinition,
            playerDeadOrGhost: false);
        StaticPopupCoordinatorLaw.Plan cancelled = StaticPopupCoordinatorLaw.Click(
            bind.Slots, bind.Slot!.Value, buttonIndex: 2);
        EnchantConfirmUiLaw.PopupLayout layout = EnchantConfirmUiLaw.Layout(12);
        Check(bind.Outcome == StaticPopupCoordinatorLaw.Outcome.Shown &&
              bind.Slot == 1 &&
              EnchantConfirmUiLaw.Visible(bind.Slots) is { Slot: 1 } &&
              bind.Effects.Any(effect => effect.Kind ==
                  StaticPopupCoordinatorLaw.EffectKind.MainMenuOpenSound) &&
              EnchantConfirmUiLaw.BindDefinition.ShowAlert &&
              EnchantConfirmUiLaw.BindDefinition.HideOnEscape &&
              EnchantConfirmUiLaw.BindDefinition.HasAccept &&
              !EnchantConfirmUiLaw.BindDefinition.HasCancel &&
              EnchantConfirmUiLaw.ReplaceDefinition.ShowAlert &&
              EnchantConfirmUiLaw.ReplaceDefinition.HideOnEscape &&
              EnchantConfirmUiLaw.ReplaceDefinition.HasAccept &&
              !EnchantConfirmUiLaw.ReplaceDefinition.HasCancel &&
              cancelled.Outcome == StaticPopupCoordinatorLaw.Outcome.Cancelled &&
              cancelled.Effects.All(effect => effect.Kind !=
                  StaticPopupCoordinatorLaw.EffectKind.CancelClicked),
            "enchant confirmation StaticPopup lifecycle drift");
        Check(layout.Width == 420 && layout.Height == 72 &&
              layout.Text == new EnchantConfirmUiLaw.LogicalRect(65, 16, 290, 12) &&
              layout.Alert == new EnchantConfirmUiLaw.LogicalRect(12, 4, 64, 64) &&
              layout.AcceptButton ==
                  new EnchantConfirmUiLaw.LogicalRect(76, 36, 128, 20) &&
              layout.DeclineButton ==
                  new EnchantConfirmUiLaw.LogicalRect(217, 36, 128, 20),
            "enchant confirmation Benilla showAlert geometry drift");
        Check(EnchantConfirmUiLaw.ScaledFrame(new Vector2(100, 200), layout, 1.5f) ==
                  new EnchantConfirmUiLaw.ScreenRect(
                      new Vector2(100, 200), new Vector2(630, 108)),
            "enchant confirmation scaled frame geometry drift");

        string confirmation = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Panels", "GameLoop.EnchantConfirm.cs"));
        string popupAdapter = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Hud", "GameLoop.PartyFrames.cs"));
        Check(confirmation.Contains("StaticPopupCoordinatorLaw.Show(", StringComparison.Ordinal) &&
              confirmation.Contains("EnchantConfirmUiLaw.Visible(_staticPopupSlots)",
                  StringComparison.Ordinal) &&
              confirmation.Contains("StaticPopupOrigin(slot, layout.Width, s)",
                  StringComparison.Ordinal) &&
              confirmation.Contains("EnchantConfirmUiLaw.ScaledFrame", StringComparison.Ordinal) &&
              confirmation.Contains("StaticPopupCoordinatorLaw.Click(",
                  StringComparison.Ordinal) &&
              !confirmation.Contains("new Vector2", StringComparison.Ordinal) &&
              !confirmation.Contains("ScreenRect frame = new", StringComparison.Ordinal) &&
              popupAdapter.Contains("effect.Type is EnchantConfirmUiLaw.BindPopupType or",
                  StringComparison.Ordinal) &&
              popupAdapter.Contains("AcceptEnchantConfirmation()", StringComparison.Ordinal),
            "enchant confirmation renderer or shared popup adapter is unwired");
    }

    private static void CheckThrows(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException("malformed item-enchant-time packet was accepted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
