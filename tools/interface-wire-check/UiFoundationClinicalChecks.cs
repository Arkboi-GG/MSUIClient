using MSUIClient;
using MSUIClient.Engine.UI;

internal static class UiFoundationClinicalChecks
{
    public static void Run()
    {
        CheckButtonLaw();
        CheckTabLaw();
        CheckPanelOwnershipLaw();
        CheckPopupCoordinatorLaw();
        CheckFadeLaw();
        CheckVanillaUiAdapter();
    }

    private static void CheckButtonLaw()
    {
        Check(ButtonInteractionLaw.DefaultRegisteredClicks.SetEquals(["LeftButtonUp"]),
            "button default click registration drift");
        Check(!ButtonInteractionLaw.PhysicalTransition(true, null, "LeftButton",
                  ButtonInteractionLaw.Edge.Down, false, true, false, false).FireOnClick &&
              ButtonInteractionLaw.PhysicalTransition(true, null, "LeftButton",
                  ButtonInteractionLaw.Edge.Up, true, true, false, false).FireOnClick &&
              !ButtonInteractionLaw.PhysicalTransition(true, null, "LeftButton",
                  ButtonInteractionLaw.Edge.Up, true, false, false, false).FireOnClick &&
              !ButtonInteractionLaw.PhysicalTransition(true, null, "LeftButton",
                  ButtonInteractionLaw.Edge.Up, false, true, false, false).FireOnClick,
            "button default release/same-owner/drag-off law drift");

        IReadOnlySet<string> rightDown = ButtonInteractionLaw.RegisterForClicks(
            ["rightbuttondown"]);
        ButtonInteractionLaw.ClickPlan down = ButtonInteractionLaw.PhysicalTransition(
            true, rightDown, "RightButton", ButtonInteractionLaw.Edge.Down,
            ownsOriginatingPress: false, pointerInside: true, isCheckButton: false,
            checkedBefore: false);
        Check(down.FireOnClick && down.DownArgument &&
              !ButtonInteractionLaw.WantsClick(rightDown, "LeftButton",
                  ButtonInteractionLaw.Edge.Up),
            "button RegisterForClicks replacement/case/down argument drift");

        ButtonInteractionLaw.ClickPlan check = ButtonInteractionLaw.PhysicalTransition(
            true, null, "LeftButton", ButtonInteractionLaw.Edge.Up,
            ownsOriginatingPress: true, pointerInside: true, isCheckButton: true,
            checkedBefore: false);
        ButtonInteractionLaw.ClickPlan disabled = ButtonInteractionLaw.PhysicalTransition(
            false, null, "LeftButton", ButtonInteractionLaw.Edge.Up,
            ownsOriginatingPress: true, pointerInside: true, isCheckButton: true,
            checkedBefore: false);
        ButtonInteractionLaw.ClickPlan programmatic = ButtonInteractionLaw.ProgrammaticClick(
            true, isCheckButton: true, checkedBefore: false);
        Check(check.FireOnClick && check.ToggleCheckedBeforeCallback && check.CheckedAfter &&
              !check.DownArgument && !disabled.FireOnClick && !disabled.CheckedAfter &&
              programmatic.FireOnClick && programmatic.ToggleCheckedBeforeCallback &&
              programmatic.CheckedAfter && !programmatic.DownArgument,
            "button enabled/check-toggle/programmatic release law drift");

        ButtonInteractionLaw.Visual heldInside = ButtonInteractionLaw.ResolveVisual(
            true, true, true, false, false, false);
        ButtonInteractionLaw.Visual dragOff = ButtonInteractionLaw.ResolveVisual(
            true, false, true, false, false, false);
        ButtonInteractionLaw.Visual scripted = ButtonInteractionLaw.ResolveVisual(
            true, false, false, true, false, false);
        ButtonInteractionLaw.Visual pushedFallback = ButtonInteractionLaw.ResolveVisual(
            true, true, true, false, false, false, hasPushedTexture: false);
        ButtonInteractionLaw.Visual missingDisabled = ButtonInteractionLaw.ResolveVisual(
            false, false, false, false, false, false, hasDisabledTexture: false);
        ButtonInteractionLaw.Visual locked = ButtonInteractionLaw.ResolveVisual(
            true, false, false, false, false, true);
        ButtonInteractionLaw.Visual disabledChecked = ButtonInteractionLaw.ResolveVisual(
            false, false, false, false, true, false);
        Check(heldInside.PrimaryTexture == ButtonInteractionLaw.TextureSlot.Pushed &&
              heldInside.Pushed && dragOff.PrimaryTexture == ButtonInteractionLaw.TextureSlot.Normal &&
              !dragOff.Pushed && scripted.PrimaryTexture == ButtonInteractionLaw.TextureSlot.Pushed &&
              pushedFallback.PrimaryTexture == ButtonInteractionLaw.TextureSlot.Normal &&
              missingDisabled.PrimaryTexture == ButtonInteractionLaw.TextureSlot.None &&
              missingDisabled.LabelState == ButtonInteractionLaw.LabelState.Disabled &&
              locked.HighlightVisible && locked.LabelState == ButtonInteractionLaw.LabelState.Normal &&
              disabledChecked.DisabledCheckedVisible && !disabledChecked.CheckedVisible,
            "button region visibility/pushed fallback/highlight/check art law drift");

        Check(!ButtonInteractionLaw.CoerceChecked(0) &&
              !ButtonInteractionLaw.CoerceChecked(.9) &&
              ButtonInteractionLaw.CoerceChecked(1) &&
              ButtonInteractionLaw.CoerceChecked(true) &&
              !ButtonInteractionLaw.CoerceChecked("false") &&
              ButtonInteractionLaw.CoerceChecked("2.7") &&
              !ButtonInteractionLaw.CoerceChecked("0.9"),
            "button SetChecked numeric/string coercion drift");
    }

    private static void CheckTabLaw()
    {
        PanelTabLaw.Visual selected = PanelTabLaw.Resolve(true, false, true);
        PanelTabLaw.Visual disabled = PanelTabLaw.Resolve(true, true, true);
        PanelTabLaw.Visual hover = PanelTabLaw.Resolve(false, false, true);
        Check(selected.State == PanelTabLaw.State.Selected && !selected.Enabled &&
              selected.ShowActiveSlices && !selected.ShowHoverHighlight &&
              selected.LabelPaint == PanelTabLaw.LabelPaint.Highlight &&
              disabled.State == PanelTabLaw.State.Disabled && !disabled.Enabled &&
              disabled.ShowInactiveSlices && !disabled.ShowActiveSlices &&
              disabled.LabelPaint == PanelTabLaw.LabelPaint.Gray &&
              hover.Enabled && hover.ShowInactiveSlices && hover.ShowHoverHighlight &&
              hover.LabelPaint == PanelTabLaw.LabelPaint.Highlight,
            "panel tab selected/disabled/hover state drift");

        PanelTabLaw.Fit defaultPad = PanelTabLaw.Resize(50, 20);
        PanelTabLaw.Fit zeroPad = PanelTabLaw.Resize(50, 20, padding: 0);
        PanelTabLaw.Fit belowCaps = PanelTabLaw.Resize(50, 20, absoluteSize: 30);
        PanelTabLaw.Fit maxQuirk = PanelTabLaw.Resize(100, 20, padding: 10, maxWidth: 80);
        PanelTabLaw.Fit room = PanelTabLaw.FitWithinParent(100, 20,
            tabLeft: 10, parentRight: 100, rightInset: 10, padding: 0);
        Check(defaultPad == new PanelTabLaw.Fit(114, 74, 0, true, false, false) &&
              zeroPad == new PanelTabLaw.Fit(90, 50, 0, true, false, false) &&
              belowCaps == new PanelTabLaw.Fit(40, 1, 1, false, true, false) &&
              maxQuirk == new PanelTabLaw.Fit(130, 90, 90, false, false, false) &&
              room == new PanelTabLaw.Fit(80, 40, 40, false, true, true) &&
              PanelTabLaw.Room(10, 100, 10) == 80 &&
              !PanelTabLaw.NeedsSettle(0, 80, null, null) &&
              !PanelTabLaw.NeedsSettle(50, 80, 50, 80) &&
              PanelTabLaw.NeedsSettle(50, 80, 49, 80),
            "panel tab resize/max-width/room/settle law drift");
    }

    private static void CheckPanelOwnershipLaw()
    {
        var menu = new UiPanelOwnershipLaw.Panel("Menu", UiPanelOwnershipLaw.Area.Center);
        var map = new UiPanelOwnershipLaw.Panel("Map", UiPanelOwnershipLaw.Area.Fullscreen,
            WhileDead: true);
        var a = new UiPanelOwnershipLaw.Panel("A", UiPanelOwnershipLaw.Area.Left, 0);
        var b = new UiPanelOwnershipLaw.Panel("B", UiPanelOwnershipLaw.Area.Left, 0);

        UiPanelOwnershipLaw.Transition unregistered = UiPanelOwnershipLaw.Show(
            new(null, menu, map),
            new("Loose", UiPanelOwnershipLaw.Area.Unregistered), playerDeadOrGhost: true);
        Check(unregistered.Outcome == UiPanelOwnershipLaw.Outcome.Opened &&
              unregistered.Seats == new UiPanelOwnershipLaw.Seats(null, menu, map) &&
              EffectKinds(unregistered.Effects).SequenceEqual(
                  [UiPanelOwnershipLaw.EffectKind.Show]),
            "unregistered UI panel bypass drift");

        UiPanelOwnershipLaw.Transition nativeForce = UiPanelOwnershipLaw.Show(
            new(null, menu, null), a, force: true);
        Check(nativeForce.Outcome == UiPanelOwnershipLaw.Outcome.RefusedByNativeCenter &&
              nativeForce.Effects.Count == 0,
            "frozen native-center pre-gate/force quirk drift");
        Check(UiPanelOwnershipLaw.Show(UiPanelOwnershipLaw.Seats.Empty, a,
                  playerDeadOrGhost: true).Outcome ==
              UiPanelOwnershipLaw.Outcome.RefusedWhileDead &&
              UiPanelOwnershipLaw.Show(UiPanelOwnershipLaw.Seats.Empty,
                  a with { WhileDead = true }, playerDeadOrGhost: true).Outcome ==
              UiPanelOwnershipLaw.Outcome.Opened,
            "UI panel while-dead gate drift");

        UiPanelOwnershipLaw.Transition fullRefusal = UiPanelOwnershipLaw.Show(
            new(null, null, map), a);
        UiPanelOwnershipLaw.Transition fullForce = UiPanelOwnershipLaw.Show(
            new(null, null, map), a, force: true);
        Check(fullRefusal.Outcome == UiPanelOwnershipLaw.Outcome.RefusedByFullscreen &&
              fullForce.Seats == new UiPanelOwnershipLaw.Seats(a, null, null) &&
              EffectKinds(fullForce.Effects).SequenceEqual([
                  UiPanelOwnershipLaw.EffectKind.Hide,
                  UiPanelOwnershipLaw.EffectKind.AnchorLeft,
                  UiPanelOwnershipLaw.EffectKind.Show]),
            "UI panel fullscreen refusal/force eviction drift");

        UiPanelOwnershipLaw.Transition replace = UiPanelOwnershipLaw.Show(new(a, null, null), b);
        Check(replace.Seats.Left == b && EffectKinds(replace.Effects).SequenceEqual([
                  UiPanelOwnershipLaw.EffectKind.Hide,
                  UiPanelOwnershipLaw.EffectKind.AnchorLeft,
                  UiPanelOwnershipLaw.EffectKind.Show]),
            "UI panel zero-push replacement drift");

        var high = a with { Pushable = 2 };
        var low = b with { Pushable = 1 };
        UiPanelOwnershipLaw.Transition moveOldCenter = UiPanelOwnershipLaw.Show(
            new(high, null, null), low);
        Check(moveOldCenter.Seats == new UiPanelOwnershipLaw.Seats(low, high, null) &&
              EffectKinds(moveOldCenter.Effects).SequenceEqual([
                  UiPanelOwnershipLaw.EffectKind.AnchorCenter,
                  UiPanelOwnershipLaw.EffectKind.Raise,
                  UiPanelOwnershipLaw.EffectKind.AnchorLeft,
                  UiPanelOwnershipLaw.EffectKind.Show]),
            "UI panel higher-priority old-left promotion drift");

        UiPanelOwnershipLaw.Transition putIncomingCenter = UiPanelOwnershipLaw.Show(
            new(low, null, null), high);
        Check(putIncomingCenter.Seats == new UiPanelOwnershipLaw.Seats(low, high, null) &&
              EffectKinds(putIncomingCenter.Effects).SequenceEqual([
                  UiPanelOwnershipLaw.EffectKind.Show,
                  UiPanelOwnershipLaw.EffectKind.AnchorCenter]),
            "UI panel incoming center placement drift");

        var center = new UiPanelOwnershipLaw.Panel("CenterLeft", UiPanelOwnershipLaw.Area.Left, 1);
        var incoming = new UiPanelOwnershipLaw.Panel("Incoming", UiPanelOwnershipLaw.Area.Left, 2);
        UiPanelOwnershipLaw.Transition displaceBoth = UiPanelOwnershipLaw.Show(
            new(a, center, null), incoming);
        Check(displaceBoth.Seats == new UiPanelOwnershipLaw.Seats(center, incoming, null) &&
              EffectKinds(displaceBoth.Effects).SequenceEqual([
                  UiPanelOwnershipLaw.EffectKind.Hide,
                  UiPanelOwnershipLaw.EffectKind.AnchorLeft,
                  UiPanelOwnershipLaw.EffectKind.Show,
                  UiPanelOwnershipLaw.EffectKind.AnchorCenter]),
            "UI panel occupied-seat push priority drift");

        UiPanelOwnershipLaw.Transition promote = UiPanelOwnershipLaw.Hide(
            new(a, center, null), a);
        Check(promote.Seats == new UiPanelOwnershipLaw.Seats(center, null, null) &&
              EffectKinds(promote.Effects).SequenceEqual([
                  UiPanelOwnershipLaw.EffectKind.Hide,
                  UiPanelOwnershipLaw.EffectKind.AnchorLeft]),
            "UI panel left-hide center promotion drift");

        UiPanelOwnershipLaw.Transition closeCaptured = UiPanelOwnershipLaw.CloseWindows(
            new(a, center, null), ignoreNativeCenter: false);
        UiPanelOwnershipLaw.Transition ignoreCenter = UiPanelOwnershipLaw.CloseWindows(
            new(a, menu, null), ignoreNativeCenter: true);
        Check(closeCaptured.Seats == UiPanelOwnershipLaw.Seats.Empty &&
              EffectKinds(closeCaptured.Effects).SequenceEqual([
                  UiPanelOwnershipLaw.EffectKind.Hide,
                  UiPanelOwnershipLaw.EffectKind.AnchorLeft,
                  UiPanelOwnershipLaw.EffectKind.Hide]) &&
              ignoreCenter.Seats == new UiPanelOwnershipLaw.Seats(null, menu, null) &&
              EffectKinds(ignoreCenter.Effects).SequenceEqual([
                  UiPanelOwnershipLaw.EffectKind.Hide]),
            "UI panel captured-center close/ignore-native-center drift");

        UiPanelOwnershipLaw.Transition fullOpen = UiPanelOwnershipLaw.Show(
            UiPanelOwnershipLaw.Seats.Empty, map);
        UiPanelOwnershipLaw.Transition centerOpen = UiPanelOwnershipLaw.Show(
            UiPanelOwnershipLaw.Seats.Empty, menu);
        Check(EffectKinds(fullOpen.Effects).SequenceEqual([
                  UiPanelOwnershipLaw.EffectKind.CloseEscapeContainers,
                  UiPanelOwnershipLaw.EffectKind.Show]) &&
              EffectKinds(centerOpen.Effects).SequenceEqual([
                  UiPanelOwnershipLaw.EffectKind.CloseAllBags,
                  UiPanelOwnershipLaw.EffectKind.Show]),
            "full/native-center close ordering drift");
    }

    private static void CheckPopupCoordinatorLaw()
    {
        var cancellable = new StaticPopupCoordinatorLaw.Definition("A",
            HideOnEscape: true, HasAccept: true, HasCancel: true, HasOnHide: true);
        var other = new StaticPopupCoordinatorLaw.Definition("B",
            HideOnEscape: true, HasCancel: true, HasOnHide: true);
        var survivor = new StaticPopupCoordinatorLaw.Definition("SURVIVE",
            WhileDead: true, HasCancel: true, HasOnHide: true);

        StaticPopupCoordinatorLaw.Plan dead = StaticPopupCoordinatorLaw.Show(
            StaticPopupCoordinatorLaw.Slots.Empty, cancellable, playerDeadOrGhost: true);
        Check(dead.Outcome == StaticPopupCoordinatorLaw.Outcome.RefusedWhileDead &&
              PopupKinds(dead.Effects).SequenceEqual([
                  StaticPopupCoordinatorLaw.EffectKind.CancelWithoutReason]),
            "StaticPopup while-dead refusal drift");

        StaticPopupCoordinatorLaw.Slots full = new(
            Popup(cancellable), Popup(other));
        var noRoomDef = new StaticPopupCoordinatorLaw.Definition("C", HasCancel: true);
        StaticPopupCoordinatorLaw.Plan noRoom = StaticPopupCoordinatorLaw.Show(
            full, noRoomDef, playerDeadOrGhost: false);
        Check(noRoom.Outcome == StaticPopupCoordinatorLaw.Outcome.RefusedNoFreeSlot &&
              PopupKinds(noRoom.Effects).SequenceEqual([
                  StaticPopupCoordinatorLaw.EffectKind.CancelWithoutReason]),
            "StaticPopup no-free-instance refusal drift");

        var cancelling = new StaticPopupCoordinatorLaw.Definition("NEW", Cancels: "A");
        StaticPopupCoordinatorLaw.Plan cancels = StaticPopupCoordinatorLaw.Show(
            new(Popup(cancellable), null), cancelling, false);
        Check(PopupKinds(cancels.Effects).Take(4).SequenceEqual([
                  StaticPopupCoordinatorLaw.EffectKind.Hide,
                  StaticPopupCoordinatorLaw.EffectKind.MainMenuCloseSound,
                  StaticPopupCoordinatorLaw.EffectKind.OnHide,
                  StaticPopupCoordinatorLaw.EffectKind.CancelOverride]),
            "StaticPopup cancels hide-before-override ordering drift");

        StaticPopupCoordinatorLaw.Plan same = StaticPopupCoordinatorLaw.Show(
            new(Popup(cancellable), null), cancellable, false);
        Check(same.Slot == 1 && PopupKinds(same.Effects).Take(4).SequenceEqual([
                  StaticPopupCoordinatorLaw.EffectKind.CancelOverride,
                  StaticPopupCoordinatorLaw.EffectKind.Hide,
                  StaticPopupCoordinatorLaw.EffectKind.MainMenuCloseSound,
                  StaticPopupCoordinatorLaw.EffectKind.OnHide]),
            "StaticPopup same-type override/reuse ordering drift");

        var death = new StaticPopupCoordinatorLaw.Definition("DEATH", WhileDead: true);
        StaticPopupCoordinatorLaw.Plan deathSweep = StaticPopupCoordinatorLaw.Show(
            new(Popup(cancellable), Popup(survivor)), death, false);
        Check(deathSweep.Slot == 1 && deathSweep.Slots.Second?.Definition.Type == "SURVIVE" &&
              PopupKinds(deathSweep.Effects).Take(4).SequenceEqual([
                  StaticPopupCoordinatorLaw.EffectKind.Hide,
                  StaticPopupCoordinatorLaw.EffectKind.MainMenuCloseSound,
                  StaticPopupCoordinatorLaw.EffectKind.OnHide,
                  StaticPopupCoordinatorLaw.EffectKind.CancelOverride]),
            "StaticPopup DEATH non-whileDead sweep drift");

        var sounded = new StaticPopupCoordinatorLaw.Definition("SOUND",
            HasOnShow: true, EntrySound: "igPlayerInvite");
        StaticPopupCoordinatorLaw.Plan show = StaticPopupCoordinatorLaw.Show(
            StaticPopupCoordinatorLaw.Slots.Empty, sounded, false);
        Check(PopupKinds(show.Effects).SequenceEqual([
                  StaticPopupCoordinatorLaw.EffectKind.PrepareContent,
                  StaticPopupCoordinatorLaw.EffectKind.HideEditBox,
                  StaticPopupCoordinatorLaw.EffectKind.EnableAccept,
                  StaticPopupCoordinatorLaw.EffectKind.Show,
                  StaticPopupCoordinatorLaw.EffectKind.MainMenuOpenSound,
                  StaticPopupCoordinatorLaw.EffectKind.OnShow,
                  StaticPopupCoordinatorLaw.EffectKind.Resize,
                  StaticPopupCoordinatorLaw.EffectKind.EntrySound]),
            "StaticPopup show/open/resize/entry-sound ordering drift");
        Check(StaticPopupCoordinatorLaw.Show(new(Popup(cancellable), null), other, false).Slot == 2,
            "StaticPopup first-free slot selection drift");

        StaticPopupCoordinatorLaw.Plan direct = StaticPopupCoordinatorLaw.HideByType(
            new(Popup(cancellable), null), "A");
        Check(PopupKinds(direct.Effects).SequenceEqual([
                  StaticPopupCoordinatorLaw.EffectKind.Hide,
                  StaticPopupCoordinatorLaw.EffectKind.MainMenuCloseSound,
                  StaticPopupCoordinatorLaw.EffectKind.OnHide]) &&
              !PopupKinds(direct.Effects).Any(IsCancel),
            "StaticPopup direct-hide/OnCancel separation drift");

        StaticPopupCoordinatorLaw.Plan escape = StaticPopupCoordinatorLaw.Escape(full);
        Check(PopupKinds(escape.Effects).SequenceEqual([
                  StaticPopupCoordinatorLaw.EffectKind.CancelClicked,
                  StaticPopupCoordinatorLaw.EffectKind.Hide,
                  StaticPopupCoordinatorLaw.EffectKind.MainMenuCloseSound,
                  StaticPopupCoordinatorLaw.EffectKind.OnHide,
                  StaticPopupCoordinatorLaw.EffectKind.CancelClicked,
                  StaticPopupCoordinatorLaw.EffectKind.Hide,
                  StaticPopupCoordinatorLaw.EffectKind.MainMenuCloseSound,
                  StaticPopupCoordinatorLaw.EffectKind.OnHide]),
            "StaticPopup two-slot Escape sweep drift");

        StaticPopupCoordinatorLaw.Plan keep = StaticPopupCoordinatorLaw.Click(
            new(Popup(cancellable), null), 1, 1, acceptReturnedKeepOpen: true);
        StaticPopupCoordinatorLaw.Plan editEscape = StaticPopupCoordinatorLaw.EditBoxEscape(
            new(Popup(cancellable), null), 1);
        Check(keep.Outcome == StaticPopupCoordinatorLaw.Outcome.KeptOpen &&
              PopupKinds(keep.Effects).SequenceEqual([
                  StaticPopupCoordinatorLaw.EffectKind.Accept]) &&
              PopupKinds(editEscape.Effects).SequenceEqual([
                  StaticPopupCoordinatorLaw.EffectKind.ClearEditBoxFocus,
                  StaticPopupCoordinatorLaw.EffectKind.Hide,
                  StaticPopupCoordinatorLaw.EffectKind.MainMenuCloseSound,
                  StaticPopupCoordinatorLaw.EffectKind.OnHide]),
            "StaticPopup accept-keep/editbox-Escape drift");

        var timeoutDef = cancellable with { TimeoutSeconds = .1 };
        StaticPopupCoordinatorLaw.Plan timeout = StaticPopupCoordinatorLaw.Advance(
            new(Popup(timeoutDef), null), 1, .2);
        var delayDef = new StaticPopupCoordinatorLaw.Definition("DELAY",
            HasOnUpdate: true, UsesDelayText: true, StartDelaySeconds: .1);
        StaticPopupCoordinatorLaw.Plan delay = StaticPopupCoordinatorLaw.Advance(
            new(Popup(delayDef), null), 1, .2);
        Check(PopupKinds(timeout.Effects).SequenceEqual([
                  StaticPopupCoordinatorLaw.EffectKind.CancelTimeout,
                  StaticPopupCoordinatorLaw.EffectKind.Hide,
                  StaticPopupCoordinatorLaw.EffectKind.MainMenuCloseSound,
                  StaticPopupCoordinatorLaw.EffectKind.OnHide]) &&
              PopupKinds(delay.Effects).SequenceEqual([
                  StaticPopupCoordinatorLaw.EffectKind.RevealDelayedText,
                  StaticPopupCoordinatorLaw.EffectKind.EnableAccept]),
            "StaticPopup timeout/StartDelay early-return drift");

        Check(StaticPopupCoordinatorLaw.Height(12, 20) == 72 &&
              StaticPopupCoordinatorLaw.Height(12, 20, 32, hasEditBox: true) == 112 &&
              StaticPopupCoordinatorLaw.CountdownTextUnit(59.1) == "1|minute" &&
              StaticPopupCoordinatorLaw.CountdownTextUnit(1) == "1|second",
            "StaticPopup resize/countdown-unit drift");
    }

    private static void CheckFadeLaw()
    {
        UiFrameFadeLaw.StartPlan fadeIn = UiFrameFadeLaw.Start(durationSeconds: 1);
        UiFrameFadeLaw.StartPlan fadeOut = UiFrameFadeLaw.Start(UiFrameFadeLaw.Mode.Out,
            durationSeconds: 1);
        UiFrameFadeLaw.StartPlan replacement = UiFrameFadeLaw.Start(
            durationSeconds: 2, startAlpha: .2f, initialTimerSeconds: 0,
            alreadyRegistered: true);
        Check(fadeIn.Alpha == 0 && fadeIn.State.EndAlpha == 1 && fadeIn.ShowFrame &&
              fadeOut.Alpha == 1 && fadeOut.State.EndAlpha == 0 &&
              replacement.Alpha == .2f && replacement.State.TimerSeconds == 0 &&
              !replacement.ShowFrame && !replacement.AddedToRegistry,
            "UIFrameFade defaults/already-registered restart drift");

        UiFrameFadeLaw.Step inMid = UiFrameFadeLaw.Advance(fadeIn.State, .5);
        UiFrameFadeLaw.Step outMid = UiFrameFadeLaw.Advance(fadeOut.State, .25);
        Check(Near(inMid.Alpha, .5f) && Near(outMid.Alpha, .75f) &&
              !inMid.RemovedFromRegistry && !outMid.RemovedFromRegistry,
            "UIFrameFade IN/OUT interpolation drift");

        UiFrameFadeLaw.State holding = new(UiFrameFadeLaw.Mode.In, 0, 0, 1, 0, .1, true);
        UiFrameFadeLaw.Step crossed = UiFrameFadeLaw.Advance(holding, .2);
        UiFrameFadeLaw.Step completed = UiFrameFadeLaw.Advance(crossed.State!.Value, 0);
        Check(crossed.State?.HoldSeconds == -.1 && !crossed.RemovedFromRegistry &&
              completed.RemovedFromRegistry && completed.InvokeFinishedCallback,
            "UIFrameFade hold-crosses-now/completes-next-tick drift");

        UiFrameFadeLaw.RegistryStep skipped = UiFrameFadeLaw.AdvanceRegistry([
            new("A", new(UiFrameFadeLaw.Mode.In, 0, 0, 1, 0, 0, false)),
            new("B", new(UiFrameFadeLaw.Mode.In, 1, 0, 1, 0, 0, false)),
        ], .5);
        Check(skipped.Observations.Count == 1 && skipped.Observations[0].Id == "A" &&
              skipped.Entries.Count == 1 && skipped.Entries[0].Id == "B" &&
              skipped.Entries[0].State.TimerSeconds == 0,
            "UIFrameFade frozen removal-skip drift");

        IReadOnlyList<UiFrameFadeLaw.RegistryEntry> deduped = UiFrameFadeLaw.RemoveFrame([
            new("A", fadeIn.State), new("B", fadeIn.State), new("A", fadeOut.State)], "A");
        Check(deduped.Count == 1 && deduped[0].Id == "B" &&
              UiFrameFadeLaw.IsFading(deduped, "B") &&
              !UiFrameFadeLaw.IsFading(deduped, "A"),
            "UIFrameFade remove-all/is-fading drift");
    }

    private static void CheckVanillaUiAdapter()
    {
        string root = ClientConfig.FindRepoRoot();
        string source = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.VanillaUi.cs"));
        Check(!source.Contains("ImGui.IsItemClicked()", StringComparison.Ordinal) &&
              Count(source, "bool releasedInside = ImGui.InvisibleButton") == 6 &&
              Count(source, "ButtonInteractionLaw.ResolveVisual") == 3 &&
              Count(source, "PanelTabLaw.Resolve") == 4,
            "Program.VanillaUi release-edge/law adapter source drift");
        Check(source.Contains("AdditiveHandle(\n                @\"Interface\\PaperDollInfoFrame\\UI-Character-Tab-Highlight\")",
                  StringComparison.Ordinal) &&
              source.Contains("\"GameFontHighlight\"", StringComparison.Ordinal) &&
              source.Contains("\"GameFontDisable\"", StringComparison.Ordinal) &&
              source.Contains("\"GameFontHighlightSmall\"", StringComparison.Ordinal),
            "Program.VanillaUi additive tab highlight/state font adapter drift");
        Check(source.Contains("new Vector2(10, -2) * scale", StringComparison.Ordinal) &&
              source.Contains("new Vector2(logicalWidth - 10, 30) * scale",
                  StringComparison.Ordinal) &&
              source.Contains("new Vector2(2, 8) * scale", StringComparison.Ordinal) &&
              source.Contains("new Vector2(logicalWidth + 2, 40) * scale",
                  StringComparison.Ordinal),
            "Program.VanillaUi authored tab-highlight containment seat drift");
    }

    private static StaticPopupCoordinatorLaw.Instance Popup(
        StaticPopupCoordinatorLaw.Definition definition) =>
        new(definition, null, Math.Max(0, definition.TimeoutSeconds),
            definition.StartDelaySeconds);

    private static IEnumerable<UiPanelOwnershipLaw.EffectKind> EffectKinds(
        IReadOnlyList<UiPanelOwnershipLaw.Effect> effects) => effects.Select(x => x.Kind);

    private static IEnumerable<StaticPopupCoordinatorLaw.EffectKind> PopupKinds(
        IReadOnlyList<StaticPopupCoordinatorLaw.Effect> effects) => effects.Select(x => x.Kind);

    private static bool IsCancel(StaticPopupCoordinatorLaw.EffectKind kind) => kind is
        StaticPopupCoordinatorLaw.EffectKind.CancelWithoutReason or
        StaticPopupCoordinatorLaw.EffectKind.CancelOverride or
        StaticPopupCoordinatorLaw.EffectKind.CancelClicked or
        StaticPopupCoordinatorLaw.EffectKind.CancelTimeout;

    private static int Count(string source, string value)
    {
        int count = 0;
        int start = 0;
        while ((start = source.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }
        return count;
    }

    private static bool Near(float a, float b) => MathF.Abs(a - b) < .0001f;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
