using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>Current desktop character-select authored geometry and modal behavior.</summary>
public static class CharSelectUiLaw
{
    public readonly record struct ScreenRect(Vector2 Min, Vector2 Size)
    {
        public Vector2 Max => Min + Size;
    }

    public readonly record struct DeleteDialogLayout(
        ScreenRect Frame,
        ScreenRect Alert,
        Vector2 LeadCenter,
        Vector2 IdentityCenter,
        Vector2 InstructionsCenter,
        ScreenRect Edit,
        ScreenRect EditBorderLeft,
        ScreenRect EditBorderRight,
        ScreenRect Okay,
        ScreenRect Cancel);

    public const float DeleteWidth = 512f;
    public const float DeleteHeight = 256f;
    public const float AlertLeft = 12f;
    public const float AlertTop = 86f;
    public const float AlertSize = 64f;
    public const float LeadTop = 16f;
    public const float IdentityTop = 40f;
    public const float InstructionsTop = 83f;
    public const float EditTop = 102f;
    public const float EditWidth = 130f;
    public const float EditHeight = 32f;
    public const float EditBorderWidth = 75f;
    public const float EditBorderOverhang = 10f;
    public const float ButtonWidth = 200f;
    public const float ButtonHeight = 40f;
    public const float ButtonBottom = 16f;
    public const float ButtonGap = 13f;
    public const float OkayRightInset = 6f;
    public const float CancelLeftInset = 7f;
    public const string ConfirmText = "DELETE";
    public const string DeleteSound = "gsCharacterSelectionDelCharacter";
    public const string CreateSound = "gsCharacterSelectionCreateNew";
    public const string BackSound = "gsCharacterSelectionExit";
    public const string EnterWorldSound = "gsCharacterSelectionEnterWorld";
    public const string AcceptSound = "gsTitleOptionOK";
    public const string CancelSound = "gsTitleOptionExit";
    public const string SoundCategory = "ui.glue.char-select";

    public static readonly Vector2 EditLeftUvMin = Vector2.Zero;
    public static readonly Vector2 EditLeftUvMax = new(.29296875f, 1f);
    public static readonly Vector2 EditRightUvMin = new(.70703125f, 0f);
    public static readonly Vector2 EditRightUvMax = Vector2.One;

    public static ScreenRect Host(Vector2 displaySize) =>
        new(Vector2.Zero, displaySize);

    public static ScreenRect TuningWindow =>
        new(new Vector2(48f, 48f), new Vector2(360f, 0f));

    public static DeleteDialogLayout DeleteDialog(Vector2 display, float scale)
    {
        float s = MathF.Max(scale, 0f);
        Vector2 frameSize = new(DeleteWidth * s, DeleteHeight * s);
        Vector2 origin = (display - frameSize) * .5f;
        float centerX = origin.X + frameSize.X * .5f;
        Vector2 editMin = new(centerX - EditWidth * s * .5f, origin.Y + EditTop * s);
        Vector2 buttonSize = new(ButtonWidth * s, ButtonHeight * s);
        float buttonTop = origin.Y + (DeleteHeight - ButtonBottom - ButtonHeight) * s;
        float okayLeft = centerX - (ButtonWidth + OkayRightInset) * s;
        float cancelLeft = centerX + CancelLeftInset * s;

        return new(
            new(origin, frameSize),
            new(origin + new Vector2(AlertLeft, AlertTop) * s,
                new Vector2(AlertSize, AlertSize) * s),
            new(centerX, origin.Y + LeadTop * s),
            new(centerX, origin.Y + IdentityTop * s),
            new(centerX, origin.Y + InstructionsTop * s),
            new(editMin, new Vector2(EditWidth, EditHeight) * s),
            new(editMin + new Vector2(-EditBorderOverhang, 0f) * s,
                new Vector2(EditBorderWidth, EditHeight) * s),
            new(editMin + new Vector2(EditWidth - EditBorderWidth + EditBorderOverhang, 0f) * s,
                new Vector2(EditBorderWidth, EditHeight) * s),
            new(new Vector2(okayLeft, buttonTop), buttonSize),
            new(new Vector2(cancelLeft, buttonTop), buttonSize));
    }
}
