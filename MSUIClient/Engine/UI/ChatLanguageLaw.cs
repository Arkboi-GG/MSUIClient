namespace MSUIClient.Engine.UI;

/// <summary>The non-data gates around build-5875's client-side chat language substitution.</summary>
public static class ChatLanguageLaw
{
    public const uint PlayerFlagGameMaster = 0x8;

    public static uint EffectiveLanguage(
        ChatFrameLaw.MsgType type, uint wireLanguage, bool havePlayer, uint playerFlags)
    {
        if (!havePlayer || (playerFlags & PlayerFlagGameMaster) != 0 ||
            type is ChatFrameLaw.MsgType.Emote or ChatFrameLaw.MsgType.System or
                ChatFrameLaw.MsgType.MonsterEmote or ChatFrameLaw.MsgType.RaidBossEmote)
            return 0;
        return wireLanguage;
    }

    public static uint DefaultLanguage(byte race) => race is 2 or 5 or 6 or 8 ? 1u : 7u;

    public static string Name(uint language) => language switch
    {
        1 => "Orcish", 2 => "Darnassian", 3 => "Taurahe", 6 => "Dwarvish",
        7 => "Common", 8 => "Demonic", 9 => "Titan", 10 => "Thalassian",
        11 => "Draconic", 12 => "Kalimag", 13 => "Gnomish", 14 => "Troll",
        33 => "Gutterspeak", _ => "",
    };

    public static string Header(uint effectiveLanguage, uint defaultLanguage)
    {
        string name = Name(effectiveLanguage);
        return name.Length == 0 || effectiveLanguage == defaultLanguage ? "" : $"[{name}] ";
    }
}
