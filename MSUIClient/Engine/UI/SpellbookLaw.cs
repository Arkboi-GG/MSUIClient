using MSUIClient.Formats;
using System.Numerics;

namespace MSUIClient.Engine.UI;

public readonly record struct SpellbookLogicalRect(float X, float Y, float Width, float Height)
{
    public Vector2 Min => new(X, Y);
    public Vector2 Size => new(Width, Height);
    public Vector2 ScaledMin(Vector2 origin, float scale) => origin + Min * scale;
    public Vector2 ScaledSize(float scale) => Size * scale;
}

public readonly record struct SpellbookArtSeat(string Path, SpellbookLogicalRect Rect);

/// <summary>Pure 1.12 spellbook classification/sorting rules used by the live book and validators.</summary>
public static class SpellbookLaw
{
    public const string GeneralName = "General";
    public const string GeneralIcon = @"Interface\Icons\Ability_Kick";
    // SpellBookFrame is 384x512. The transparent ImGui host extends 32 units to the right so the
    // eighth 32x32 skill-line seat remains inside its input surface; neither value is resizable.
    public static readonly Vector2 FrameSize = new(384, 512);
    public static readonly Vector2 HostSize = new(416, 512);
    public static readonly SpellbookLogicalRect FrameRect = new(0, 0, 384, 512);
    public static readonly SpellbookLogicalRect CloseButton = new(324, 9, 32, 32);
    public static readonly SpellbookLogicalRect PreviousPageButton = new(34, 391, 32, 32);
    public static readonly SpellbookLogicalRect NextPageButton = new(298, 391, 32, 32);
    public static readonly Vector2 TitleCenter = new(198, 26);
    public static readonly Vector2 PageTextCenter = new(178, 416);
    public static readonly SpellbookArtSeat[] PanelArt =
    [
        new(@"Interface\Spellbook\Spellbook-Icon", new(10, 8, 58, 58)),
        new(@"Interface\Spellbook\UI-SpellbookPanel-TopLeft", new(0, 0, 256, 256)),
        new(@"Interface\Spellbook\UI-SpellbookPanel-TopRight", new(256, 0, 128, 256)),
        new(@"Interface\Spellbook\UI-SpellbookPanel-BotLeft", new(0, 256, 256, 256)),
        new(@"Interface\Spellbook\UI-SpellbookPanel-BotRight", new(256, 256, 128, 256)),
    ];

    // SpellBookFrame.xml declares eight right-rail skill-line tab seats.
    public const int MaxClassTabs = 8;
    public const int SpellsPerPage = 12;
    public const int SpellRows = 6;
    public const float SpellColumnStep = 157f;
    public const float SpellRowStep = 51f;
    public const float SkillLineTabStep = 49f;
    public const float SkillLineTabSize = 32f;
    public static readonly SpellbookLogicalRect SkillLineTabBackdrop = new(-3, -11, 64, 64);
    public static readonly Vector2 DragPreviewOffset = new(10);
    public static readonly Vector2 DragPreviewSize = new(32);
    public static readonly Vector2 FontCalibrationOffset = new(420, 25);
    public static readonly Vector2 FontCalibrationSize = new(430, 0);

    // Frozen SpellBookFrame sound kits: open/close on show/hide, page-turn on both page clicks.
    // "igAbiliityPageTurn" is the real registered kit name (SoundEntries id 836), not a typo.
    public const string OpenSound = "igSpellBookOpen";
    public const string CloseSound = "igSpellBookClose";
    public const string PageTurnSound = "igAbiliityPageTurn";

    // Build-5875 Fonts.xml + SpellButtonTemplate, read directly from patch.MPQ. Keep these as
    // SOURCE semantics. The renderer-specific font-height conversion is deliberately not here.
    public const float NameFontHeight = 12f;
    public const float RankFontHeight = 10f;
    public const float ButtonSize = 37f;
    public const float NameWidth = 103f;
    public const int NameMaxLines = 3;
    public const float NameAnchorX = 4f;
    public const float NameAnchorYWithRank = 4f;
    public const float NameAnchorYWithoutRank = 2f;
    public const float RankWidth = 79f;
    public const float RankBoxHeight = 18f;
    public const float RankAnchorY = 4f;
    public static readonly SpellbookLogicalRect SpellButtonBackground = new(-3, -3, 64, 64);
    public static readonly SpellbookLogicalRect SpellButtonNormalRing =
        new((ButtonSize - 64f) * .5f, (ButtonSize - 64f) * .5f, 64, 64);
    // Current Benilla decision 1393: the 33/64 live portion of this 71.53-unit texture lands on
    // the 37-unit spell-button corners, instead of reusing the pet bar's 58-unit plate.
    public const float AutocastOverlaySize = 71.53f;
    public static readonly SpellbookLogicalRect SpellButtonAutocastOverlay =
        new((ButtonSize - AutocastOverlaySize) * .5f,
            (ButtonSize - AutocastOverlaySize) * .5f,
            AutocastOverlaySize, AutocastOverlaySize);

    // UI-AutoCastButton.m2 projected by the current Benilla spellbook's concentric 37x37 model
    // site at SetModelScale(1.48). Stars use the screen diagonal, not gameplay UI scale, and the
    // model viewport clips every quad to the button rectangle.
    public const float AutocastModelScale = 1.48f;
    public const float AutocastModelUnitsToUi = 1280f;
    public const float AutocastBoneSquareModelUnits = .02f;
    public const float AutocastPathSide =
        AutocastBoneSquareModelUnits * AutocastModelUnitsToUi * AutocastModelScale;
    public const int AutocastEmitterCount = 4;
    public const int AutocastParticlesPerEmitter = 300;
    public const float AutocastPeriodSeconds = 2f;
    public const float AutocastParticleLifeSeconds = 1f;
    public const float AutocastSpinRadians = .1f;
    // CooldownFrameTemplate: 36x36, centered on the 37x37 spell button at CENTER(0,-1).
    // FrameXML y is up while screen y is down, hence the +1.5 screen-space top offset.
    public const float CooldownSize = 36f;
    public static readonly Vector2 CooldownOffset = new(0.5f, 1.5f);
    public const string CheckedTexture = @"Interface\Buttons\CheckButtonHilight";
    public const uint ModShapeshiftAura = 36;
    public const uint PassiveNameColor = 0xff00a3c4; // RGB(.77,.64,0) => (196,163,0).
    public const uint RankColor = 0xff003359; // RGB(0.35, 0.20, 0), ImGui ABGR packing.

    public static bool Eligible(in SpellInfo spell) => spell.InSpellbook;

    public static Vector2 CooldownMin(Vector2 buttonMin, float scale) =>
        buttonMin + CooldownOffset * scale;

    public static Vector2 CooldownMax(Vector2 buttonMin, float scale) =>
        CooldownMin(buttonMin, scale) + new Vector2(CooldownSize * scale);

    public static Vector2 DragPreviewMin(Vector2 mousePosition, float scale) =>
        mousePosition + DragPreviewOffset * scale;

    public static Vector2 DragPreviewMax(Vector2 mousePosition, float scale) =>
        DragPreviewMin(mousePosition, scale) + DragPreviewSize * scale;

    public static Vector2 ButtonScaledSize(float scale) => new(ButtonSize * scale);

    public static Vector2 SkillLineTabScaledSize(float scale) => new(SkillLineTabSize * scale);

    public static Vector2 SpellNamePosition(Vector2 buttonMin, float scale, float anchorY,
        float nameBlockHeight) => new(
            buttonMin.X + (ButtonSize + NameAnchorX) * scale,
            buttonMin.Y + ButtonSize * .5f * scale - anchorY * scale - nameBlockHeight * .5f);

    public static Vector2 SpellNameLinePosition(Vector2 namePosition, int line, float pitch) =>
        namePosition + Vector2.UnitY * line * pitch;

    public static Vector2 SpellRankPosition(Vector2 namePosition, float nameBlockHeight,
        float scale, float rankEm) => namePosition + Vector2.UnitY *
            (nameBlockHeight - RankAnchorY * scale + (RankBoxHeight * scale - rankEm) * .5f);

    public static Vector2 FontCalibrationPosition(Vector2 spellbookOrigin, float scale) =>
        spellbookOrigin + FontCalibrationOffset * scale;

    public static SpellbookLogicalRect SpellButtonSeat(int index)
    {
        index = Math.Clamp(index, 0, SpellsPerPage - 1);
        int column = index / SpellRows;
        int row = index % SpellRows;
        return new(34 + column * SpellColumnStep, 85 + row * SpellRowStep,
            ButtonSize, ButtonSize);
    }

    public static SpellbookLogicalRect SkillLineTabSeat(int index)
    {
        index = Math.Clamp(index, 0, MaxClassTabs - 1);
        return new(352, 65 + index * SkillLineTabStep, SkillLineTabSize, SkillLineTabSize);
    }

    public static float AutocastParticleAge(int particle) =>
        Math.Clamp(particle, 0, AutocastParticlesPerEmitter - 1) /
        (float)AutocastParticlesPerEmitter;

    public static float AutocastLap(double nowSeconds)
    {
        double wrapped = nowSeconds % AutocastPeriodSeconds;
        if (wrapped < 0) wrapped += AutocastPeriodSeconds;
        return (float)(wrapped / AutocastPeriodSeconds);
    }

    /// <summary>Emitter position relative to the spell button's screen-space top-left.</summary>
    public static Vector2 AutocastPoint(float lap, int emitter, float ageSeconds)
    {
        emitter = Math.Clamp(emitter, 0, AutocastEmitterCount - 1);
        float phase = lap - emitter / (float)AutocastEmitterCount -
            ageSeconds / AutocastPeriodSeconds;
        phase -= MathF.Floor(phase);
        float edge = phase * 4f;
        float leg = edge - MathF.Floor(edge);
        (float x, float yUp) = (int)MathF.Floor(edge) switch
        {
            0 => (0f, leg * AutocastPathSide),
            1 => (leg * AutocastPathSide, AutocastPathSide),
            2 => (AutocastPathSide, AutocastPathSide * (1f - leg)),
            _ => (AutocastPathSide * (1f - leg), 0f),
        };
        return new(x, ButtonSize - yUp);
    }

    public static float AutocastStarHalfExtent(float ageSeconds, float screenDiagonal) =>
        Ramp(.005f, .0015f, .001f,
            Math.Clamp(ageSeconds / AutocastParticleLifeSeconds, 0f, 1f)) * screenDiagonal;

    public static Vector4 AutocastStarColor(float ageSeconds)
    {
        float age = Math.Clamp(ageSeconds / AutocastParticleLifeSeconds, 0f, 1f);
        return new(
            Ramp(.976f, .996f, 1f, age),
            Ramp(.875f, .945f, 1f, age),
            Ramp(.192f, .745f, 1f, age),
            Ramp(1f, 1f, 0f, age));
    }

    public static Vector2 AutocastStarCorner(Vector2 center, float x, float y, float angle)
    {
        (float sin, float cos) = MathF.SinCos(angle);
        return center + new Vector2(x * cos - y * sin, x * sin + y * cos);
    }

    private static float Ramp(float first, float middle, float last, float age)
    {
        (float a, float b, float segment) = age <= .5f
            ? (first, middle, age / .5f)
            : (middle, last, (age - .5f) / .5f);
        // The M2 ramp evaluator samples 0.5% inside each segment, never at the authored endpoint.
        float t = segment * .99f + .005f;
        return a + (b - a) * t;
    }

    /// <summary>The form id authored by the first SPELL_AURA_MOD_SHAPESHIFT effect lane.</summary>
    public static uint? ShapeshiftForm(in SpellInfo spell)
    {
        if (spell.AuraIds is null || spell.EffectMiscValues is null) return null;
        int count = Math.Min(spell.AuraIds.Length, spell.EffectMiscValues.Length);
        for (int i = 0; i < count; i++)
            if (spell.AuraIds[i] == ModShapeshiftAura && spell.EffectMiscValues[i] > 0)
                return (uint)spell.EffectMiscValues[i];
        return null;
    }

    /// <summary>
    /// Frozen IsCurrentCast semantics: a matching active form or this spell's open profession
    /// window. An ordinary in-flight cast is deliberately not an input to this predicate.
    /// </summary>
    public static bool Checked(in SpellInfo spell, byte activeForm, bool professionWindowOpen) =>
        professionWindowOpen ||
        (activeForm != 0 && ShapeshiftForm(spell) == activeForm);

    /// <summary>MacroFrame_AddMacroLine's exact spellbook payload (the caller appends it raw).</summary>
    public static string? MacroCastLine(in SpellInfo spell)
    {
        if (spell.Passive) return null;
        return string.IsNullOrWhiteSpace(spell.Rank)
            ? $"/cast {spell.Name}"
            : $"/cast {spell.Name}({spell.Rank})";
    }

    public static int LeadingRankNumber(string? rank)
    {
        if (string.IsNullOrEmpty(rank)) return 0;
        int i = 0;
        while (i < rank.Length && !char.IsDigit(rank[i])) i++;
        int value = 0;
        while (i < rank.Length && char.IsDigit(rank[i]))
        {
            value = checked(value * 10 + rank[i] - '0');
            i++;
        }
        return value;
    }
}
