using System.Diagnostics;

namespace MSUIClient.World;

/// <summary>
/// The game-world clock (2026-08-12). Vanilla's server owns the time of day: it
/// sends SMSG_LOGIN_SETTIMESPEED (0x0042) at login - a bit-packed game time
/// (minute:6, hour:5, weekday:3, day:6, month:4, year:5 from the LSB) plus a
/// float timescale in game-minutes per real second (0.01666667 = one game
/// minute per real minute = real time). The client then advances it locally
/// between updates, which is exactly what this class does: store the decoded
/// time plus the Stopwatch stamp it arrived at, and derive the current hour on
/// demand.
///
/// Before a server time arrives (offline, creator mode, the login screen) the
/// clock falls back to this machine's wall-clock time of day, so "track the
/// world's time" degrades to something true rather than to a frozen noon.
///
/// Data-only, like WorldAtmosphere: the game loop decides whether the
/// atmosphere follows this clock (TimeSource.Server), a pinned hour (Fixed) or
/// the accelerated debug cycle (Cycle).
/// </summary>
public sealed class WorldClock
{
    /// <summary>Vanilla's timescale: one game minute per real minute.</summary>
    public const float VanillaTimescale = 1f / 60f;

    private long _receivedStamp;
    private float _baseMinutes;      // game minutes since midnight at receipt
    private float _timescale = VanillaTimescale;

    /// <summary>True once SMSG_LOGIN_SETTIMESPEED has been decoded.</summary>
    public bool HasServerTime { get; private set; }

    /// <summary>Game-minutes advanced per real second (vanilla 0.01666667).</summary>
    public float Timescale => _timescale;

    public int ServerHour { get; private set; }
    public int ServerMinute { get; private set; }
    public int ServerWeekday { get; private set; }
    public int ServerDay { get; private set; }
    public int ServerMonth { get; private set; }
    public int ServerYear { get; private set; }

    /// <summary>
    /// Decode a packed game time + timescale. <paramref name="receivedStamp"/>
    /// is the Stopwatch.GetTimestamp() taken when the packet arrived, so the
    /// elapsed-time base is the wire moment rather than the drain moment.
    /// A non-finite or non-positive timescale is rejected in favour of the
    /// vanilla constant - a zero would freeze the clock forever.
    /// </summary>
    public void SetServerTime(uint packed, float timescale, long receivedStamp)
    {
        ServerMinute = (int)(packed & 0x3F);
        ServerHour = (int)((packed >> 6) & 0x1F);
        ServerWeekday = (int)((packed >> 11) & 0x7);
        ServerDay = (int)((packed >> 14) & 0x3F);
        ServerMonth = (int)((packed >> 20) & 0xF);
        ServerYear = (int)((packed >> 24) & 0x1F);

        _baseMinutes = ServerHour * 60f + ServerMinute;
        _timescale = float.IsFinite(timescale) && timescale > 0f
            ? timescale
            : VanillaTimescale;
        _receivedStamp = receivedStamp;
        HasServerTime = true;
    }

    /// <summary>
    /// Fractional hours 0..24: the decoded server time advanced by elapsed real
    /// seconds x timescale, or the local wall clock when no server time exists.
    /// </summary>
    public float CurrentHours
    {
        get
        {
            if (!HasServerTime)
                return (float)DateTime.Now.TimeOfDay.TotalHours;

            float elapsedSeconds = (float)Stopwatch.GetElapsedTime(_receivedStamp).TotalSeconds;
            float hours = (_baseMinutes + elapsedSeconds * _timescale) / 60f % 24f;
            return hours < 0f ? hours + 24f : hours;
        }
    }
}
