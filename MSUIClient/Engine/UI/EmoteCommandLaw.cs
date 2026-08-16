namespace MSUIClient.Engine.UI;

/// <summary>
/// The vanilla text-emote command table (169 emotes, ~225 slash aliases),
/// built from two real 1.12.1 client sources via tools/mpqpeek against
/// patch.MPQ - not recalled, read:
///
///   - Interface\FrameXML\GlobalStrings.lua's EMOTE&lt;N&gt;_CMD*/_TOKEN globals
///     give every slash alias and its TOKEN name (e.g. "/wave" -&gt; "WAVE").
///   - DBFilesClient\EmotesText.dbc gives the real (id, InternalName) rows.
///
/// The numeric key here is the DBC id, NOT GlobalStrings.lua's EMOTE&lt;N&gt; loop
/// index - those two numberings are NOT the same thing, and using the Lua
/// index as the wire textEmoteId was the original bug: GlobalStrings.lua
/// carries a 170th entry (EMOTE27_TOKEN = "UNUSED") that has no EmotesText.dbc
/// row at all, so every token after it was off by one relative to the real id.
/// Every id below is matched by TOKEN string against the real DBC row instead,
/// which is what the client binary's own DoEmote() does internally (Lua only
/// ever carries the token, never a numbering it invented itself).
///
/// EmotesText.dbc's id IS the wire textEmoteId a CMSG_TEXT_EMOTE packet
/// carries; confirmed against the server side too (VMaNGOS
/// WorldSession::HandleTextEmoteOpcode does sEmotesTextStore.LookupEntry
/// (packet.textEmote) and silently no-ops if that misses - which is exactly
/// how the Lua-index version failed silent for every single emote).
///
/// The token is kept here for reference/debugging - EmotesTextLaw looks the
/// emote up by this same numeric id, not by the token, at runtime. The reply
/// to a sent emote is SMSG_TEXT_EMOTE (0x0105, see GameLoop.Chat.cs's
/// HandleTextEmoteReceive); VMaNGOS sends only raw ids on that packet and
/// never resolves the flavour text itself (confirmed against its own
/// EmotesTextEntry struct), so EmotesTextLaw does that resolution client-side
/// - see its own doc comment for the full derivation.
///
/// Playing the animation (on other clients, or seeing it locally) is a
/// separate, still-unwired gap: that rides SMSG_EMOTE (0x0103, wire
/// u32 emoteId + u64 unitGuid, confirmed via VMaNGOS's Unit::HandleEmoteCommand
/// / WorldPackets::Misc::EmoteNotify), whose emoteId is an Emotes.dbc reference
/// - a different id space again from both EmoteCommandLaw's and
/// EmotesTextLaw's, and not yet read out of the client archives at all.
/// </summary>
public static class EmoteCommandLaw
{
    // id (EmotesText.dbc row id - the real wire value) -> (token, every slash alias for it)
    private static readonly Dictionary<int, (string Token, string[] Aliases)> ById = new()
    {
        [1] = ("AGREE", new[] { "/agree" }),
        [2] = ("AMAZE", new[] { "/amaze" }),
        [3] = ("ANGRY", new[] { "/angry", "/mad" }),
        [4] = ("APOLOGIZE", new[] { "/apologize", "/sorry" }),
        [5] = ("APPLAUD", new[] { "/applaud", "/applause", "/bravo" }),
        [6] = ("BASHFUL", new[] { "/bashful" }),
        [7] = ("BECKON", new[] { "/beckon" }),
        [8] = ("BEG", new[] { "/beg" }),
        [9] = ("BITE", new[] { "/bite" }),
        [10] = ("BLEED", new[] { "/bleed", "/blood" }),
        [11] = ("BLINK", new[] { "/blink" }),
        [12] = ("BLUSH", new[] { "/blush" }),
        [13] = ("BONK", new[] { "/bonk", "/doh" }),
        [14] = ("BORED", new[] { "/bored" }),
        [15] = ("BOUNCE", new[] { "/bounce" }),
        [16] = ("BRB", new[] { "/brb" }),
        [17] = ("BOW", new[] { "/bow" }),
        [18] = ("BURP", new[] { "/belch", "/burp" }),
        [19] = ("BYE", new[] { "/bye", "/farewell", "/goodbye" }),
        [20] = ("CACKLE", new[] { "/cackle" }),
        [21] = ("CHEER", new[] { "/cheer" }),
        [22] = ("CHICKEN", new[] { "/chicken", "/flap", "/strut" }),
        [23] = ("CHUCKLE", new[] { "/chuckle" }),
        [24] = ("CLAP", new[] { "/clap" }),
        [25] = ("CONFUSED", new[] { "/confused" }),
        [26] = ("CONGRATULATE", new[] { "/cong", "/congrats", "/congratulate" }),
        [27] = ("COUGH", new[] { "/cough" }),
        [28] = ("COWER", new[] { "/cower", "/fear" }),
        [29] = ("CRACK", new[] { "/crack", "/knuckles" }),
        [30] = ("CRINGE", new[] { "/cringe" }),
        [31] = ("CRY", new[] { "/cry", "/sob", "/weep" }),
        [32] = ("CURIOUS", new[] { "/curious" }),
        [33] = ("CURTSEY", new[] { "/curtsey" }),
        [34] = ("DANCE", new[] { "/dance" }),
        [35] = ("DRINK", new[] { "/drink", "/shindig" }),
        [36] = ("DROOL", new[] { "/drool" }),
        [37] = ("EAT", new[] { "/chew", "/eat", "/feast" }),
        [38] = ("EYE", new[] { "/eye" }),
        [39] = ("FART", new[] { "/fart" }),
        [40] = ("FIDGET", new[] { "/fidget", "/impatient" }),
        [41] = ("FLEX", new[] { "/flex", "/strong" }),
        [42] = ("FROWN", new[] { "/disappointed", "/disappointment", "/frown" }),
        [43] = ("GASP", new[] { "/gasp" }),
        [44] = ("GAZE", new[] { "/gaze" }),
        [45] = ("GIGGLE", new[] { "/giggle" }),
        [46] = ("GLARE", new[] { "/glare" }),
        [47] = ("GLOAT", new[] { "/gloat" }),
        [48] = ("GREET", new[] { "/greet", "/greetings" }),
        [49] = ("GRIN", new[] { "/grin", "/wicked", "/wickedly" }),
        [50] = ("GROAN", new[] { "/groan" }),
        [51] = ("GROVEL", new[] { "/grovel", "/peon" }),
        [52] = ("GUFFAW", new[] { "/guffaw" }),
        [53] = ("HAIL", new[] { "/hail" }),
        [54] = ("HAPPY", new[] { "/glad", "/happy", "/yay" }),
        [55] = ("HELLO", new[] { "/hello", "/hi" }),
        [56] = ("HUG", new[] { "/hug" }),
        [57] = ("HUNGRY", new[] { "/food", "/hungry", "/pizza" }),
        [58] = ("KISS", new[] { "/blow", "/kiss" }),
        [59] = ("KNEEL", new[] { "/kneel" }),
        [60] = ("LAUGH", new[] { "/laugh", "/lol" }),
        [61] = ("LAYDOWN", new[] { "/lay", "/laydown", "/lie", "/liedown" }),
        [62] = ("MASSAGE", new[] { "/massage" }),
        [63] = ("MOAN", new[] { "/moan" }),
        [64] = ("MOON", new[] { "/moon" }),
        [65] = ("MOURN", new[] { "/mourn" }),
        [66] = ("NO", new[] { "/no" }),
        [67] = ("NOD", new[] { "/nod", "/yes" }),
        [68] = ("NOSEPICK", new[] { "/nosepick", "/pick" }),
        [69] = ("PANIC", new[] { "/panic" }),
        [70] = ("PEER", new[] { "/peer" }),
        [71] = ("PLEAD", new[] { "/plead" }),
        [72] = ("POINT", new[] { "/point" }),
        [73] = ("POKE", new[] { "/poke" }),
        [74] = ("PRAY", new[] { "/pray" }),
        [75] = ("ROAR", new[] { "/roar" }),
        [76] = ("ROFL", new[] { "/rofl" }),
        [77] = ("RUDE", new[] { "/rude" }),
        [78] = ("SALUTE", new[] { "/salute" }),
        [79] = ("SCRATCH", new[] { "/cat", "/catty", "/scratch" }),
        [80] = ("SEXY", new[] { "/sexy" }),
        [81] = ("SHAKE", new[] { "/rear", "/shake" }),
        [82] = ("SHOUT", new[] { "/shout" }),
        [83] = ("SHRUG", new[] { "/shrug" }),
        [84] = ("SHY", new[] { "/shy" }),
        [85] = ("SIGH", new[] { "/sigh" }),
        [86] = ("SIT", new[] { "/sit" }),
        [87] = ("SLEEP", new[] { "/sleep" }),
        [88] = ("SNARL", new[] { "/snarl" }),
        [89] = ("SPIT", new[] { "/spit" }),
        [90] = ("STARE", new[] { "/stare" }),
        [91] = ("SURPRISED", new[] { "/surprised" }),
        [92] = ("SURRENDER", new[] { "/surrender" }),
        [93] = ("TALK", new[] { "/talk" }),
        [94] = ("TALKEX", new[] { "/excited", "/talkex" }),
        [95] = ("TALKQ", new[] { "/question", "/talkq" }),
        [96] = ("TAP", new[] { "/tap" }),
        [97] = ("THANK", new[] { "/thank", "/thanks", "/ty" }),
        [98] = ("THREATEN", new[] { "/doom", "/threat", "/threaten", "/wrath" }),
        [99] = ("TIRED", new[] { "/tired" }),
        [100] = ("VICTORY", new[] { "/victory" }),
        [101] = ("WAVE", new[] { "/wave" }),
        [102] = ("WELCOME", new[] { "/welcome" }),
        [103] = ("WHINE", new[] { "/whine" }),
        [104] = ("WHISTLE", new[] { "/whistle" }),
        [105] = ("WORK", new[] { "/work" }),
        [106] = ("YAWN", new[] { "/yawn" }),
        [107] = ("BOGGLE", new[] { "/boggle" }),
        [108] = ("CALM", new[] { "/calm" }),
        [109] = ("COLD", new[] { "/cold" }),
        [110] = ("COMFORT", new[] { "/comfort" }),
        [111] = ("CUDDLE", new[] { "/cuddle", "/spoon" }),
        [112] = ("DUCK", new[] { "/duck" }),
        [113] = ("INSULT", new[] { "/insult" }),
        [114] = ("INTRODUCE", new[] { "/introduce" }),
        [115] = ("JK", new[] { "/jk" }),
        [116] = ("LICK", new[] { "/lick" }),
        [117] = ("LISTEN", new[] { "/listen" }),
        [118] = ("LOST", new[] { "/lost" }),
        [119] = ("MOCK", new[] { "/mock" }),
        [120] = ("PONDER", new[] { "/ponder" }),
        [121] = ("POUNCE", new[] { "/pounce" }),
        [122] = ("PRAISE", new[] { "/lavish", "/praise" }),
        [123] = ("PURR", new[] { "/purr" }),
        [124] = ("PUZZLE", new[] { "/puzzled" }),
        [125] = ("RAISE", new[] { "/raise", "/volunteer" }),
        [126] = ("READY", new[] { "/rdy", "/ready" }),
        [127] = ("SHIMMY", new[] { "/shimmy" }),
        [128] = ("SHIVER", new[] { "/shiver" }),
        [129] = ("SHOO", new[] { "/pest", "/shoo" }),
        [130] = ("SLAP", new[] { "/slap" }),
        [131] = ("SMIRK", new[] { "/smirk" }),
        [132] = ("SNIFF", new[] { "/sniff" }),
        [133] = ("SNUB", new[] { "/snub" }),
        [134] = ("SOOTHE", new[] { "/soothe" }),
        [135] = ("STINK", new[] { "/smell", "/stink" }),
        [136] = ("TAUNT", new[] { "/taunt" }),
        [137] = ("TEASE", new[] { "/tease" }),
        [138] = ("THIRSTY", new[] { "/thirsty" }),
        [139] = ("VETO", new[] { "/veto" }),
        [140] = ("SNICKER", new[] { "/snicker" }),
        [141] = ("STAND", new[] { "/stand" }),
        [142] = ("TICKLE", new[] { "/tickle" }),
        [143] = ("VIOLIN", new[] { "/violin" }),
        [163] = ("SMILE", new[] { "/smile" }),
        [183] = ("RASP", new[] { "/rasp" }),
        [203] = ("PITY", new[] { "/pity" }),
        [204] = ("GROWL", new[] { "/growl" }),
        [205] = ("BARK", new[] { "/bark" }),
        [223] = ("SCARED", new[] { "/scared" }),
        [224] = ("FLOP", new[] { "/flop" }),
        [225] = ("LOVE", new[] { "/love" }),
        [226] = ("MOO", new[] { "/moo" }),
        [243] = ("COMMEND", new[] { "/commend" }),
        [264] = ("TRAIN", new[] { "/train" }),
        [303] = ("HELPME", new[] { "/helpme" }),
        [304] = ("INCOMING", new[] { "/incoming" }),
        [305] = ("CHARGE", new[] { "/charge" }),
        [306] = ("FLEE", new[] { "/flee" }),
        [307] = ("ATTACKMYTARGET", new[] { "/attacktarget" }),
        [323] = ("OOM", new[] { "/oom" }),
        [324] = ("FOLLOW", new[] { "/followme" }),
        [325] = ("WAIT", new[] { "/wait" }),
        [326] = ("HEALME", new[] { "/healme" }),
        [327] = ("OPENFIRE", new[] { "/openfire" }),
        [328] = ("FLIRT", new[] { "/flirt" }),
        [329] = ("JOKE", new[] { "/silly" }),
        [343] = ("GOLFCLAP", new[] { "/golfclap" }),
        [363] = ("WINK", new[] { "/wink" }),
        [364] = ("PAT", new[] { "/pat" }),
    };

    private static readonly Dictionary<string, int> AliasToId = ById
        .SelectMany(kv => kv.Value.Aliases.Select(alias => (Alias: alias, Id: kv.Key)))
        .ToDictionary(p => p.Alias, p => p.Id);

    /// <summary>A leading-slash command, already lowercased (e.g. "/wave") - its
    /// wire textEmoteId, or null if it isn't one of the 169 known text emotes.</summary>
    public static int? Resolve(string command) =>
        AliasToId.TryGetValue(command, out int id) ? id : null;
}
