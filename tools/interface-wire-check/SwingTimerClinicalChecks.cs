using MSUIClient;
using MSUIClient.Engine.UI;

/// <summary>
/// Swing Timer: the AddOns-page port of the MSUI_SwingTimer 1.12 addon. The wiring half
/// matters more here than in the other two ports, because almost everything load-bearing in
/// the addon was a workaround for information 1.12 Lua could not see — and every one of
/// those workarounds has a native replacement that must not quietly regress back.
/// </summary>
internal static class SwingTimerClinicalChecks
{
    public static void Run()
    {
        RunLaw();
        RunWiring();
    }

    private static void RunLaw()
    {
        // Attack times arrive in milliseconds. Zero means the weapon is absent, which is how
        // an empty off hand draws no cursor without asking the class or the inventory.
        Check(SwingTimerLaw.SwingSeconds(2600) == 2.6f, "swing seconds drifted from ms");
        Check(SwingTimerLaw.SwingSeconds(0) == 0f,
            "an absent weapon no longer reports a zero swing");

        var track = new SwingTrack(100d, 2f);
        Check(track.Progress(100d) == 0f, "a fresh swing does not start at the left");
        Check(track.Progress(101d) is { } half && Math.Abs(half - .5f) < .001f,
            "swing progress drifted");
        Check(track.Progress(102d) is null && track.Progress(103d) is null,
            "a finished swing still reports progress");
        Check(track.Progress(99d) is null, "a swing reported progress before it started");
        Check(new SwingTrack(100d, 0f).Progress(100d) is null,
            "a zero-duration swing reported progress");

        Check(SwingTimerLaw.Remaining(track, 100.5d) is { } left &&
              Math.Abs(left - 1.5f) < .001f, "remaining seconds drifted");
        Check(SwingTimerLaw.Remaining(track, 105d) is null,
            "a finished swing still reports remaining time");

        // Flight compensation: half the round trip, and nothing at all when switched off or
        // when latency has not been measured.
        Check(SwingTimerLaw.FlightCompensation(true, 200) == .1d,
            "flight compensation is not half the round trip");
        Check(SwingTimerLaw.FlightCompensation(false, 200) == 0d,
            "flight compensation applied while disabled");
        Check(SwingTimerLaw.FlightCompensation(true, 0) == 0d,
            "flight compensation invented a delay with no measurement");

        // The aim band covers the last half second, and refuses the cases where it would lie.
        Check(SwingTimerLaw.AimBand(true, true, 2f) is { } band &&
              Math.Abs(band.Start - .75f) < .001f && band.End == 1f,
            "ranged aim band drifted from the last half second");
        Check(SwingTimerLaw.AimBand(true, false, 2f) is null,
            "aim band drawn for a weapon with no aim penalty");
        Check(SwingTimerLaw.AimBand(false, true, 2f) is null,
            "aim band drawn while the option was off");
        Check(SwingTimerLaw.AimBand(true, true, .4f) is null,
            "aim band covered a whole reload shorter than the aim time");

        // Melee and ranged are mutually exclusive in 1.12: the most recent wins, and a
        // disabled half never takes the rail.
        Check(SwingTimerLaw.Mode(true, true, 10d, 20d) == SwingMode.Ranged &&
              SwingTimerLaw.Mode(true, true, 20d, 10d) == SwingMode.Melee,
            "swing mode does not follow the most recent swing");
        Check(SwingTimerLaw.Mode(true, false, 0d, 99d) == SwingMode.Melee &&
              SwingTimerLaw.Mode(false, true, 99d, 0d) == SwingMode.Ranged,
            "swing mode ignored a disabled half");

        // Visibility: unlocked always shows so the rail can be found and dragged.
        Check(!SwingTimerLaw.Visible(false, true, false, true),
            "the rail drew while the feature was disabled");
        Check(SwingTimerLaw.Visible(true, true, true, false),
            "the rail hid while unlocked and could not be dragged");
        Check(!SwingTimerLaw.Visible(true, false, true, false),
            "the rail stayed up while idle with hide-when-idle on");
        Check(SwingTimerLaw.Visible(true, false, false, false),
            "the rail hid while idle with hide-when-idle off");

        // Cursors stay on the rail at both ends.
        Check(SwingTimerLaw.CursorOffset(0f, 100f) == 0f &&
              SwingTimerLaw.CursorOffset(1f, 100f) == 100f - SwingTimerLaw.CursorWidth,
            "a cursor left the rail at one of its ends");
        Check(SwingTimerLaw.CursorOffset(2f, 100f) == 100f - SwingTimerLaw.CursorWidth &&
              SwingTimerLaw.CursorOffset(-1f, 100f) == 0f,
            "cursor offset is not clamped");

        Check(SwingTimerLaw.ClampWidth(9999f) == SwingTimerLaw.MaximumWidth &&
              SwingTimerLaw.ClampHeight(0f) == SwingTimerLaw.MinimumHeight &&
              SwingTimerLaw.ClampTravel(9f) == SwingTimerLaw.MaximumTravelSeconds,
            "swing timer settings are not clamped");
    }

    private static void RunWiring()
    {
        string root = ClientConfig.FindRepoRoot();
        string client = Path.Combine(root, "MSUIClient");

        string timer = SourceText.Read(Path.Combine(client, "GameLoop", "Hud",
            "GameLoop.SwingTimer.cs"));
        string animations = SourceText.Read(Path.Combine(client, "GameLoop", "Combat",
            "GameLoop.CombatAnimations.cs"));
        string casting = SourceText.Read(Path.Combine(client, "GameLoop", "Combat",
            "GameLoop.Casting.cs"));
        string feedback = SourceText.Read(Path.Combine(client, "GameLoop", "Combat",
            "GameLoop.CombatFeedback.cs"));
        string settings = SourceText.Read(Path.Combine(client, "Engine", "GameSettings.cs"));
        string page = SourceText.Read(Path.Combine(client, "GameLoop", "Panels",
            "GameLoop.Settings.cs"));

        Check(feedback.Contains("DrawSwingTimer();", StringComparison.Ordinal),
            "the swing timer rail is never drawn");

        // THE headline of this port. The addon read swings by subscribing to chat events and
        // string-matching LOCALIZED combat text against hand-written spell-name tables,
        // because "1.12 has no structured combat log". This client parses
        // SMSG_ATTACKERSTATEUPDATE into a typed event. Nothing here may go back to text.
        Check(animations.Contains("NoteSwingTimerMelee(swing);", StringComparison.Ordinal),
            "melee swings no longer reach the rail from the typed combat event");
        Check(!timer.Contains("CHAT_MSG", StringComparison.Ordinal) &&
              !timer.Contains("MELEE_SWING_SPELLS", StringComparison.Ordinal) &&
              !timer.Contains("RANGED_SWING_SPELLS", StringComparison.Ordinal),
            "the swing timer reintroduced chat parsing or a spell-name table");

        // The addon could not tell which hand struck ("1.12 gives no hand-of-origin on a
        // white hit") and re-seeded the most-expired hand as a guess. The offhand bit is
        // right there in HitInfo, and this client already reads it for animations and sounds.
        // Deliberately a POSITIVE assertion. An earlier version of this check also banned
        // the phrase "most-expired", which failed on the source comment explaining what the
        // addon used to do — a check that forbids documenting the trap is worse than no
        // check. What matters is that the hand comes from the bit, and that is stated here.
        Check(timer.Contains("HitInfoOffHand", StringComparison.Ordinal) &&
              timer.Contains("0x0004u", StringComparison.Ordinal) &&
              timer.Contains("(swing.HitInfo & HitInfoOffHand) != 0", StringComparison.Ordinal),
            "off-hand attribution no longer reads the HitInfo bit");

        // Ranged shots are identified by the real spell attribute, not an English name.
        Check(casting.Contains("NoteSwingTimerRanged(packet.SpellId, rangedInfo, packet.Caster);",
                  StringComparison.Ordinal) &&
              timer.Contains("info.Ranged", StringComparison.Ordinal),
            "ranged shots no longer reach the rail from SpellInfo.Ranged");

        // Durations come from the real per-weapon attack-time fields, not UnitAttackSpeed.
        Check(timer.Contains("self.Fields.MainAttackTime", StringComparison.Ordinal) &&
              timer.Contains("self.Fields.OffhandAttackTime", StringComparison.Ordinal) &&
              timer.Contains("self.Fields.RangedAttackTime", StringComparison.Ordinal),
            "swing durations no longer read the real per-weapon attack-time fields");
        Check(!timer.Contains("UnitAttackSpeed", StringComparison.Ordinal) &&
              !timer.Contains("GetNetStats", StringComparison.Ordinal),
            "the swing timer reintroduced a 1.12 Lua getter");

        // Latency comes from this client's own socket, not a 30-second sample.
        Check(timer.Contains("_net.LatencyMs", StringComparison.Ordinal),
            "flight compensation no longer uses the measured socket latency");

        Check(settings.Contains("public bool Enabled { get; set; }", StringComparison.Ordinal),
            "the swing timer no longer defaults to off");

        foreach (string control in new[]
        {
            "Enable Swing Timer", "Unlock rail (drag to move)", "Track melee swings",
            "Track ranged shots", "Hide when idle", "Show the ranged aim band",
            "Compensate for latency",
        })
            Check(page.Contains(control, StringComparison.Ordinal),
                $"the AddOns page lost the '{control}' control");

        Check(OptionsSearchUiLaw.Catalog.Any(entry =>
                entry.Page == OptionsSearchPage.AddOns &&
                entry.Label.Equals("Swing Timer", StringComparison.Ordinal)),
            "the swing timer is not findable from options search");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
