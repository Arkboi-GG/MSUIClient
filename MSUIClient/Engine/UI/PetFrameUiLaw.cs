using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Build-5875 PetFrame geometry, transcribed from <c>Interface\FrameXML\PetFrame.xml</c>
/// (extracted with tools/mpqpeek). Every number below is the shipped file's, not a guess
/// from a screenshot - the frame this replaced carried invented offsets that put the two
/// status bars eight pixels left and eight pixels high of the recess the frame art draws
/// for them, and the name a quarter of the frame too far right.
///
/// Vanilla anchors PetFrame to PlayerFrame's TOPLEFT at (80, -60), and PlayerFrame itself
/// to UIParent's TOPLEFT at (-19, -4) - so in the absolute screen space MSUI lays its unit
/// frames out in, the pet frame's authored origin is (61, 64). Y offsets are negated here
/// because FrameXML measures down-negative from a TOPLEFT anchor and MSUI measures
/// down-positive.
/// </summary>
public static class PetFrameUiLaw
{
    /// <summary>PlayerFrame(-19,-4) + PetFrame(80,-60), in MSUI's down-positive screen space.</summary>
    public static readonly Vector2 Origin = new(61f, 64f);

    /// <summary>PetFrame's own Size. The ART is taller (see <see cref="TextureSize"/>).</summary>
    public const float Width = 128f;
    public const float Height = 53f;
    public static Vector2 Size => new(Width, Height);

    /// <summary>PetFrameTexture: 128x64 at TOPLEFT (0, -2) - it overhangs the frame below.</summary>
    public const string FrameTexture = @"Interface\TargetingFrame\UI-SmallTargetingFrame";
    public static Vector2 TextureOffset => new(0f, 2f);
    public static Vector2 TextureSize => new(128f, 64f);

    /// <summary>PetPortrait: 37x37 at TOPLEFT (7, -6), drawn UNDER the frame art's ring.</summary>
    public static Vector2 PortraitOffset => new(7f, 6f);
    public const float PortraitSize = 37f;

    /// <summary>PetFrameHealthBar: 70x8 at TOPLEFT (47, -22).</summary>
    public static Vector2 HealthBarOffset => new(47f, 22f);

    /// <summary>PetFrameManaBar: 70x8 at TOPLEFT (47, -29). The two overlap by a pixel in
    /// the shipped file; that is the authored layout, not a rounding slip.</summary>
    public static Vector2 ManaBarOffset => new(47f, 29f);
    public static Vector2 BarSize => new(70f, 8f);

    /// <summary>PetName: GameFontNormalSmall, BOTTOMLEFT (50, 33) - left-aligned at x=50 with
    /// its BASELINE box bottom 33 above the frame's bottom edge, i.e. 20 down from its top.
    /// Left-aligned, so it cannot be placed by a centre the way the player/target names are.</summary>
    public const string NameFont = "GameFontNormalSmall";
    public const float NameLeft = 50f;
    public const float NameBottom = Height - 33f;

    /// <summary>
    /// PetFrame's HitRectInsets (left 7, right 66, top 6, bottom 7): in vanilla only the
    /// PORTRAIT half of the frame is clickable and a click over the status bars falls
    /// through to the world. RECORDED, NOT APPLIED - MSUI hosts the whole frame as the
    /// click/right-click target, and narrowing that is an interaction change rather than
    /// the alignment this law exists to fix.
    /// </summary>
    public static Vector2 HitRectInsetsMin => new(7f, 6f);
    public static Vector2 HitRectInsetsMax => new(66f, 7f);
}
