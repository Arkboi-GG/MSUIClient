namespace MSUIClient.Engine.UI;

/// <summary>
/// Pure two-slot StaticPopup coordinator from the frozen UiPanels.xml closure. It records callback,
/// visibility, and sound steps separately so adapters cannot accidentally collapse observable
/// order. It does not render a dialog or define any entry-specific business action.
/// </summary>
public static class StaticPopupCoordinatorLaw
{
    public const int SlotCount = 2;
    public const float BaseWidth = 320f;
    public const float BaseHeight = 72f;
    public const float SecondSlotGap = 8f;

    public readonly record struct Definition(
        string Type,
        bool WhileDead = false,
        bool HideOnEscape = false,
        string? Cancels = null,
        bool HasAccept = false,
        bool HasCancel = false,
        bool HasOnShow = false,
        bool HasOnHide = false,
        bool HasOnUpdate = false,
        bool HasEditBox = false,
        bool UsesTimeoutText = false,
        bool UsesDelayText = false,
        double TimeoutSeconds = 0,
        double? StartDelaySeconds = null,
        string? EntrySound = null);

    public readonly record struct Instance(
        Definition Definition,
        string? DataToken,
        double TimeLeft,
        double? StartDelay);

    public readonly record struct Slots(Instance? First, Instance? Second)
    {
        public static Slots Empty => new(null, null);
    }

    public enum EffectKind
    {
        /// <summary>Invoke the entry's OnCancel with no reason argument.</summary>
        CancelWithoutReason,
        CancelOverride,
        CancelClicked,
        CancelTimeout,
        Accept,

        PrepareContent,
        ClearEditBox,
        ShowEditBox,
        HideEditBox,
        DisableAccept,
        EnableAccept,
        RevealDelayedText,
        UpdateCountdownText,
        UpdateDelayText,

        Show,
        Hide,
        MainMenuOpenSound,
        MainMenuCloseSound,
        OnShow,
        OnHide,
        Resize,
        EntrySound,
        OnUpdate,
        ClearEditBoxFocus,
    }

    public readonly record struct Effect(
        EffectKind Kind,
        int Slot,
        string Type,
        string? Value = null);

    public enum Outcome
    {
        Shown,
        RefusedWhileDead,
        RefusedNoFreeSlot,
        Hidden,
        NothingVisible,
        KeptOpen,
        Accepted,
        Cancelled,
        TimedOut,
        Advanced,
    }

    public readonly record struct Plan(
        Slots Slots,
        IReadOnlyList<Effect> Effects,
        Outcome Outcome,
        int? Slot = null);

    /// <summary>
    /// Plans StaticPopup_Show. Exact frozen ordering is preserved:
    /// cancels/DEATH hide before override-cancel; same-type replacement override-cancels before
    /// hide; refusal calls OnCancel without a reason; Show synchronously runs open sound/OnShow,
    /// Resize follows, and the entry sound is last.
    /// </summary>
    public static Plan Show(
        Slots slots,
        Definition request,
        bool playerDeadOrGhost,
        string? dataToken = null)
    {
        var effects = new List<Effect>();
        if (playerDeadOrGhost && !request.WhileDead)
        {
            AppendCancel(request, EffectKind.CancelWithoutReason, 0, effects);
            return new(slots, effects, Outcome.RefusedWhileDead);
        }

        if (!string.IsNullOrEmpty(request.Cancels) &&
            Find(slots, request.Cancels!) is int cancelledSlot)
        {
            Instance old = Get(slots, cancelledSlot)!.Value;
            AppendHide(old, cancelledSlot, effects);
            AppendCancel(old.Definition, EffectKind.CancelOverride, cancelledSlot, effects);
            slots = Set(slots, cancelledSlot, null);
        }

        if (string.Equals(request.Type, "DEATH", StringComparison.Ordinal))
        {
            for (int slot = 1; slot <= SlotCount; slot++)
            {
                if (Get(slots, slot) is not { } old || old.Definition.WhileDead)
                    continue;
                AppendHide(old, slot, effects);
                AppendCancel(old.Definition, EffectKind.CancelOverride, slot, effects);
                slots = Set(slots, slot, null);
            }
        }

        int? target = Find(slots, request.Type);
        if (target is int sameTypeSlot)
        {
            Instance old = Get(slots, sameTypeSlot)!.Value;
            AppendCancel(old.Definition, EffectKind.CancelOverride, sameTypeSlot, effects);
            AppendHide(old, sameTypeSlot, effects);
            slots = Set(slots, sameTypeSlot, null);
        }
        else
        {
            target = FirstFree(slots);
        }

        if (target is null)
        {
            AppendCancel(request, EffectKind.CancelWithoutReason, 0, effects);
            return new(slots, effects, Outcome.RefusedNoFreeSlot);
        }

        int selected = target.Value;
        var instance = new Instance(
            request,
            dataToken,
            Math.Max(0, request.TimeoutSeconds),
            request.StartDelaySeconds is { } delay ? Math.Max(0, delay) : null);
        slots = Set(slots, selected, instance);

        effects.Add(new(EffectKind.PrepareContent, selected, request.Type));
        if (request.HasEditBox)
        {
            effects.Add(new(EffectKind.ClearEditBox, selected, request.Type));
            effects.Add(new(EffectKind.ShowEditBox, selected, request.Type));
        }
        else
        {
            effects.Add(new(EffectKind.HideEditBox, selected, request.Type));
        }
        effects.Add(new(request.StartDelaySeconds is not null
            ? EffectKind.DisableAccept
            : EffectKind.EnableAccept, selected, request.Type));
        AppendShow(instance, selected, effects);
        effects.Add(new(EffectKind.Resize, selected, request.Type));
        if (!string.IsNullOrEmpty(request.EntrySound))
            effects.Add(new(EffectKind.EntrySound, selected, request.Type, request.EntrySound));
        return new(slots, effects, Outcome.Shown, selected);
    }

    /// <summary>StaticPopup_Hide: direct hide only; it never invokes OnCancel.</summary>
    public static Plan HideByType(Slots slots, string type)
    {
        var effects = new List<Effect>();
        int? first = null;
        for (int slot = 1; slot <= SlotCount; slot++)
        {
            if (Get(slots, slot) is not { } instance ||
                !string.Equals(instance.Definition.Type, type, StringComparison.Ordinal))
                continue;
            first ??= slot;
            AppendHide(instance, slot, effects);
            slots = Set(slots, slot, null);
        }
        return new(slots, effects,
            first is null ? Outcome.NothingVisible : Outcome.Hidden, first);
    }

    /// <summary>
    /// Global StaticPopup_EscapePressed sweeps both slots in index order. Each eligible entry gets
    /// OnCancel("clicked") before the ordinary hide lifecycle; non-hideOnEscape entries remain.
    /// </summary>
    public static Plan Escape(Slots slots)
    {
        var effects = new List<Effect>();
        int? first = null;
        for (int slot = 1; slot <= SlotCount; slot++)
        {
            if (Get(slots, slot) is not { } instance || !instance.Definition.HideOnEscape)
                continue;
            first ??= slot;
            AppendCancel(instance.Definition, EffectKind.CancelClicked, slot, effects);
            AppendHide(instance, slot, effects);
            slots = Set(slots, slot, null);
        }
        return new(slots, effects,
            first is null ? Outcome.NothingVisible : Outcome.Cancelled, first);
    }

    /// <summary>
    /// Plans a dialog button click after the entry callback has reported whether accept should
    /// keep the dialog open. <paramref name="typeStillSame"/> models callbacks which synchronously
    /// replace the slot; the frozen click driver hides only while the original type still owns it.
    /// </summary>
    public static Plan Click(
        Slots slots,
        int slot,
        int buttonIndex,
        bool acceptReturnedKeepOpen = false,
        bool typeStillSame = true)
    {
        Instance? current = Get(slots, slot);
        if (current is null)
            return new(slots, [], Outcome.NothingVisible);

        Instance instance = current.Value;
        var effects = new List<Effect>();
        if (buttonIndex == 1)
        {
            if (instance.Definition.HasAccept)
                effects.Add(new(EffectKind.Accept, slot, instance.Definition.Type));
            if (acceptReturnedKeepOpen)
                return new(slots, effects, Outcome.KeptOpen, slot);
        }
        else
        {
            AppendCancel(instance.Definition, EffectKind.CancelClicked, slot, effects);
        }

        if (typeStillSame)
        {
            AppendHide(instance, slot, effects);
            slots = Set(slots, slot, null);
        }
        return new(slots, effects,
            buttonIndex == 1 ? Outcome.Accepted : Outcome.Cancelled, slot);
    }

    /// <summary>
    /// Edit-box Escape clears focus and directly hides. It does not call OnCancel itself; an
    /// entry-specific OnHide may still perform work.
    /// </summary>
    public static Plan EditBoxEscape(Slots slots, int slot)
    {
        Instance? current = Get(slots, slot);
        if (current is null)
            return new(slots, [], Outcome.NothingVisible);
        Instance instance = current.Value;
        var effects = new List<Effect>
        {
            new(EffectKind.ClearEditBoxFocus, slot, instance.Definition.Type),
        };
        AppendHide(instance, slot, effects);
        return new(Set(slots, slot, null), effects, Outcome.Hidden, slot);
    }

    /// <summary>
    /// Advances one visible instance. Timeout cancels before hide and returns immediately. A
    /// StartDelay crossing enables the accept button and also returns immediately, so Resize and
    /// entry OnUpdate wait for the next tick exactly as in the frozen script.
    /// </summary>
    public static Plan Advance(Slots slots, int slot, double elapsedSeconds)
    {
        if (elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        Instance? current = Get(slots, slot);
        if (current is null)
            return new(slots, [], Outcome.NothingVisible);

        Instance instance = current.Value;
        var effects = new List<Effect>();
        if (instance.TimeLeft > 0)
        {
            double timeLeft = instance.TimeLeft - elapsedSeconds;
            if (timeLeft <= 0)
            {
                instance = instance with { TimeLeft = 0 };
                AppendCancel(instance.Definition, EffectKind.CancelTimeout, slot, effects);
                AppendHide(instance, slot, effects);
                return new(Set(slots, slot, null), effects, Outcome.TimedOut, slot);
            }
            instance = instance with { TimeLeft = timeLeft };
            if (instance.Definition.UsesTimeoutText)
                effects.Add(new(EffectKind.UpdateCountdownText, slot,
                    instance.Definition.Type, CountdownTextUnit(timeLeft)));
        }

        if (instance.StartDelay is { } delay)
        {
            double timeLeft = delay - elapsedSeconds;
            if (timeLeft <= 0)
            {
                instance = instance with { StartDelay = null };
                effects.Add(new(EffectKind.RevealDelayedText, slot, instance.Definition.Type));
                effects.Add(new(EffectKind.EnableAccept, slot, instance.Definition.Type));
                return new(Set(slots, slot, instance), effects, Outcome.Advanced, slot);
            }
            instance = instance with { StartDelay = timeLeft };
            if (instance.Definition.UsesDelayText)
                effects.Add(new(EffectKind.UpdateDelayText, slot,
                    instance.Definition.Type, CountdownTextUnit(timeLeft)));
        }

        effects.Add(new(EffectKind.Resize, slot, instance.Definition.Type));
        if (instance.Definition.HasOnUpdate)
            effects.Add(new(EffectKind.OnUpdate, slot, instance.Definition.Type));
        return new(Set(slots, slot, instance), effects, Outcome.Advanced, slot);
    }

    /// <summary>Frozen no-money popup height for either the plain or edit-box branch.</summary>
    public static float Height(float textHeight, float buttonHeight, float editBoxHeight = 0,
        bool hasEditBox = false) =>
        16f + Math.Max(0, textHeight) + 8f +
        (hasEditBox ? Math.Max(0, editBoxHeight) + 8f : 0f) +
        Math.Max(0, buttonHeight) + 16f;

    /// <summary>
    /// Countdown display uses ceiling and switches to ceiling(minutes) at 60 seconds.
    /// The value is returned as a stable diagnostic token rather than localized prose.
    /// </summary>
    public static string CountdownTextUnit(double seconds)
    {
        int wholeSeconds = (int)Math.Ceiling(Math.Max(0, seconds));
        if (wholeSeconds < 60)
            return $"{wholeSeconds}|{(wholeSeconds == 1 ? "second" : "seconds")}";
        int minutes = (int)Math.Ceiling(wholeSeconds / 60d);
        return $"{minutes}|{(minutes == 1 ? "minute" : "minutes")}";
    }

    private static void AppendShow(Instance instance, int slot, List<Effect> effects)
    {
        string type = instance.Definition.Type;
        effects.Add(new(EffectKind.Show, slot, type));
        effects.Add(new(EffectKind.MainMenuOpenSound, slot, type, "igMainMenuOpen"));
        if (instance.Definition.HasOnShow)
            effects.Add(new(EffectKind.OnShow, slot, type));
    }

    private static void AppendHide(Instance instance, int slot, List<Effect> effects)
    {
        string type = instance.Definition.Type;
        effects.Add(new(EffectKind.Hide, slot, type));
        effects.Add(new(EffectKind.MainMenuCloseSound, slot, type, "igMainMenuClose"));
        if (instance.Definition.HasOnHide)
            effects.Add(new(EffectKind.OnHide, slot, type));
    }

    private static void AppendCancel(
        Definition definition,
        EffectKind kind,
        int slot,
        List<Effect> effects)
    {
        if (definition.HasCancel)
            effects.Add(new(kind, slot, definition.Type));
    }

    private static int? Find(Slots slots, string type)
    {
        if (slots.First is { } first &&
            string.Equals(first.Definition.Type, type, StringComparison.Ordinal))
            return 1;
        if (slots.Second is { } second &&
            string.Equals(second.Definition.Type, type, StringComparison.Ordinal))
            return 2;
        return null;
    }

    private static int? FirstFree(Slots slots) =>
        slots.First is null ? 1 : slots.Second is null ? 2 : null;

    private static Instance? Get(Slots slots, int slot) => slot switch
    {
        1 => slots.First,
        2 => slots.Second,
        _ => null,
    };

    private static Slots Set(Slots slots, int slot, Instance? value) => slot switch
    {
        1 => slots with { First = value },
        2 => slots with { Second = value },
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };
}
