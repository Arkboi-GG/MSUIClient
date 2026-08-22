namespace MSUIClient.Engine.UI;

/// <summary>
/// Benilla/1.12 posture law. These are the only client-volunteered stand states accepted by
/// VMaNGOS, and posture emotes must route through CMSG_STANDSTATECHANGE rather than the ordinary
/// CMSG_TEXT_EMOTE path (the server deliberately does not apply those state emotes for us).
/// </summary>
public static class StandStateUiLaw
{
    public const byte Stand = 0;
    public const byte Sit = 1;
    public const byte Sleep = 3;
    public const byte Kneel = 8;

    public static byte? ResolveCommand(string command) => command.ToLowerInvariant() switch
    {
        "/stand" => Stand,
        "/sit" => Sit,
        "/sleep" or "/lay" => Sleep,
        "/kneel" => Kneel,
        _ => null,
    };

    public static bool IsClientState(uint state) => state is Stand or Sit or Sleep or Kneel;

    /// <summary>The looping pose AnimationData id for a descriptor stand-state.</summary>
    public static int LoopAnimation(byte state) => state switch
    {
        Sit => 97,
        Sleep => 100,
        4 => 102, // chair low
        5 => 103, // chair medium
        6 => 104, // chair high
        Kneel => 115,
        _ => 0,
    };
}
