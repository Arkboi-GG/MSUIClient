namespace MSUIClient.Engine.UI;

/// <summary>
/// Emotes.dbc's id -&gt; AnimationData.dbc AnimID, transcribed from the real
/// 1.12.1 DBC (DBFilesClient\Emotes.dbc, read via tools/mpqpeek + a throwaway
/// parser - not recalled; see dumps/Emotes.dbc and dumps/emote-animation-table.cs.txt).
///
/// This is the wire id SMSG_EMOTE (0x0103, u32 emoteId + u64 unitGuid, confirmed
/// via VMaNGOS's Unit::HandleEmoteCommand / WorldPackets::Misc::EmoteNotify)
/// carries - a completely different id space from both EmoteCommandLaw's
/// (EmotesText.dbc, the CMSG/SMSG_TEXT_EMOTE wire id) and EmotesTextLaw's. Do
/// not cross-reference those tables against this one; the server hands us an
/// Emotes.dbc id directly and this is its only consumer.
///
/// The Name column (Emotes.dbc's own EmoteSlashCommand string, e.g.
/// "ONESHOT_WAVE(DNR)") is kept for reference/debugging only, same as
/// EmoteCommandLaw's Token - lookups are by id. Rows whose AnimID is 0
/// (STATE_SLEEP, STATE_SIT, STATE_STAND, STATE_NONE, STATE_DEAD, STATE_KNEEL,
/// STATE_AT_EASE) are persistent stand-state changes, not one-shots; they ride
/// SMSG_STANDSTATE_UPDATE in the real client, not this packet, but are kept
/// here for completeness since they are real DBC rows. Resolve() returns them
/// as-is (AnimID 0 = Stand) - callers that only want meaningful one-shots
/// should skip an AnimID of 0.
///
/// Confirmed against the Benilla reference trace (docs/current/spells/
/// BENILLA_SPELL_SYSTEM_TRACE.md lines 4085-4089): the real client resolves
/// SMSG_EMOTE's id through exactly this DBC to get a one-shot AnimationData id
/// played over the current gait, gated off only at stand-state SLEEP or while
/// swimming.
/// </summary>
public static class EmoteAnimationLaw
{
    // Emotes.dbc id (SMSG_EMOTE's wire emoteId) -> (AnimationData.dbc AnimID, EmoteSlashCommand name)
    private static readonly Dictionary<int, (int AnimId, string Name)> ById = new()
    {
        [0] = (0, "ONESHOT_NONE"),
        [1] = (60, "ONESHOT_TALK(DNR)"),
        [2] = (66, "ONESHOT_BOW"),
        [3] = (67, "ONESHOT_WAVE(DNR)"),
        [4] = (68, "ONESHOT_CHEER(DNR)"),
        [5] = (64, "ONESHOT_EXCLAMATION(DNR)"),
        [6] = (65, "ONESHOT_QUESTION"),
        [7] = (61, "ONESHOT_EAT"),
        [10] = (69, "STATE_DANCE"),
        [11] = (70, "ONESHOT_LAUGH"),
        [12] = (0, "STATE_SLEEP"),
        [13] = (0, "STATE_SIT"),
        [14] = (73, "ONESHOT_RUDE(DNR)"),
        [15] = (74, "ONESHOT_ROAR(DNR)"),
        [16] = (75, "ONESHOT_KNEEL"),
        [17] = (76, "ONESHOT_KISS"),
        [18] = (77, "ONESHOT_CRY"),
        [19] = (78, "ONESHOT_CHICKEN"),
        [20] = (79, "ONESHOT_BEG"),
        [21] = (80, "ONESHOT_APPLAUD"),
        [22] = (81, "ONESHOT_SHOUT(DNR)"),
        [23] = (82, "ONESHOT_FLEX"),
        [24] = (83, "ONESHOT_SHY(DNR)"),
        [25] = (84, "ONESHOT_POINT(DNR)"),
        [26] = (0, "STATE_STAND"),
        [27] = (25, "STATE_READYUNARMED"),
        [28] = (62, "STATE_WORK"),
        [29] = (84, "STATE_POINT(DNR)"),
        [30] = (0, "STATE_NONE"),
        [33] = (9, "ONESHOT_WOUND"),
        [34] = (10, "ONESHOT_WOUNDCRITICAL"),
        [35] = (16, "ONESHOT_ATTACKUNARMED"),
        [36] = (17, "ONESHOT_ATTACK1H"),
        [37] = (18, "ONESHOT_ATTACK2HTIGHT"),
        [38] = (19, "ONESHOT_ATTACK2HLOOSE"),
        [39] = (20, "ONESHOT_PARRYUNARMED"),
        [43] = (24, "ONESHOT_PARRYSHIELD"),
        [44] = (25, "ONESHOT_READYUNARMED"),
        [45] = (26, "ONESHOT_READY1H"),
        [48] = (29, "ONESHOT_READYBOW"),
        [50] = (31, "ONESHOT_SPELLPRECAST"),
        [51] = (32, "ONESHOT_SPELLCAST"),
        [53] = (55, "ONESHOT_BATTLEROAR"),
        [54] = (57, "ONESHOT_SPECIALATTACK1H"),
        [60] = (95, "ONESHOT_KICK"),
        [61] = (107, "ONESHOT_ATTACKTHROWN"),
        [64] = (14, "STATE_STUN"),
        [65] = (0, "STATE_DEAD"),
        [66] = (113, "ONESHOT_SALUTE"),
        [68] = (0, "STATE_KNEEL"),
        [69] = (63, "STATE_USESTANDING"),
        [70] = (67, "ONESHOT_WAVE_NOSHEATHE"),
        [71] = (68, "ONESHOT_CHEER_NOSHEATHE"),
        [92] = (199, "ONESHOT_EAT_NOSHEATHE"),
        [93] = (137, "STATE_STUN_NOSHEATHE"),
        [94] = (69, "ONESHOT_DANCE"),
        [113] = (113, "ONESHOT_SALUTE_NOSHEATH"),
        [133] = (138, "STATE_USESTANDING_NOSHEATHE"),
        [153] = (70, "ONESHOT_LAUGH_NOSHEATHE"),
        [173] = (136, "STATE_WORK_NOSHEATHE"),
        [193] = (31, "STATE_SPELLPRECAST"),
        [213] = (48, "ONESHOT_READYRIFLE"),
        [214] = (48, "STATE_READYRIFLE"),
        [233] = (136, "STATE_WORK_NOSHEATHE_MINING"),
        [234] = (136, "STATE_WORK_NOSHEATHE_CHOPWOOD"),
        [253] = (192, "zzOLDONESHOT_LIFTOFF"),
        [254] = (192, "ONESHOT_LIFTOFF"),
        [273] = (185, "ONESHOT_YES(DNR)"),
        [274] = (186, "ONESHOT_NO(DNR)"),
        [275] = (195, "ONESHOT_TRAIN(DNR)"),
        [293] = (200, "ONESHOT_LAND"),
        [313] = (0, "STATE_AT_EASE"),
        [333] = (26, "STATE_READY1H"),
        [353] = (140, "STATE_SPELLKNEELSTART"),
        [373] = (202, "STATE_SUBMERGED"),
        [374] = (201, "ONESHOT_SUBMERGE"),
        [375] = (27, "STATE_READY2H"),
        [376] = (29, "STATE_READYBOW"),
    };

    /// <summary>SMSG_EMOTE's wire emoteId -&gt; the AnimationData.dbc id to play as a
    /// one-shot over the unit's current gait, or null if the id isn't a known
    /// Emotes.dbc row.</summary>
    public static int? Resolve(int emoteId) =>
        ById.TryGetValue(emoteId, out var row) ? row.AnimId : null;
}
