namespace MSUIClient.Engine.UI;

/// <summary>
/// Pure ownership law for the frozen build-5875 UI panel seats. This class decides only which
/// panel owns the left, center, or full-screen seat and the order of the resulting host actions.
/// It deliberately knows nothing about a panel's rendering, sounds, wires, or close callback;
/// adapters must continue to use each MSUI surface's existing behavior for those effects.
/// </summary>
public static class UiPanelOwnershipLaw
{
    public enum Area
    {
        /// <summary>An unregistered top-level frame: show directly and never claim a panel seat.</summary>
        Unregistered,
        Left,
        Center,
        Fullscreen,
    }

    public readonly record struct Panel(
        string Id,
        Area Area,
        int Pushable = 0,
        bool WhileDead = false);

    public readonly record struct Seats(Panel? Left, Panel? Center, Panel? Fullscreen)
    {
        public static Seats Empty => new(null, null, null);
    }

    public enum EffectKind
    {
        Hide,
        Show,
        AnchorLeft,
        AnchorCenter,
        Raise,

        /// <summary>Close backpack, equipped bags, and keyring: CloseAllWindows' ESC set.</summary>
        CloseEscapeContainers,

        /// <summary>Close backpack and equipped bags, but preserve the keyring.</summary>
        CloseAllBags,
    }

    public readonly record struct Effect(EffectKind Kind, string? PanelId = null);

    public enum Outcome
    {
        Opened,
        Hidden,
        Closed,
        AlreadyVisible,
        AlreadyHidden,
        RefusedByNativeCenter,
        RefusedByFullscreen,
        RefusedWhileDead,
    }

    public readonly record struct Transition(
        Seats Seats,
        IReadOnlyList<Effect> Effects,
        Outcome Outcome,
        bool FoundAny = false);

    /// <summary>
    /// CanOpenPanels: a registered native-center owner (the game menu in the frozen closure)
    /// blocks registered non-center panels. Left-area panels occupying the center seat do not.
    /// </summary>
    public static bool CanOpenPanels(Seats seats) =>
        seats.Center is not { Area: Area.Center };

    /// <summary>
    /// Plans frozen ShowUIPanel behavior. <paramref name="alreadyVisible"/> is kept explicit because
    /// visible frames return before any ownership mutation. The frozen executable performs its
    /// native-center CanOpenPanels gate before the later force branch, so a non-center request is
    /// refused there even when <paramref name="force"/> is true; this wider-mask-style quirk is
    /// preserved rather than normalized to the surrounding source comment.
    /// </summary>
    public static Transition Show(
        Seats seats,
        Panel request,
        bool alreadyVisible = false,
        bool force = false,
        bool playerDeadOrGhost = false)
    {
        var effects = new List<Effect>();
        if (alreadyVisible)
            return new(seats, effects, Outcome.AlreadyVisible);

        // Unregistered top-level frames bypass CanOpenPanels, death, and seat ownership.
        if (request.Area == Area.Unregistered)
        {
            effects.Add(new(EffectKind.Show, request.Id));
            return new(seats, effects, Outcome.Opened);
        }

        if (!CanOpenPanels(seats) && request.Area != Area.Center)
            return new(seats, effects, Outcome.RefusedByNativeCenter);

        if (playerDeadOrGhost && !request.WhileDead)
            return new(seats, effects, Outcome.RefusedWhileDead);

        if (seats.Fullscreen is not null && request.Area != Area.Fullscreen)
        {
            if (!force)
                return new(seats, effects, Outcome.RefusedByFullscreen);
            SetFullscreen(ref seats, null, effects);
        }

        // This is source-faithful defensive structure. For a normal registered native-center
        // owner, the earlier CanOpenPanels gate already refused a non-center request.
        if (seats.Center is { Area: Area.Center } && request.Area != Area.Center)
        {
            if (!force)
                return new(seats, effects, Outcome.RefusedByNativeCenter);
            SetCenter(ref seats, null, skipAnchor: false, effects);
        }

        if (request.Area == Area.Fullscreen)
        {
            Transition closed = CloseAllWindows(seats, ignoreNativeCenter: false);
            seats = closed.Seats;
            effects.AddRange(closed.Effects);
            SetFullscreen(ref seats, request, effects);
            return new(seats, effects, Outcome.Opened);
        }

        if (request.Area == Area.Center)
        {
            Transition closed = CloseWindows(seats, ignoreNativeCenter: false);
            seats = closed.Seats;
            effects.AddRange(closed.Effects);
            effects.Add(new(EffectKind.CloseAllBags));
            SetCenter(ref seats, request, skipAnchor: true, effects);
            return new(seats, effects, Outcome.Opened);
        }

        // Left-area placement and push priority.
        if (seats.Left is null)
        {
            SetLeft(ref seats, request, effects);
            return new(seats, effects, Outcome.Opened);
        }

        Panel left = seats.Left.Value;
        if (seats.Center is null)
        {
            if (left.Pushable == 0 && request.Pushable == 0)
            {
                SetLeft(ref seats, request, effects);
            }
            else if (left.Pushable > request.Pushable)
            {
                MovePanelToCenter(ref seats, effects);
                SetLeft(ref seats, request, effects);
            }
            else
            {
                SetCenter(ref seats, request, skipAnchor: false, effects);
            }
            return new(seats, effects, Outcome.Opened);
        }

        Panel center = seats.Center.Value;
        if (request.Pushable > center.Pushable)
        {
            MovePanelToLeft(ref seats, effects);
            SetCenter(ref seats, request, skipAnchor: false, effects);
        }
        else
        {
            SetLeft(ref seats, request, effects);
        }
        return new(seats, effects, Outcome.Opened);
    }

    public static Transition Hide(Seats seats, Panel panel, bool alreadyShown = true)
    {
        var effects = new List<Effect>();
        if (!alreadyShown)
            return new(seats, effects, Outcome.AlreadyHidden);

        HideCore(ref seats, panel, effects);
        return new(seats, effects, Outcome.Hidden);
    }

    /// <summary>
    /// Frozen CloseWindows order: capture all three owners, hide left, hide full-screen, then hide
    /// the captured center unless it is a native-center owner protected by ignoreCenter. Capturing
    /// first matters because hiding left may promote a left-area center owner into the left seat.
    /// </summary>
    public static Transition CloseWindows(Seats seats, bool ignoreNativeCenter)
    {
        var effects = new List<Effect>();
        Panel? originalLeft = seats.Left;
        Panel? originalCenter = seats.Center;
        Panel? originalFullscreen = seats.Fullscreen;
        bool found = originalLeft is not null || originalCenter is not null ||
            originalFullscreen is not null;

        if (originalLeft is { } left)
            HideCore(ref seats, left, effects);
        if (originalFullscreen is { } fullscreen)
            HideCore(ref seats, fullscreen, effects);
        if (originalCenter is { } center &&
            (center.Area != Area.Center || !ignoreNativeCenter))
            HideCore(ref seats, center, effects);

        return new(seats, effects, Outcome.Closed, found);
    }

    /// <summary>CloseAllWindows closes its ESC container set before the panel seats.</summary>
    public static Transition CloseAllWindows(Seats seats, bool ignoreNativeCenter)
    {
        var effects = new List<Effect> { new(EffectKind.CloseEscapeContainers) };
        Transition closed = CloseWindows(seats, ignoreNativeCenter);
        effects.AddRange(closed.Effects);
        return new(closed.Seats, effects, Outcome.Closed, closed.FoundAny);
    }

    private static void HideCore(ref Seats seats, Panel panel, List<Effect> effects)
    {
        if (Same(seats.Fullscreen, panel))
        {
            SetFullscreen(ref seats, null, effects);
            return;
        }
        if (Same(seats.Center, panel))
        {
            SetCenter(ref seats, null, skipAnchor: false, effects);
            return;
        }
        if (Same(seats.Left, panel))
        {
            if (seats.Center is { Area: Area.Left })
                MovePanelToLeft(ref seats, effects);
            else
                SetLeft(ref seats, null, effects);
            return;
        }

        // HideUIPanel also accepts shown, unregistered/unowned frames.
        effects.Add(new(EffectKind.Hide, panel.Id));
    }

    private static void SetLeft(ref Seats seats, Panel? panel, List<Effect> effects)
    {
        Panel? old = seats.Left;
        seats = seats with { Left = panel };
        if (old is { } prior)
            effects.Add(new(EffectKind.Hide, prior.Id));
        if (panel is { } next)
        {
            effects.Add(new(EffectKind.AnchorLeft, next.Id));
            effects.Add(new(EffectKind.Show, next.Id));
        }
    }

    private static void SetCenter(
        ref Seats seats,
        Panel? panel,
        bool skipAnchor,
        List<Effect> effects)
    {
        Panel? old = seats.Center;
        seats = seats with { Center = panel };
        if (old is { } prior)
            effects.Add(new(EffectKind.Hide, prior.Id));
        if (panel is { } next)
        {
            // SetCenterFrame shows before it optionally changes the anchor.
            effects.Add(new(EffectKind.Show, next.Id));
            if (!skipAnchor)
                effects.Add(new(EffectKind.AnchorCenter, next.Id));
        }
    }

    private static void SetFullscreen(ref Seats seats, Panel? panel, List<Effect> effects)
    {
        Panel? old = seats.Fullscreen;
        seats = seats with { Fullscreen = panel };
        if (old is { } prior)
            effects.Add(new(EffectKind.Hide, prior.Id));
        if (panel is { } next)
            effects.Add(new(EffectKind.Show, next.Id));
    }

    private static void MovePanelToLeft(ref Seats seats, List<Effect> effects)
    {
        if (seats.Center is not { } center)
            return;

        SetLeft(ref seats, null, effects);
        effects.Add(new(EffectKind.AnchorLeft, center.Id));
        seats = seats with { Left = center, Center = null };
    }

    private static void MovePanelToCenter(ref Seats seats, List<Effect> effects)
    {
        if (seats.Left is not { } left)
            return;

        SetCenter(ref seats, null, skipAnchor: false, effects);
        effects.Add(new(EffectKind.AnchorCenter, left.Id));
        effects.Add(new(EffectKind.Raise, left.Id));
        seats = seats with { Left = null, Center = left };
    }

    private static bool Same(Panel? candidate, Panel panel) =>
        candidate is { } value && string.Equals(value.Id, panel.Id, StringComparison.Ordinal);
}
