using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.World;

/// <summary>
/// The vanilla client's global day/night cycle table, `World\dnc.db`
/// (misc.MPQ). This is the file the 1.12 client uses to run the sun and moon
/// over the day: per-hour rows carrying the DIRECTIONAL LIGHT INTENSITIES
/// (DayIntensity / NightIntensity), the light directions (DayX/Y/Z,
/// NightX/Y/Z), and grey day/night/ambient/fog colour ramps. It was removed in
/// TBC when Light.dbc absorbed the job.
///
/// Discovered for MSUI during the 2026-08-12 lighting-mode pass
/// (SYSTEM_EXTERIOR_LIGHTING.md "Lighting modes"): Light.dbc band 0 - "global
/// diffuse" - reads as implausible pure orange 0xFF8800 at noon precisely
/// because the vanilla client never applies it raw. It multiplies it by this
/// table's intensity curve (0 at night, ramping to 0.8 through daytime), the
/// same combination the vanilla-era open renderers (wowmapview lineage) used:
/// diffuse = band0 * DayIntensity. The 1.12 Parity lighting mode reproduces
/// that; MSUI mode ignores this table entirely.
///
/// FORMAT (reverse-read from Nico's own file, 5201 bytes):
///   uint32 columnCount            (25)
///   uint32 recordCount            (25 as stored - but see below)
///   columnCount x { uint32 type; uint32 nameOffset; }   type 'S' = string
///   rows x columnCount x { uint32 type; 4-byte value; } type 'F' = float
///   string block (the column names) at the smallest nameOffset
///
/// The stored recordCount over-counts: the real row count is what fits between
/// the cell area and the string block ((5008-208)/(25*8) = 24 rows, hours
/// 0..23). Derived, not trusted.
///
/// NOT WIRED: DayX/Y/Z is the authored sun arc (X pinned at 0.7, Y/Z rotating
/// hourly). WorldAtmosphere.SunDirectionAt keeps its invented arc in both
/// modes for now - the dnc arc's mapping into our world space is unverified.
/// </summary>
public sealed class DayNightCycle
{
    public const string MpqPath = @"World\dnc.db";

    private float[] _hours = [];
    private float[] _dayIntensity = [];
    private float[] _nightIntensity = [];

    // The colour and direction ramps (2026-08-14, the night-parity pass).
    // These are what actually make vanilla's night: at midnight the day light
    // is BLACK at intensity 0 and the night light is PURE BLUE (0, 0, 0.5) at
    // intensity 1 - so "moonlight" is band 0 multiplied down to its blue
    // component only, and the ambient ramp (0.3, 0.3, 0.6) x 0.8 pulls every
    // shadowed face to blue-violet. Intensity alone (the pre-pass wiring)
    // reproduced none of that, which is why night read as a dimmed day.
    private float[] _dayR = [], _dayG = [], _dayB = [];
    private float[] _dayX = [], _dayY = [], _dayZ = [];
    private float[] _nightR = [], _nightG = [], _nightB = [];
    private float[] _nightX = [], _nightY = [], _nightZ = [];
    private float[] _ambientIntensity = [];
    private float[] _ambientR = [], _ambientG = [], _ambientB = [];

    public bool Ready { get; private set; }

    /// <summary>Why the table is empty, when it is. For logs and the probe.</summary>
    public string Status { get; private set; } = "not loaded";

    public void Load(string clientDataPath)
    {
        var data = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, MpqPath);
        if (data is null)
        {
            Status = @"World\dnc.db not found in the MPQs";
            Console.WriteLine($"[dnc] {Status} - 1.12 Parity uses no intensity curve");
            return;
        }

        try
        {
            Parse(data);
            Status = $"{_hours.Length} hourly row(s)";
            Console.WriteLine($"[dnc] loaded: {Status} " +
                              $"(noon day intensity {DayIntensityAt(12f):F2})");
            Ready = _hours.Length >= 2;
        }
        catch (Exception ex)
        {
            Status = $"dnc.db failed to parse: {ex.Message}";
            Console.WriteLine($"[dnc] {Status} - 1.12 Parity uses no intensity curve");
        }
    }

    private void Parse(byte[] d)
    {
        int columnCount = BitConverter.ToInt32(d, 0);
        if (columnCount <= 0 || columnCount > 64)
            throw new InvalidDataException($"implausible column count {columnCount}");

        // Column names, and while walking them, the start of the string block -
        // which is what bounds the cell area and yields the REAL row count.
        var names = new string[columnCount];
        int stringBlockStart = d.Length;
        int off = 8;
        for (int c = 0; c < columnCount; c++)
        {
            int nameOffset = BitConverter.ToInt32(d, off + 4);
            off += 8;
            if (nameOffset < 0 || nameOffset >= d.Length)
                throw new InvalidDataException($"column {c} name offset {nameOffset} out of range");
            stringBlockStart = Math.Min(stringBlockStart, nameOffset);

            int end = Array.IndexOf(d, (byte)0, nameOffset);
            if (end < 0) end = d.Length;
            names[c] = System.Text.Encoding.ASCII.GetString(d, nameOffset, end - nameOffset);
        }

        int dataStart = off;
        int rowBytes = columnCount * 8;
        int rowCount = Math.Max(0, (stringBlockStart - dataStart) / rowBytes);

        float[] Column(string name)
        {
            int col = Array.IndexOf(names, name);
            if (col < 0)
                throw new InvalidDataException(
                    $"expected column {name} among [{string.Join(",", names)}]");
            var values = new float[rowCount];
            for (int r = 0; r < rowCount; r++)
                values[r] = BitConverter.ToSingle(d, dataStart + r * rowBytes + col * 8 + 4);
            return values;
        }

        _hours = Column("Hour");
        _dayIntensity = Column("DayIntensity");
        _nightIntensity = Column("NightIntensity");
        _dayR = Column("DayR"); _dayG = Column("DayG"); _dayB = Column("DayB");
        _dayX = Column("DayX"); _dayY = Column("DayY"); _dayZ = Column("DayZ");
        _nightR = Column("NightR"); _nightG = Column("NightG"); _nightB = Column("NightB");
        _nightX = Column("NightX"); _nightY = Column("NightY"); _nightZ = Column("NightZ");
        _ambientIntensity = Column("AmbientIntensity");
        _ambientR = Column("AmbientR"); _ambientG = Column("AmbientG"); _ambientB = Column("AmbientB");
    }

    /// <summary>What dnc.db multiplies onto the zone's Light.dbc DIFFUSE at one
    /// moment (the combined sun+moon scale), plus the dominant light's TO-LIGHT
    /// direction in world space. AMBIENT IS DELIBERATELY ABSENT: the reference
    /// client uploads Light.dbc band 1 raw (benilla's recovered 19:45 Stormwind
    /// trace shows the unscaled band value in the GL ambient constant), and the
    /// first cut of this pass multiplying the dnc ambient ramp in as well
    /// crushed night to near-black - the owner's "wtf thats wrong" screenshot,
    /// 2026-08-15. The ramps run the two directional lights; ambient is the
    /// zone's own.</summary>
    public readonly record struct Modulation(Vector3 SunScale, Vector3 SunDirection);

    /// <summary>
    /// The 1.12 global cycle at an hour. The real client runs the sun and moon
    /// as two GL lights; we have one directional, so the two diffuse terms are
    /// SUMMED (the table never has both non-black at once - the day colour
    /// fades to black by 20:00 and the night blue only appears from 21:00) and
    /// the direction follows whichever term carries the light.
    /// </summary>
    public Modulation ModulationAt(float hours)
    {
        var day = new Vector3(Sample(_dayR, hours), Sample(_dayG, hours), Sample(_dayB, hours))
                  * Sample(_dayIntensity, hours);
        var night = new Vector3(Sample(_nightR, hours), Sample(_nightG, hours), Sample(_nightB, hours))
                    * Sample(_nightIntensity, hours);

        // The stored vectors are the light's TRAVEL direction; shaders dot the
        // normal against TO-LIGHT, so negate. Weight by each term's luminance.
        var dayDir = new Vector3(Sample(_dayX, hours), Sample(_dayY, hours), Sample(_dayZ, hours));
        var nightDir = new Vector3(Sample(_nightX, hours), Sample(_nightY, hours), Sample(_nightZ, hours));
        float dayLum = day.X + day.Y + day.Z;
        float nightLum = night.X + night.Y + night.Z;
        float total = dayLum + nightLum;
        Vector3 travel = total > 1e-4f
            ? Vector3.Normalize(dayDir * (dayLum / total) + nightDir * (nightLum / total))
            : Vector3.Normalize(dayDir);

        return new Modulation(day + night, -travel);
    }

    /// <summary>Print every column of every row - the whole authored table -
    /// so a lighting investigation reads data instead of guessing curves.</summary>
    public static void DumpRaw(string clientDataPath)
    {
        var d = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, MpqPath);
        if (d is null) { Console.WriteLine("[dnc] dump: file not found"); return; }

        int columnCount = BitConverter.ToInt32(d, 0);
        var names = new string[columnCount];
        int stringBlockStart = d.Length;
        int off = 8;
        for (int c = 0; c < columnCount; c++)
        {
            int nameOffset = BitConverter.ToInt32(d, off + 4);
            off += 8;
            stringBlockStart = Math.Min(stringBlockStart, nameOffset);
            int end = Array.IndexOf(d, (byte)0, nameOffset);
            names[c] = System.Text.Encoding.ASCII.GetString(d, nameOffset, end - nameOffset);
        }

        int rowBytes = columnCount * 8;
        int rowCount = Math.Max(0, (stringBlockStart - off) / rowBytes);
        Console.WriteLine($"[dnc] dump: {columnCount} columns x {rowCount} rows");
        Console.WriteLine("[dnc] columns: " + string.Join(", ", names));
        for (int r = 0; r < rowCount; r++)
        {
            var cells = new string[columnCount];
            for (int c = 0; c < columnCount; c++)
                cells[c] = BitConverter.ToSingle(d, off + r * rowBytes + c * 8 + 4).ToString("F3");
            Console.WriteLine($"[dnc] row {r,2}: " + string.Join(" ", cells));
        }
    }

    public float DayIntensityAt(float hours) => Sample(_dayIntensity, hours);
    public float NightIntensityAt(float hours) => Sample(_nightIntensity, hours);

    /// <summary>
    /// The one number 1.12 Parity multiplies onto the Light.dbc band-0 diffuse.
    ///
    /// The real client runs the sun and the moon as TWO lights (DayIntensity on
    /// one, NightIntensity on the other). We have a single directional light,
    /// and band 0 already colour-shifts to the blue moonlight palette at night,
    /// so the two intensities collapse to max(): 0.8 through the day, 1.0 deep
    /// at night, dipping through the dawn/dusk crossover where both curves are
    /// mid-ramp. Documented as an approximation, not the client's exact math.
    /// </summary>
    public float SunIntensityAt(float hours)
        => MathF.Max(DayIntensityAt(hours), NightIntensityAt(hours));

    /// <summary>Linear interpolation over the hourly rows, wrapping midnight.</summary>
    private float Sample(float[] values, float hours)
    {
        int n = _hours.Length;
        if (n == 0 || values.Length != n) return 1f;
        if (n == 1) return values[0];

        hours %= 24f;
        if (hours < 0f) hours += 24f;

        // Rows are stored 0..23 in order. The wrap segment runs last row -> first.
        if (hours < _hours[0] || hours >= _hours[n - 1])
        {
            float from = _hours[n - 1];
            float to = _hours[0] + 24f;
            float at = hours < _hours[0] ? hours + 24f : hours;
            float span = to - from;
            float t = span <= 0f ? 0f : (at - from) / span;
            return values[n - 1] + (values[0] - values[n - 1]) * t;
        }

        for (int i = 0; i < n - 1; i++)
        {
            if (hours >= _hours[i] && hours < _hours[i + 1])
            {
                float span = _hours[i + 1] - _hours[i];
                float t = span <= 0f ? 0f : (hours - _hours[i]) / span;
                return values[i] + (values[i + 1] - values[i]) * t;
            }
        }
        return values[n - 1];
    }
}
