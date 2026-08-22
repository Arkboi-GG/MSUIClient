using MSUIClient;
using MSUIClient.Net;

internal static class WeatherClinicalChecks
{
    public static void Run()
    {
        Check((ushort)Op.SMSG_WEATHER == 0x02F4, "SMSG_WEATHER opcode drift");
        WeatherPacket packet = WeatherPackets.Parse(
            Convert.FromHexString("010000000000403F5521000001"));
        Check(packet.WeatherType == 1 && Math.Abs(packet.Grade - .75f) < .0001f &&
              packet.SoundId == 8533 && packet.Instant,
            "SMSG_WEATHER type/grade/sound/instant decode drift");
        ExpectInvalid(() => WeatherPackets.Parse(new byte[12]));
        ExpectInvalid(() => WeatherPackets.Parse(new byte[14]));

        string root = ClientConfig.FindRepoRoot();
        string dispatch = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string glue = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Sound.cs"));
        string soundscape = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Sound",
            "WorldSoundscape.cs"));
        Check(dispatch.Contains("case Op.SMSG_WEATHER:", StringComparison.Ordinal) &&
              dispatch.Contains("ApplyWeather(body);", StringComparison.Ordinal),
            "SMSG_WEATHER dispatch drift");
        Check(glue.Contains("_weatherSoundKit = weather.SoundId;", StringComparison.Ordinal) &&
              glue.Contains("_soundscape.WeatherAmbienceKit = _weatherSoundKit;",
                  StringComparison.Ordinal) &&
              glue.Contains("_soundscapeIndoors = true;", StringComparison.Ordinal),
            "weather sound retention or WMO indoor verdict drift");
        Check(soundscape.Contains("if (!Interior && WeatherAmbienceKit != 0) return WeatherAmbienceKit;",
                  StringComparison.Ordinal) &&
              soundscape.Contains("if (Submerged) return UnderwaterLoopKit;",
                  StringComparison.Ordinal) &&
              soundscape.Contains("AmbienceFadeSeconds = 5.0f", StringComparison.Ordinal),
            "weather/underwater/indoor ambience selector or crossfade drift");
    }

    private static void ExpectInvalid(Action action)
    {
        try
        {
            action();
            throw new InvalidDataException("malformed weather body was accepted");
        }
        catch (InvalidDataException) { }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
