using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

/// <summary>Current build-5875 ShapeshiftBar admission, order, state and geometry.</summary>
public static class StanceBarUiLaw
{
    public const uint ExcludeAttributeEx2 = 0x2;
    public const uint ForceAttributeEx2 = 0x10;
    public const uint ModShapeshiftAura = 36;
    public const int SlotCount = 10;
    public const float FrameHeight = 32;
    public const float ButtonSize = 30;
    public const float FirstButtonX = 11;
    public const float ButtonBottom = 3;
    public const float ButtonStep = 37;
    public const float RaisedRingSize = 50;
    public const float UnraisedRingSize = 64;
    public const float ManagedX = 30;
    public const float RaisedY = 45;
    public const string RingPath = @"Interface\Buttons\UI-Quickslot2";
    public const string DepressPath = @"Interface\Buttons\UI-Quickslot-Depress";
    public const string HighlightPath = @"Interface\Buttons\ButtonHilight-Square";
    public const string CheckedPath = @"Interface\Buttons\CheckButtonHilight";
    public static readonly Vector2 ShelfLeftSize = new(45, 50);
    public static readonly Vector2 ShelfMiddleSize = new(38, 50);
    public static readonly Vector2 ShelfRightSize = new(42, 50);

    public static bool Admitted(in SpellInfo spell) =>
        (spell.AttributesEx2 & ExcludeAttributeEx2) == 0 &&
        (FormId(spell) != 0 || (spell.AttributesEx2 & ForceAttributeEx2) != 0);

    public static uint FormId(in SpellInfo spell)
    {
        if (spell.AuraIds is null || spell.EffectMiscValues is null) return 0;
        int count = Math.Min(spell.AuraIds.Length, spell.EffectMiscValues.Length);
        for (int i = 0; i < count; i++)
            if (spell.AuraIds[i] == ModShapeshiftAura && spell.EffectMiscValues[i] > 0)
                return (uint)spell.EffectMiscValues[i];
        return 0;
    }

    public static IReadOnlyList<SpellInfo> Forms(IEnumerable<SpellInfo> known) => known
        .Where(spell => Admitted(spell))
        .OrderBy(spell => spell.StanceBarOrder < 0 ? long.MaxValue : spell.StanceBarOrder)
        .ThenBy(spell => spell.Id)
        .Take(SlotCount)
        .ToArray();

    public static bool Active(in SpellInfo spell, byte formByte, bool liveOwnAura)
    {
        uint form = FormId(spell);
        return form != 0 ? form == formByte : spell.ActiveIconId != 0 && liveOwnAura;
    }

    public static string Icon(in SpellInfo spell, bool active) =>
        active && spell.ActiveIconId != 0 && !string.IsNullOrWhiteSpace(spell.ActiveIconPath)
            ? spell.ActiveIconPath : spell.IconPath;

    public static bool CancelActive(uint formId, bool active, bool formCancelable) =>
        active && (formId == 0 || formCancelable);

    public static float ButtonX(int index) =>
        FirstButtonX + Math.Clamp(index, 0, SlotCount - 1) * ButtonStep;
    public static float ButtonTop => FrameHeight - ButtonBottom - ButtonSize;
    public static float FrameWidth(int formCount) => formCount <= 0 ? 0 :
        ButtonX(Math.Clamp(formCount, 1, SlotCount) - 1) + ButtonSize;
    public static float RingSize(bool raised) => raised ? RaisedRingSize : UnraisedRingSize;
    public static bool ShowMiddleShelf(bool raised, int formCount) => !raised && formCount > 2;

    public static Vector2 FrameOrigin(Vector2 mainMenuBarTopLeft, float scale,
        in UiParentManagedState state)
    {
        UiParentManagedPlacement placement = UiParentUiLaw.Resolve(
            UiParentManagedConsumer.ShapeshiftBar, state);
        return new(mainMenuBarTopLeft.X + placement.X * scale,
            mainMenuBarTopLeft.Y - placement.Y * scale - FrameHeight * scale);
    }
}
