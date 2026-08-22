using MSUIClient;
using MSUIClient.Formats;

internal static class TextEmoteVoiceClinicalChecks
{
    public static void Run()
    {
        EmoteTextSoundCatalog catalog = EmoteTextSoundCatalog.FromRows(
            (101, 1, 0, 1001), (101, 1, 1, 1002), (101, 2, 0, 2001));
        Check(catalog.Count == 3 && catalog.TryGet(101, 1, 0, out uint humanMale) &&
              humanMale == 1001 && catalog.TryGet(101, 1, 1, out uint humanFemale) &&
              humanFemale == 1002 && catalog.TryGet(101, 2, 0, out uint orcMale) &&
              orcMale == 2001 && !catalog.TryGet(101, 2, 1, out _),
            "EmotesTextSound text/race/sex lookup drift");

        string root = ClientConfig.FindRepoRoot();
        string net = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string chat = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Chat.cs"));
        Check(net.Contains("_emoteTextSounds = EmoteTextSoundCatalog.Load(_mpq);",
                  StringComparison.Ordinal),
            "EmotesTextSound catalog load drift");
        Check(chat.Contains("_soundscapePlaybackArmed && emoter is not null",
                  StringComparison.Ordinal) &&
              chat.Contains("traits.Race != 0 && _emoteTextSounds?.TryGet(",
                  StringComparison.Ordinal) &&
              chat.Contains("textEmote, traits.Race, traits.Gender, out uint voiceKit",
                  StringComparison.Ordinal) &&
              chat.Contains("category: \"sfx\"", StringComparison.Ordinal),
            "SMSG_TEXT_EMOTE descriptor voice routing or loading-hold gate drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
