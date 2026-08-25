// Companion voice clinical check: proves the whole acknowledgement chain against
// the live archives — every player race/gender resolves every vocal the feature
// speaks (hello/yes/no/charge/open fire/follow me) through EmotesTextSound.dbc,
// every pissed kit the law names exists in SoundEntries.dbc, and every referenced
// voice file is readable by the client's own MPQ reader as a sane RIFF/WAVE.
using MSUIClient;
using MSUIClient.Formats;
using MSUIClient.Net;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}

// ── Pure law ──────────────────────────────────────────────────────────────────
Check(CompanionVoiceLaw.OrderEmote(0, 1) == CompanionVoiceLaw.EmoteYes &&
      CompanionVoiceLaw.OrderEmote(2, 1) == CompanionVoiceLaw.EmoteYes &&
      CompanionVoiceLaw.OrderEmote(3, 1) == CompanionVoiceLaw.EmoteYes &&
      CompanionVoiceLaw.OrderEmote(4, 1) == CompanionVoiceLaw.EmoteYes,
    "move/hold/waypoint/patrol must all acknowledge with the Yes vocal");
Check(CompanionVoiceLaw.OrderEmote(1, 1) == CompanionVoiceLaw.EmoteCharge &&
      CompanionVoiceLaw.OrderEmote(1, 4) == CompanionVoiceLaw.EmoteCharge &&
      CompanionVoiceLaw.OrderEmote(1, 3) == CompanionVoiceLaw.EmoteOpenFire &&
      CompanionVoiceLaw.OrderEmote(1, 8) == CompanionVoiceLaw.EmoteOpenFire,
    "attack must split charge (melee) from open fire (ranged)");
Check(CompanionVoiceLaw.OrderEmote(6, 1) == 0 && CompanionVoiceLaw.OrderEmote(7, 1) == 0,
    "link and auto-group are meta orders and must stay silent in OrderEmote");
Check(CompanionVoiceLaw.PissedKitName(0, 0) is null && CompanionVoiceLaw.PissedKitName(9, 1) is null,
    "unknown races must have no pissed kit");

// ── Live archives ─────────────────────────────────────────────────────────────
string dataRoot = Path.Combine(ClientConfig.FindRepoRoot(), "GameData", "Data");
using var mount = new MpqMount(dataRoot);
EmoteTextSoundCatalog emoteVoices = EmoteTextSoundCatalog.Load(mount)
    ?? throw new InvalidOperationException("EmotesTextSound.dbc failed to load");
SoundEntriesCatalog sounds = SoundEntriesCatalog.Load(mount)
    ?? throw new InvalidOperationException("SoundEntries.dbc failed to load");

int filesChecked = 0;
void CheckKitPlayable(uint kit, string what)
{
    Check(sounds.TryGet(kit, out SoundEntry entry), $"{what}: kit {kit} missing from SoundEntries");
    Check(entry.Variants.Count > 0, $"{what}: kit {kit} ({entry.Name}) has no variants");
    foreach (SoundVariant variant in entry.Variants)
    {
        byte[]? bytes = mount.ReadFile(variant.Path);
        Check(bytes is not null, $"{what}: '{variant.Path}' is not readable from the archives");
        Check(bytes!.Length > 44 &&
              bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' &&
              bytes[3] == (byte)'F' && bytes[8] == (byte)'W' && bytes[9] == (byte)'A',
            $"{what}: '{variant.Path}' is not a RIFF/WAVE payload");
        filesChecked++;
    }
}

uint[] emotes =
[
    CompanionVoiceLaw.EmoteBye, CompanionVoiceLaw.EmoteHello, CompanionVoiceLaw.EmoteNo,
    CompanionVoiceLaw.EmoteYes, CompanionVoiceLaw.EmoteCharge, CompanionVoiceLaw.EmoteFollowMe,
    CompanionVoiceLaw.EmoteOpenFire,
];
for (byte race = 1; race <= 8; race++)
for (byte gender = 0; gender <= 1; gender++)
{
    foreach (uint emote in emotes)
    {
        Check(emoteVoices.TryGet(emote, race, gender, out uint kit),
            $"race {race} gender {gender}: emote {emote} has no EmotesTextSound row");
        CheckKitPlayable(kit, $"race {race} gender {gender} emote {emote}");
    }
    string? pissed = CompanionVoiceLaw.PissedKitName(race, gender);
    Check(pissed is not null, $"race {race} gender {gender}: no pissed kit name");
    Check(sounds.TryGet(pissed!, out SoundEntry pissedEntry),
        $"race {race} gender {gender}: pissed kit '{pissed}' missing from SoundEntries");
    CheckKitPlayable(pissedEntry.Id, $"race {race} gender {gender} pissed '{pissed}'");
}

Console.WriteLine($"Companion voice checks passed ({filesChecked} voice files verified " +
    "across 16 race/gender combinations).");
