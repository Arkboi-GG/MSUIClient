using MSUIClient;
using MSUIClient.Engine.UI;

/// <summary>
/// Player Power Bars: the AddOns-page port of the MSUI_PowerBars 1.12 addon. The law half
/// covers geometry, the tick inference and captions; the wiring half pins that the bars are
/// actually drawn, configurable, and reading real per-type power rather than the addon's
/// UnitMana workaround.
/// </summary>
internal static class PlayerPowerBarsClinicalChecks
{
    private const byte Energy = PlayerPowerBarsLaw.EnergyPowerType;
    private const byte Rage = 1;

    public static void Run()
    {
        RunLayout();
        RunTick();
        RunCaptions();
        RunWiring();
    }

    private static void RunLayout()
    {
        PlayerPowerBarsLayout l = PlayerPowerBarsLaw.Layout(200f, 20f, 14f, 2f, 0);

        // Health on top, power below it by exactly the gap, total height is the sum.
        Check(l.HealthMin.Y == 0f && l.HealthSize == new System.Numerics.Vector2(200f, 20f),
            "power bars health rect drifted");
        Check(l.PowerMin.Y == 22f && l.PowerSize.Y == 14f,
            "power bars power rect is not stacked under health across the gap");
        Check(l.Size.Y == 36f && l.Size.X == 200f, "power bars total size drifted");

        // No pips requested means no combo row at all, not a zero-width one that still
        // reserves lift above the bars.
        Check(l.ComboSize.X == 0f && l.ComboSize.Y == 0f, "power bars reserved an empty combo row");

        // Pips sit ABOVE the health bar (negative Y) and centred on it. The addon lifts them
        // clear of the bar rather than overlapping it.
        PlayerPowerBarsLayout combo = PlayerPowerBarsLaw.Layout(200f, 20f, 14f, 2f, 5);
        Check(combo.ComboMin.Y < 0f, "power bars combo row is not above the health bar");
        Check(Math.Abs(combo.ComboMin.X + combo.ComboSize.X * .5f - 100f) < .01f,
            "power bars combo row is not centred over the bars");
        Check(combo.ComboSize.X ==
              5 * PlayerPowerBarsLaw.ComboPipSize + 4 * PlayerPowerBarsLaw.ComboPipGap,
            "power bars combo row width drifted from five pips and four gaps");
        Check(PlayerPowerBarsLaw.ComboPipMin(0).X == 0f &&
              PlayerPowerBarsLaw.ComboPipMin(1).X ==
                  PlayerPowerBarsLaw.ComboPipSize + PlayerPowerBarsLaw.ComboPipGap,
            "power bars pip spacing drifted");

        // A hand-edited settings.json must not be able to produce a zero-height bar or a
        // frame wider than any screen.
        PlayerPowerBarsLayout absurd = PlayerPowerBarsLaw.Layout(99999f, 0f, -5f, 999f, 0);
        Check(absurd.Size.X == PlayerPowerBarsLaw.MaximumWidth,
            "power bars width is not clamped");
        Check(absurd.HealthSize.Y >= PlayerPowerBarsLaw.MinimumBarHeight &&
              absurd.PowerSize.Y >= PlayerPowerBarsLaw.MinimumBarHeight,
            "power bars bar height is not clamped away from zero");
    }

    private static void RunTick()
    {
        // Only an upward move is a tick. Spending energy must not restart the sweep, and
        // no other power type has a tick worth drawing.
        Check(PlayerPowerBarsLaw.IsRegenTick(Energy, 60, 80), "power bars missed an energy tick");
        Check(!PlayerPowerBarsLaw.IsRegenTick(Energy, 80, 60),
            "power bars treated spending energy as a tick");
        Check(!PlayerPowerBarsLaw.IsRegenTick(Energy, 80, 80),
            "power bars treated an unchanged value as a tick");
        Check(!PlayerPowerBarsLaw.IsRegenTick(Rage, 10, 40),
            "power bars treated a rage gain as an energy tick");

        // Nothing to sweep before a tick has been seen, when switched off, or on a power
        // type that does not tick.
        Check(PlayerPowerBarsLaw.TickSweep(true, Energy, 10d, null, 2f) is null,
            "power bars swept before observing a tick");
        Check(PlayerPowerBarsLaw.TickSweep(false, Energy, 10d, 9d, 2f) is null,
            "power bars swept while the option was off");
        Check(PlayerPowerBarsLaw.TickSweep(true, Rage, 10d, 9d, 2f) is null,
            "power bars swept a non-energy power type");

        // Halfway through a 2s tick is halfway across the bar.
        Check(PlayerPowerBarsLaw.TickSweep(true, Energy, 11d, 10d, 2f) is { } half &&
              Math.Abs(half - .5f) < .001f, "power bars sweep position drifted");

        // The sweep WRAPS rather than sticking at the right edge. At full energy the value
        // stops changing, so no new tick can be observed - the cursor has to keep cycling
        // on the known cadence instead of freezing.
        Check(PlayerPowerBarsLaw.TickSweep(true, Energy, 17d, 10d, 2f) is { } wrapped &&
              Math.Abs(wrapped - .5f) < .001f, "power bars sweep did not wrap past one period");
        Check(PlayerPowerBarsLaw.ClampTickSeconds(99f) == PlayerPowerBarsLaw.MaximumTickSeconds &&
              PlayerPowerBarsLaw.ClampTickSeconds(0f) == PlayerPowerBarsLaw.MinimumTickSeconds,
            "power bars tick interval is not clamped");

        // The server's real tick, from its own source (Player::RegenerateAll, Player.cpp:2318).
        Check(PlayerPowerBarsLaw.DefaultTickSeconds == 2f,
            "power bars default tick no longer matches the server's 2.0s regen tick");
    }

    private static void RunCaptions()
    {
        Check(PlayerPowerBarsLaw.TextMode(showText: false, showPercent: true) == PowerBarText.None,
            "power bars text mode ignored the master text switch");
        Check(PlayerPowerBarsLaw.TextMode(true, false) == PowerBarText.ValueOverMax &&
              PlayerPowerBarsLaw.TextMode(true, true) == PowerBarText.Percent,
            "power bars text mode drifted");

        Check(PlayerPowerBarsLaw.Caption(PowerBarText.None, 50, 100) == "",
            "power bars wrote a caption while text was off");
        Check(PlayerPowerBarsLaw.Caption(PowerBarText.ValueOverMax, 50, 100) == "50 / 100",
            "power bars value caption drifted");
        Check(PlayerPowerBarsLaw.Caption(PowerBarText.Percent, 50, 100) == "50%",
            "power bars percent caption drifted");

        // An entity whose fields have not populated yet must not divide by zero on the
        // frame it first appears.
        Check(PlayerPowerBarsLaw.Caption(PowerBarText.Percent, 0, 0) == "0%",
            "power bars divided by a zero maximum");
    }

    private static void RunWiring()
    {
        string root = ClientConfig.FindRepoRoot();
        string client = Path.Combine(root, "MSUIClient");

        string bars = SourceText.Read(Path.Combine(client, "GameLoop", "Hud",
            "GameLoop.PlayerPowerBars.cs"));
        string feedback = SourceText.Read(Path.Combine(client, "GameLoop", "Combat",
            "GameLoop.CombatFeedback.cs"));
        string settings = SourceText.Read(Path.Combine(client, "Engine", "GameSettings.cs"));
        string page = SourceText.Read(Path.Combine(client, "GameLoop", "Panels",
            "GameLoop.Settings.cs"));

        // Drawn every frame beside the player frame, not instead of it.
        Check(feedback.Contains("DrawPlayerPowerBars();", StringComparison.Ordinal),
            "the player power bars are never drawn");

        // THE headline of this port: the addon read every power type through UnitMana
        // because UnitRage/UnitEnergy/UnitFocus do not exist on this client. The native
        // client reads the real per-type field, so nothing here may reintroduce a
        // single-getter workaround or a per-class branch to pick the value.
        Check(bars.Contains("player.Fields.ActivePower", StringComparison.Ordinal) &&
              bars.Contains("player.Fields.ActiveMaxPower", StringComparison.Ordinal),
            "power bars no longer read the real per-type power field");
        Check(!bars.Contains("UnitMana", StringComparison.Ordinal),
            "power bars reintroduced the UnitMana single-getter workaround");

        // The tick reset. Possession or a Druid leaving Cat Form must not carry a stale
        // energy phase across onto a different unit or power type.
        Check(bars.Contains("player.Guid != _powerBarsPreviousUnit", StringComparison.Ordinal) &&
              bars.Contains("type != _powerBarsPreviousType", StringComparison.Ordinal),
            "power bars carry a stale tick phase across a unit or power-type change");

        // Position persists, and the file is written on release rather than every drag frame.
        Check(bars.Contains("SettingsFile?.Save();", StringComparison.Ordinal) &&
              bars.Contains("ImGui.IsItemDeactivated()", StringComparison.Ordinal),
            "power bars position is saved on every drag frame or not at all");

        // Off by default: this adds furniture to the screen, and the player frame already
        // shows health and power.
        Check(settings.Contains("public bool Enabled { get; set; }", StringComparison.Ordinal),
            "player power bars no longer default to off");

        // Configurable is the point of the feature, so the controls have to exist.
        foreach (string control in new[]
        {
            "Enable Player Power Bars", "Unlock bars (drag to move)", "Width",
            "Health bar height", "Power bar height", "Scale",
            "Show values on the bars", "Show combo points", "Show the energy tick sweep",
        })
            Check(page.Contains(control, StringComparison.Ordinal),
                $"the AddOns page lost the '{control}' control");

        Check(OptionsSearchUiLaw.Catalog.Any(entry =>
                entry.Page == OptionsSearchPage.AddOns &&
                entry.Label.Equals("Player Power Bars", StringComparison.Ordinal)),
            "player power bars are not findable from options search");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
