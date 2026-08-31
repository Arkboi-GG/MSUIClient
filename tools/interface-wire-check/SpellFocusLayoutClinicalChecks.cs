using MSUIClient.Engine.UI;

// The Spell Workshop's focus layout (GameLoop.Creator.SpellFocus.cs) hands the two
// sidebars roughly half the screen between them. Two things must hold at EVERY
// display size and scale dial, or the mode is unusable rather than merely ugly:
// a viewing column always survives between the panes, and the width helper never
// throws - Math.Clamp throws when min > max, and the floor DOES exceed the ceiling
// on a degenerate display (boot, minimize), which is exactly when a creator frame
// would take it down.
internal static class SpellFocusLayoutClinicalChecks
{
    private static readonly (float W, float H)[] Displays =
    [
        (1280f, 720f), (1920f, 1080f), (3840f, 2160f),
    ];

    private static readonly float[] UiScales = [0.6f, 1f, 2.5f];

    private static readonly float[] Fractions =
    [
        SpellFocusLayoutLaw.MinFraction,
        SpellFocusLayoutLaw.DefaultFraction,
        SpellFocusLayoutLaw.MaxFraction,
    ];

    public static void Run()
    {
        Check(SpellFocusLayoutLaw.MinFraction < SpellFocusLayoutLaw.MaxFraction,
            "focus-layout fraction bounds inverted");
        Check(SpellFocusLayoutLaw.DefaultFraction >= SpellFocusLayoutLaw.MinFraction &&
              SpellFocusLayoutLaw.DefaultFraction <= SpellFocusLayoutLaw.MaxFraction,
            "focus-layout default fraction sits outside its own bounds");

        foreach ((float w, float h) in Displays)
        {
            float s = MathF.Max(h / 768f, 0.5f);
            foreach (float uiScale in UiScales)
                foreach (float fraction in Fractions)
                {
                    float pane = SpellFocusLayoutLaw.SidebarWidth(w, h, uiScale, fraction);
                    string at = $"{w}x{h} scale {uiScale} fraction {fraction}";

                    Check(float.IsFinite(pane) && pane > 0f,
                        $"focus-layout sidebar width is not a usable number at {at}");
                    // THE invariant: the viewing column is what the mode exists for.
                    Check(pane < w,
                        $"focus-layout sidebar swallowed the viewing column at {at}");
                    Check(pane <= MathF.Max(w - 360f * s, 1f) + 0.001f,
                        $"focus-layout sidebar exceeded its viewing-column ceiling at {at}");

                    // Every fraction the drag can produce must actually MOVE the
                    // sidebar, or the grab strip rewrites settings.json for nothing.
                    (float lo, float hi) = SpellFocusLayoutLaw.EffectiveFractionBand(w, h, uiScale);
                    Check(lo <= hi, $"focus-layout effective drag band inverted at {at}");
                    Check(lo >= SpellFocusLayoutLaw.MinFraction - 0.001f &&
                          hi <= SpellFocusLayoutLaw.MaxFraction + 0.001f,
                        $"focus-layout effective band escaped its nominal bounds at {at}");
                    float atLo = SpellFocusLayoutLaw.SidebarWidth(w, h, uiScale, lo);
                    float atHi = SpellFocusLayoutLaw.SidebarWidth(w, h, uiScale, hi);
                    Check(hi - lo < 0.0001f || atHi > atLo + 0.5f,
                        $"focus-layout drag band is inert - both ends give the same width at {at}");
                }
        }

        // Degenerate displays: the floor exceeds the ceiling here, so an unguarded
        // Math.Clamp would throw rather than return something small.
        foreach ((float w, float h) in new[] { (1f, 1f), (0f, 0f), (120f, 80f) })
            foreach (float uiScale in UiScales)
            {
                float pane = SpellFocusLayoutLaw.SidebarWidth(w, h, uiScale,
                    SpellFocusLayoutLaw.DefaultFraction);
                Check(float.IsFinite(pane) && pane > 0f,
                    $"focus-layout sidebar width degenerated at {w}x{h} scale {uiScale}");
            }

        // Drag mapping: every value the grab can produce is inside the band that
        // actually moves the sidebar, at every scale - including mouse positions
        // dragged clean off either edge of the screen.
        foreach ((float w, float h) in Displays)
            foreach (float uiScale in UiScales)
            {
                (float lo, float hi) = SpellFocusLayoutLaw.EffectiveFractionBand(w, h, uiScale);
                for (float x = -200f; x <= w + 200f; x += w / 16f)
                {
                    float f = SpellFocusLayoutLaw.FractionFromDragX(w, h, uiScale, x);
                    Check(f >= lo - 0.0001f && f <= hi + 0.0001f,
                        $"focus-layout drag escaped its effective band at x={x} on {w}x{h} @{uiScale}");
                }
                // Dragging left widens, dragging right narrows - never inverted.
                float wide = SpellFocusLayoutLaw.FractionFromDragX(w, h, uiScale, w * 0.2f);
                float narrow = SpellFocusLayoutLaw.FractionFromDragX(w, h, uiScale, w * 0.95f);
                Check(wide >= narrow - 0.0001f,
                    $"focus-layout drag direction inverted on {w}x{h} @{uiScale}");
            }

        Check(MathF.Abs(SpellFocusLayoutLaw.FractionFromDragX(0f, 0f, 1f, 40f) -
                        SpellFocusLayoutLaw.DefaultFraction) < 0.0001f,
            "focus-layout drag on a zero-width display did not fall back to the default");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
