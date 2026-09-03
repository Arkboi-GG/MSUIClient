using MSUIClient;
using MSUIClient.Engine.UI;

/// <summary>
/// Hovercast: the AddOns-page port of the MSUI_Hovercast 1.12 addon. Half of these
/// exercise <see cref="HovercastLaw"/> directly; the rest pin the wiring that makes the
/// law reachable, because a correct law nothing calls is the failure mode this feature
/// is most exposed to (one funnel, four call sites, three publishing surfaces).
/// </summary>
internal static class HovercastClinicalChecks
{
    private const ulong Frame = 0x1111UL;
    private const ulong World = 0x2222UL;

    public static void Run()
    {
        RunLaw();
        RunWiring();
    }

    private static void RunLaw()
    {
        static bool Accepts(ulong _) => true;
        static bool Rejects(ulong _) => false;

        // Off is off: the switch has to be the very first gate, or a disabled feature
        // still pays for the hover lookup and still has an opinion about the press.
        Check(Resolve(enabled: false, frame: Frame, accepts: Accepts) is
                { Redirects: false, Reason: HovercastReason.Disabled },
            "hovercast redirects while disabled");

        // Items and macros pass through untouched, exactly as the addon left them.
        Check(Resolve(spell: false, frame: Frame, accepts: Accepts) is
                { Redirects: false, Reason: HovercastReason.NotASpell },
            "hovercast redirected a non-spell slot");

        // An armed ground/item/commander cursor owns the next pick.
        Check(Resolve(armed: true, frame: Frame, accepts: Accepts) is
                { Redirects: false, Reason: HovercastReason.TargetingArmed },
            "hovercast stole a press from an armed targeting cursor");

        Check(Resolve(accepts: Accepts) is
                { Redirects: false, Reason: HovercastReason.NoHover },
            "hovercast invented a target with nothing hovered");

        // The headline behaviour.
        Check(Resolve(frame: Frame, accepts: Accepts) is
                { Guid: Frame, Source: HovercastSource.UnitFrame, Reason: HovercastReason.UnitFrame },
            "hovercast did not redirect onto the hovered unit frame");

        // World units are opt-in, and a frame always outranks a body.
        Check(Resolve(world: World, accepts: Accepts) is
                { Redirects: false, Reason: HovercastReason.WorldHoverNotEnabled },
            "hovercast redirected onto a world unit with the world option off");
        Check(Resolve(world: World, allowWorld: true, accepts: Accepts) is
                { Guid: World, Source: HovercastSource.WorldUnit },
            "hovercast ignored a world unit with the world option on");
        Check(Resolve(frame: Frame, world: World, allowWorld: true, accepts: Accepts) is
                { Guid: Frame, Source: HovercastSource.UnitFrame },
            "hovercast let a world unit outrank the frame under the cursor");

        // The addon's worst edge: a spell the hovered unit cannot take must FALL THROUGH
        // to ordinary targeting, never be refused. Hovering a party frame cannot be
        // allowed to stop an attack spell reaching the enemy you are actually fighting.
        Check(Resolve(frame: Frame, accepts: Rejects) is
                { Redirects: false, Reason: HovercastReason.UnitRejectsSpell },
            "hovercast refused a press instead of falling through on an ineligible unit");
        Check(Resolve(world: World, allowWorld: true, accepts: Rejects) is
                { Redirects: false, Reason: HovercastReason.UnitRejectsSpell },
            "hovercast refused a world press instead of falling through");
    }

    private static void RunWiring()
    {
        string root = ClientConfig.FindRepoRoot();
        string client = Path.Combine(root, "MSUIClient");

        string bars = SourceText.Read(Path.Combine(client, "GameLoop", "Hud",
            "GameLoop.ActionBars.cs"));
        string hovercast = SourceText.Read(Path.Combine(client, "GameLoop", "Hud",
            "GameLoop.Hovercast.cs"));
        string unitFrames = SourceText.Read(Path.Combine(client, "GameLoop", "Hud",
            "GameLoop.UnitFrames.cs"));
        string partyFrames = SourceText.Read(Path.Combine(client, "GameLoop", "Hud",
            "GameLoop.PartyFrames.cs"));
        string program = SourceText.Read(Path.Combine(client, "Program.cs"));
        string settings = SourceText.Read(Path.Combine(client, "Engine", "GameSettings.cs"));
        string page = SourceText.Read(Path.Combine(client, "GameLoop", "Panels",
            "GameLoop.Settings.cs"));

        // The single funnel. Every bar, key and mouse press reaches UseAction, so the
        // redirect belongs on its one spell branch and nowhere else. If this assertion
        // ever needs a second call site, the funnel has been broken.
        Check(bars.Contains("TryCast(slot.ActionId, HovercastTarget(slot));", StringComparison.Ordinal),
            "UseAction no longer routes its spell branch through HovercastTarget");

        // The redirect must go out as an explicit target, which is what turns off
        // autoSelfCast in ResolveCastTarget -- a hovered unit is a deliberate choice and
        // must never silently become a self-cast.
        Check(bars.Contains("autoSelfCast: explicitTarget == 0", StringComparison.Ordinal),
            "explicit hovercast targets can fall back to self-cast");

        // Eligibility comes from CastTargetLaw and Spell.dbc, never a hand-kept name list.
        // The addon needed HELPFUL_SPELLS/DUAL_SPELLS only because 1.12 Lua could not read
        // the target word; reintroducing one here would silently miss custom spells.
        Check(hovercast.Contains("CastTargetLaw.Resolve(spell, candidate, self: null, autoSelfCast: false)",
                StringComparison.Ordinal),
            "hovercast eligibility no longer defers to CastTargetLaw");
        Check(!hovercast.Contains("HELPFUL_SPELLS", StringComparison.Ordinal) &&
              !hovercast.Contains("DUAL_SPELLS", StringComparison.Ordinal),
            "hovercast grew a hand-maintained spell name list");

        // Double buffering. The surfaces publish during Render and UseAction reads during
        // Update, so a single field would be cleared by whichever surface drew last.
        Check(hovercast.Contains("_hovercastFrameHoverPending", StringComparison.Ordinal) &&
              program.Contains("BeginHovercastFrame();", StringComparison.Ordinal),
            "hovercast hover is not promoted on the frame boundary");

        // All three publishing surfaces. Party frames are the reason the feature exists.
        Check(unitFrames.Contains("NoteHovercastFrameHover(unit.Guid, ImGui.IsItemHovered());",
                  StringComparison.Ordinal) &&
              partyFrames.Contains("NoteHovercastFrameHover(view.Member.Guid, hovered);",
                  StringComparison.Ordinal),
            "a unit frame surface stopped publishing its hover to hovercast");

        // Off by default: this rebinds what an action key already does, and every player
        // receives the switch whether they went looking for it or not.
        Check(settings.Contains("public bool Hovercast { get; set; }", StringComparison.Ordinal) &&
              settings.Contains("public bool HovercastWorldUnits { get; set; }", StringComparison.Ordinal),
            "hovercast settings are missing or no longer default to off");

        // Reachable in the UI, or it does not exist as far as a player is concerned.
        Check(page.Contains("Enable Hovercast", StringComparison.Ordinal) &&
              page.Contains("Include world units", StringComparison.Ordinal),
            "the AddOns page no longer offers the hovercast switches");
        Check(OptionsSearchUiLaw.Catalog.Any(entry =>
                entry.Page == OptionsSearchPage.AddOns &&
                entry.Label.Equals("Hovercast", StringComparison.Ordinal)),
            "hovercast is not findable from options search");
    }

    private static HovercastVerdict Resolve(
        bool enabled = true, bool allowWorld = false, bool spell = true, bool armed = false,
        ulong frame = 0, ulong world = 0, Func<ulong, bool>? accepts = null) =>
        HovercastLaw.Resolve(enabled, allowWorld, spell, armed, frame, world,
            accepts ?? (_ => true));

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
