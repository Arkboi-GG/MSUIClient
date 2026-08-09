namespace MSUIClient.Engine.UI;

public enum UiParentManagedConsumer
{
    MultiBarBottomLeft,
    GroupLoot,
    Tutorial,
    FramerateLabel,
    CastingBar,
    ChatLeft,
    ChatRight,
    ShapeshiftBar,
    ContainerOffsetX,
    ContainerOffsetY,
    BattlefieldTabOffsetY,
    PetActionBarOffsetY,
}

public enum UiParentManagedValueKind
{
    FrameAnchor,
    XVariable,
    YVariable,
}

public enum UiParentAnchorPoint
{
    Bottom,
    BottomLeft,
    BottomRight,
    TopLeft,
}

public readonly record struct UiParentManagedState(
    bool BottomLeftShown,
    bool BottomRightShown,
    bool RightLeftShown,
    bool RightRightShown,
    bool PetOrStanceShown,
    bool ReputationShown,
    bool MaxLevelShown);

public readonly record struct UiParentManagedPlacement(
    UiParentManagedValueKind Kind,
    string AnchorTo,
    UiParentAnchorPoint Point,
    UiParentAnchorPoint RelativePoint,
    float X,
    float Y);

/// <summary>
/// A pure UIParent managed-position provider for new consumers. Existing MSUI bag, chat, spell,
/// and HUD offsets are intentionally not migrated through this law without a reproduced fault.
/// </summary>
public static class UiParentUiLaw
{
    private readonly record struct Definition(
        UiParentManagedValueKind Kind,
        float BaseX = 0,
        float BaseY = 0,
        float BottomEither = 0,
        float BottomLeft = 0,
        float BottomRight = 0,
        float RightLeft = 0,
        float RightRight = 0,
        float Pet = 0,
        float Reputation = 0,
        float MaxLevel = 0,
        string AnchorTo = "UIParent",
        UiParentAnchorPoint Point = UiParentAnchorPoint.Bottom,
        UiParentAnchorPoint RelativePoint = UiParentAnchorPoint.Bottom);

    public static UiParentManagedPlacement Resolve(
        UiParentManagedConsumer consumer,
        in UiParentManagedState state)
    {
        Definition d = consumer switch
        {
            UiParentManagedConsumer.MultiBarBottomLeft => new(
                UiParentManagedValueKind.FrameAnchor, BaseY: 17, Reputation: 9,
                MaxLevel: -5, AnchorTo: "ActionButton1",
                Point: UiParentAnchorPoint.BottomLeft,
                RelativePoint: UiParentAnchorPoint.TopLeft),
            UiParentManagedConsumer.GroupLoot => new(
                UiParentManagedValueKind.FrameAnchor, BaseY: 60, BottomEither: 42,
                Pet: 42, Reputation: 9),
            UiParentManagedConsumer.Tutorial => new(
                UiParentManagedValueKind.FrameAnchor, BaseY: 55, BottomEither: 47,
                Pet: 42, Reputation: 9),
            UiParentManagedConsumer.FramerateLabel => new(
                UiParentManagedValueKind.FrameAnchor, BaseY: 64, BottomEither: 42,
                Pet: 42, Reputation: 9),
            UiParentManagedConsumer.CastingBar => new(
                UiParentManagedValueKind.FrameAnchor, BaseY: 60, BottomEither: 40,
                Pet: 40, Reputation: 9),
            UiParentManagedConsumer.ChatLeft => new(
                UiParentManagedValueKind.FrameAnchor, BaseX: 32, BaseY: 85,
                BottomLeft: 17, Pet: 17, Reputation: 9, MaxLevel: -5,
                Point: UiParentAnchorPoint.BottomLeft,
                RelativePoint: UiParentAnchorPoint.BottomLeft),
            UiParentManagedConsumer.ChatRight => new(
                UiParentManagedValueKind.FrameAnchor, BaseX: -32, BaseY: 85,
                BottomRight: 17, RightLeft: -88, RightRight: -43,
                Reputation: 9, MaxLevel: -5,
                Point: UiParentAnchorPoint.BottomRight,
                RelativePoint: UiParentAnchorPoint.BottomRight),
            UiParentManagedConsumer.ShapeshiftBar => new(
                UiParentManagedValueKind.FrameAnchor, BaseX: 30, BottomLeft: 45,
                Reputation: 9, MaxLevel: -5, AnchorTo: "MainMenuBar",
                Point: UiParentAnchorPoint.BottomLeft,
                RelativePoint: UiParentAnchorPoint.TopLeft),
            UiParentManagedConsumer.ContainerOffsetX => new(
                UiParentManagedValueKind.XVariable, RightLeft: 90, RightRight: 45),
            UiParentManagedConsumer.ContainerOffsetY => new(
                UiParentManagedValueKind.YVariable, BaseY: 70, BottomEither: 27,
                Pet: 23, Reputation: 9),
            UiParentManagedConsumer.BattlefieldTabOffsetY => new(
                UiParentManagedValueKind.YVariable, BaseY: 210, BottomRight: 40,
                Reputation: 9),
            UiParentManagedConsumer.PetActionBarOffsetY => new(
                UiParentManagedValueKind.YVariable, BaseY: 97, BottomLeft: 43,
                Reputation: 9, MaxLevel: -5),
            _ => throw new ArgumentOutOfRangeException(nameof(consumer), consumer, null),
        };

        bool bottomEither = state.BottomLeftShown || state.BottomRightShown;
        float x = d.BaseX;
        float y = d.BaseY;
        if (bottomEither) y += d.BottomEither;
        if (state.BottomLeftShown) y += d.BottomLeft;
        if (state.BottomRightShown) y += d.BottomRight;
        if (state.PetOrStanceShown) y += d.Pet;
        if (state.ReputationShown) y += d.Reputation;
        if (state.MaxLevelShown) y += d.MaxLevel;
        if (state.BottomLeftShown && state.PetOrStanceShown &&
            d.BottomLeft != 0 && d.Pet != 0)
            y += 23;

        if (state.RightLeftShown) x += d.RightLeft;
        else if (state.RightRightShown) x += d.RightRight;

        return new(d.Kind, d.AnchorTo, d.Point, d.RelativePoint, x, y);
    }

    /// <summary>
    /// Pure 1.12 binding-label formatter. A caller supplies localization lookup; this does not
    /// alter MSUI's current Key storage, capture, or FriendlyKey renderer.
    /// </summary>
    public static string BindingText(
        string? name,
        string? prefix = null,
        bool abbreviate = false,
        Func<string, string?>? localized = null,
        string locale = "enUS",
        bool macClient = false)
    {
        if (name is null) return "";
        int dashCount = name.Count(c => c == '-');
        int lastDash = name.LastIndexOf('-');
        string modifiers = lastDash < 0 ? "" : name[..(lastDash + 1)];
        string baseName = lastDash < 0 ? name : name[(lastDash + 1)..];

        if (locale.Equals("deDE", StringComparison.OrdinalIgnoreCase))
            modifiers = modifiers.Replace("CTRL", "STRG", StringComparison.Ordinal);

        if (abbreviate)
        {
            if (dashCount > 1) return "·";
            modifiers = modifiers
                .Replace("CTRL", "c", StringComparison.Ordinal)
                .Replace("SHIFT", "s", StringComparison.Ordinal)
                .Replace("ALT", "a", StringComparison.Ordinal)
                .Replace("STRG", "st", StringComparison.Ordinal);
        }

        prefix ??= "";
        string? localizedName = macClient ? localized?.Invoke(prefix + baseName + "_MAC") : null;
        localizedName ??= localized?.Invoke(prefix + baseName);
        localizedName ??= baseName;
        return modifiers + localizedName;
    }
}
