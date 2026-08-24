using System.Numerics;
using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;

internal static class TalentFrameClinicalChecks
{
    public static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }

        Check(TalentFrameUiLaw.FrameWidth == 384 && TalentFrameUiLaw.FrameHeight == 512 &&
              TalentFrameUiLaw.Frame ==
                  new TalentFrameUiLaw.LogicalRect(0, 0, 384, 512) &&
              TalentFrameUiLaw.Portrait == new TalentFrameUiLaw.LogicalRect(7, 6, 60, 60) &&
              TalentFrameUiLaw.PointsBorder ==
                  new TalentFrameUiLaw.LogicalRect(75, 48, 264, 20) &&
              TalentFrameUiLaw.ScrollFrame ==
                  new TalentFrameUiLaw.LogicalRect(23, 77, 296, 332) &&
              TalentFrameUiLaw.ScrollMaximum == 172 &&
              TalentFrameUiLaw.ScrollStep == 20 &&
              TalentFrameUiLaw.CloseX ==
                  new TalentFrameUiLaw.LogicalRect(324, 9, 32, 32),
            "TalentFrame authored shell/points/scroll/close geometry drift");

        Check(TalentFrameUiLaw.TalentButton(0, 0) ==
                  new TalentFrameUiLaw.LogicalRect(58, 97, 37, 37) &&
              TalentFrameUiLaw.TalentButton(7, 3) ==
                  new TalentFrameUiLaw.LogicalRect(247, 538, 37, 37) &&
              TalentFrameUiLaw.TalentSlot(0, 0) ==
                  new TalentFrameUiLaw.LogicalRect(44.5f, 83.5f, 64, 64) &&
              TalentFrameUiLaw.TalentNormalRing(0, 0) ==
                  new TalentFrameUiLaw.LogicalRect(44.5f, 84.5f, 64, 64) &&
              TalentFrameUiLaw.TalentRankBorder(0, 0) ==
                  new TalentFrameUiLaw.LogicalRect(79, 118, 32, 32) &&
              TalentFrameUiLaw.TalentTooltipSeat(new Vector2(100, 200),
                  new Vector2(37, 37)) ==
                  new TalentFrameUiLaw.TooltipSeat(new Vector2(137, 200), Vector2.UnitY),
            "TalentFrame button/item-template/rank-border seats drift");

        Check(TalentFrameUiLaw.WheelScroll(0, -1) == 20 &&
              TalentFrameUiLaw.WheelScroll(172, -1) == 172 &&
              TalentFrameUiLaw.ArrowScroll(100, true) == 80 &&
              TalentFrameUiLaw.ArrowScroll(160, false) == 172 &&
              TalentFrameUiLaw.ScrollKnobY(0) == 93 &&
              TalentFrameUiLaw.ScrollKnobY(172) == 377 &&
              TalentFrameUiLaw.ScrollKnob(172) ==
                  new TalentFrameUiLaw.LogicalRect(325, 377, 16, 16) &&
              TalentFrameUiLaw.ScrollFromKnob(101) == 0 &&
              TalentFrameUiLaw.ScrollFromKnob(385) == 172 &&
              TalentFrameUiLaw.ScrollOffset(20, 1.5f) == new Vector2(0, -30) &&
              TalentFrameUiLaw.ScrollBackgroundTopSlice.UvMax ==
                  new Vector2(.484375f, 1) &&
              TalentFrameUiLaw.ScrollControlUvMin == new Vector2(.25f, .25f) &&
              TalentFrameUiLaw.ScrollControlUvMax == new Vector2(.75f, .75f),
            "TalentFrame fixed 504-minus-332 scroll range/20px step/thumb projection drift");

        string[] talentRequirements = TalentFrameUiLaw.TalentTooltipRequirements(
            "Arms", 20, 15, "Defensive Stance", false,
            "Deep Wounds", 2, 1);
        var talentSpell = new SpellTooltipView("Mortal Strike", "Rank 1",
            "30 Rage", "Melee Range", "Instant cast", "6 sec cooldown",
            "A vicious strike that causes weapon damage.");
        TalentFrameUiLaw.TalentTooltipRow[] talentRows =
            TalentFrameUiLaw.TalentTooltipRows(talentSpell, 1, 5,
                talentRequirements, "Causes more weapon damage.", showLearnHint: true);
        Check(talentRequirements.SequenceEqual(new[]
              {
                  "Requires 20 points in Arms Talents",
                  "Requires Defensive Stance",
                  "Requires 2 points in Deep Wounds",
              }) &&
              TalentFrameUiLaw.TalentTooltipRequirements("Arms", 0, 0,
                  null, true, "Improved Heroic Strike", 1, 0)[0] ==
                  "Requires 1 point in Improved Heroic Strike" &&
              TalentFrameUiLaw.ShowTalentLearnHint(1, 4, 5) &&
              !TalentFrameUiLaw.ShowTalentLearnHint(0, 4, 5) &&
              !TalentFrameUiLaw.ShowTalentLearnHint(1, 5, 5) &&
              talentRows.Length == 11 &&
              talentRows[0] == new TalentFrameUiLaw.TalentTooltipRow(
                  "Mortal Strike", null, "GameTooltipHeaderText",
                  TalentFrameUiLaw.TooltipWhite, false) &&
              talentRows[1].Left == "Rank 1/5" &&
              talentRows[2].Color == TalentFrameUiLaw.TooltipRed && talentRows[2].Wrap &&
              talentRows[5].Left == "30 Rage" && talentRows[5].Right == "Melee Range" &&
              talentRows[7].Color == TalentFrameUiLaw.TooltipGold && talentRows[7].Wrap &&
              talentRows[8].Left == "Next rank:" &&
              talentRows[9].Left == "Causes more weapon damage." &&
              talentRows[10].Left == "Click to learn" &&
              talentRows[10].Color == TalentFrameUiLaw.TooltipGreen &&
              TalentFrameUiLaw.TalentTooltipOrigin(
                  new TalentFrameUiLaw.TooltipSeat(new Vector2(137, 200), Vector2.UnitY),
                  new Vector2(100, 80), new Vector2(1920, 1080), 1) ==
                  new Vector2(137, 120),
            "TalentFrame SetTalent tooltip interleave/requirement/owner-right law drift");

        TalentFrameUiLaw.ConnectorSprite[] vertical = TalentFrameUiLaw.BuildConnectors(
            [(0, 0), (2, 0)],
            [new(2, 0, 0, 0, true)]);
        TalentFrameUiLaw.ConnectorSprite[] horizontal = TalentFrameUiLaw.BuildConnectors(
            [(0, 0), (0, 2)],
            [new(0, 2, 0, 0, false)]);
        Check(vertical.Count(x => !x.Arrow) == 3 && vertical.Count(x => x.Arrow) == 1 &&
              vertical.Any(x => x.Arrow && x.Rect ==
                  new TalentFrameUiLaw.LogicalRect(60, 204, 32, 32) &&
                  x.Uv0 == new Vector2(0, 0) && x.Uv1 == new Vector2(.5f, .5f)) &&
              horizontal.Count(x => !x.Arrow) == 4 &&
              horizontal.Any(x => x.Arrow && x.Rect ==
                  new TalentFrameUiLaw.LogicalRect(165, 99, 32, 32) &&
                  x.Uv0 == new Vector2(.5f, .5f) && x.Uv1 == Vector2.One),
            "TalentFrame exact branch/arrow atlas routing drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Talents.cs"));
        Check(runtime.Contains("TalentFrameUiLaw.BuildConnectors", StringComparison.Ordinal) &&
              runtime.Contains("TalentFrameUiLaw.PointsBorder", StringComparison.Ordinal) &&
              runtime.Contains("TalentFrameUiLaw.ScrollMaximum", StringComparison.Ordinal) &&
              runtime.Contains("TalentFrameUiLaw.CloseX", StringComparison.Ordinal) &&
              runtime.Contains("TalentFrameUiLaw.TalentButton(", StringComparison.Ordinal) &&
              runtime.Contains("TalentFrameUiLaw.TalentTooltipSeat", StringComparison.Ordinal) &&
              runtime.Contains("TalentFrameUiLaw.TalentTooltipRows", StringComparison.Ordinal) &&
              runtime.Contains("TalentFrameUiLaw.TalentTooltipRequirements", StringComparison.Ordinal) &&
              runtime.Contains("OfferPreservedSharedGameTooltipRenderer", StringComparison.Ordinal) &&
              runtime.Contains("DrawTalentTooltip", StringComparison.Ordinal) &&
              !runtime.Contains("ImGui.BeginTooltip", StringComparison.Ordinal) &&
              !runtime.Contains("ImGui.TextDisabled", StringComparison.Ordinal) &&
              runtime.Contains("TalentFrameUiLaw.ScrollKnob(", StringComparison.Ordinal) &&
              runtime.Contains("TalentFrameUiLaw.SpentTextTop", StringComparison.Ordinal) &&
              runtime.Contains("TalentFrameUiLaw.TabMinimum", StringComparison.Ordinal) &&
              runtime.Contains("rank.ToString()", StringComparison.Ordinal) &&
              !runtime.Contains("new Vector2", StringComparison.Ordinal) &&
              !runtime.Contains("const float scrollMaximum", StringComparison.Ordinal) &&
              !runtime.Contains("_talentScroll + (up ? -150 : 150)", StringComparison.Ordinal) &&
              !runtime.Contains("dl.AddLine(from, to", StringComparison.Ordinal),
            "TalentFrame renderer bypasses rule-owned geometry/scroll/atlas/rank presentation");
    }
}
