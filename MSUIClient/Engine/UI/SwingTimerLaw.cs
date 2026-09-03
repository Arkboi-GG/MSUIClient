namespace MSUIClient.Engine.UI;

/// <summary>Which weapon a cursor on the rail belongs to.</summary>
public enum SwingHand
{
    MainHand,
    OffHand,
    Ranged,
}

/// <summary>Melee and ranged auto-attack are mutually exclusive in 1.12, so the rail shows
/// one or the other rather than both at once.</summary>
public enum SwingMode
{
    Melee,
    Ranged,
}

/// <summary>One weapon's swing: when it started and how long it takes. Duration comes from
/// the unit's real attack-time field, so a haste change is picked up on the next swing.</summary>
public readonly record struct SwingTrack(double StartedAt, float Duration)
{
    public bool Running(double now) => Duration > 0f && now - StartedAt < Duration;

    /// <summary>0 just swung, 1 ready. Null when this weapon has no swing in flight.</summary>
    public float? Progress(double now)
    {
        if (Duration <= 0f) return null;
        double elapsed = now - StartedAt;
        if (elapsed < 0d || elapsed >= Duration) return null;
        return (float)(elapsed / Duration);
    }
}

/// <summary>
/// Swing Timer — one shared rail, thin cursors sweeping left (just swung) to right (ready).
/// Ported from the MSUI_SwingTimer 1.12 addon.
///
/// This is the port that discards the most, because almost everything load-bearing in the
/// addon was a workaround for information 1.12 Lua could not see:
///
///   - "1.12 has no structured combat log; swings come from chat combat events". The addon
///     subscribed to CHAT_MSG_COMBAT_SELF_HITS / _MISSES / CHAT_MSG_SPELL_SELF_DAMAGE and
///     matched LOCALIZED CHAT TEXT against hand-written spell-name tables. The native client
///     parses SMSG_ATTACKERSTATEUPDATE into a typed CombatMeleeSwing carrying the attacker
///     guid, so a swing is an event, not a string.
///   - "OFFHAND ATTRIBUTION: 1.12 gives no hand-of-origin on a white hit, so melee resets
///     re-seed the most-expired hand". That heuristic is gone: HitInfo carries the offhand
///     bit (0x4), which this client already reads for swing animations and melee sounds.
///   - Its MELEE_SWING_SPELLS / RANGED_SWING_SPELLS [TUNE] name tables stood in for spell
///     flags. SpellInfo.OnNextSwing and SpellInfo.Ranged are the real ones, so Heroic Strike
///     and Auto Shot are recognised by attribute rather than by English name.
///   - UnitAttackSpeed / UnitRangedDamage become the UNIT_BASEATTACKTIME and
///     UNIT_RANGEDATTACKTIME fields, in milliseconds, per weapon.
///   - Its GetNetStats poll every 30 seconds becomes NetworkClient.LatencyMs, which this
///     client measures on its own socket.
///
/// What survives is the shape of the display and the ranged aim window, which are real 1.12
/// mechanics rather than workarounds.
/// </summary>
public static class SwingTimerLaw
{
    public const float MinimumWidth = 80f;
    public const float MaximumWidth = 600f;
    public const float MinimumHeight = 8f;
    public const float MaximumHeight = 40f;
    public const float MinimumScale = .5f;
    public const float MaximumScale = 2f;

    /// <summary>Width of a cursor on the rail, in logical units.</summary>
    public const float CursorWidth = 3f;

    /// <summary>
    /// Auto Shot's aim time. The addon carries this as a [TUNE] game constant and so does
    /// this port — it is not derived from any field or packet, and it is the one number here
    /// that is still inherited rather than measured.
    /// </summary>
    public const float AimSeconds = .5f;

    public const float MinimumTravelSeconds = 0f;
    public const float MaximumTravelSeconds = .5f;

    public static float ClampWidth(float value) => Math.Clamp(value, MinimumWidth, MaximumWidth);

    public static float ClampHeight(float value) =>
        Math.Clamp(value, MinimumHeight, MaximumHeight);

    public static float ClampScale(float value) => Math.Clamp(value, MinimumScale, MaximumScale);

    public static float ClampTravel(float value) =>
        Math.Clamp(value, MinimumTravelSeconds, MaximumTravelSeconds);

    /// <summary>
    /// A weapon's swing period in seconds, from its millisecond attack-time field. Zero means
    /// the weapon is not present — an empty offhand reports 0 and must draw no cursor at all,
    /// which is how dual-wield detection works without asking the class or the inventory.
    /// </summary>
    public static float SwingSeconds(uint attackTimeMs) =>
        attackTimeMs == 0 ? 0f : attackTimeMs / 1000f;

    /// <summary>
    /// How far back to start a swing that the server has already processed. The packet spent
    /// roughly half a round trip in flight, so the swing is already that far along by the time
    /// it is seen. The addon could only do this for ranged, from a 30-second GetNetStats
    /// sample; here it is one measurement applied consistently.
    /// </summary>
    public static double FlightCompensation(bool enabled, int latencyMs) =>
        !enabled || latencyMs <= 0 ? 0d : latencyMs / 2000d;

    /// <summary>
    /// The red plant/aim band at the end of a ranged reload, as a 0..1 span of the rail.
    /// Null when there is no aim penalty to draw: wands have none, and a reload shorter than
    /// the aim time would cover the whole rail and say nothing.
    /// </summary>
    public static (float Start, float End)? AimBand(bool show, bool hasAimPenalty,
        float rangedDuration)
    {
        if (!show || !hasAimPenalty || rangedDuration <= AimSeconds) return null;
        return ((rangedDuration - AimSeconds) / rangedDuration, 1f);
    }

    /// <summary>
    /// Which half of the rail is live. Melee and ranged auto-attack cannot both run in 1.12,
    /// so the last thing to swing owns the display. Ranged wins ties because starting a shot
    /// is an explicit act while a melee timer may merely still be winding down.
    /// </summary>
    public static SwingMode Mode(bool trackMelee, bool trackRanged,
        double lastMeleeAt, double lastRangedAt)
    {
        if (!trackRanged) return SwingMode.Melee;
        if (!trackMelee) return SwingMode.Ranged;
        return lastRangedAt >= lastMeleeAt ? SwingMode.Ranged : SwingMode.Melee;
    }

    /// <summary>
    /// Whether the rail is on screen at all. Unlocked always shows so the bar can be found
    /// and dragged; otherwise "hide when idle" hides it once nothing is swinging.
    /// </summary>
    public static bool Visible(bool enabled, bool unlocked, bool hideWhenIdle, bool anyRunning) =>
        enabled && (unlocked || anyRunning || !hideWhenIdle);

    /// <summary>Left edge of a cursor at this progress, as an offset into the rail's width.</summary>
    public static float CursorOffset(float progress, float railWidth)
    {
        float usable = MathF.Max(0f, railWidth - CursorWidth);
        return Math.Clamp(progress, 0f, 1f) * usable;
    }

    /// <summary>Seconds until this weapon is ready, for the caption. Null when idle.</summary>
    public static float? Remaining(in SwingTrack track, double now) =>
        track.Progress(now) is null ? null : (float)(track.Duration - (now - track.StartedAt));
}
