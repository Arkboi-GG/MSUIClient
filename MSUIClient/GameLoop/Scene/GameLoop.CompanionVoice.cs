using System.Numerics;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

/// <summary>
/// Warcraft-style spoken command feedback: the companion a gesture addresses
/// answers in its own 1.12 race/gender voice — hello when picked, yes on an
/// order, charge or open fire on an attack, no on a refusal — and a companion
/// selected once too often runs through its race's pissed lines exactly like a
/// vanilla NPC. This is interface voice, not a world emitter: it plays flat at
/// the effects volume so an acknowledgement is never distance-culled.
/// </summary>
public sealed partial class GameLoop
{
    private double _companionVoiceLastAt;
    private long _companionVoiceHandle;
    private ulong _companionSelectGuid;
    private int _companionSelectSequence;
    // The tail of an order's chorus: extra speakers cascading behind the first.
    private readonly List<(double At, ulong Guid, uint Kit)> _companionVoicePending = [];

    private bool CompanionVoiceReady()
    {
        if (!Settings.Controls.CompanionVoice || SuppressUiAudioForDiagnostics) return false;
        if (_spellSounds is null) return false;
        // One mouth: a line still being spoken is never talked over, and lines
        // keep a minimum spacing — a skipped line is dropped, never queued.
        if (_spellSounds.IsLive(_companionVoiceHandle)) return false;
        return NowSeconds() - _companionVoiceLastAt >= CompanionVoiceLaw.MinSecondsBetweenLines;
    }

    /// <summary>
    /// The ordered set acknowledges aloud: one voice for a handful, a cascading
    /// chorus of two or three distinct voices for a squad or an army.
    /// </summary>
    private void PlayCompanionOrderVoice(byte orderType, IReadOnlyList<ulong> subjects)
    {
        if (!CompanionVoiceReady()) return;
        var speakers = PickCompanionChorus(subjects);
        double now = NowSeconds();
        double at = now;
        foreach ((ulong guid, var traits) in speakers)
        {
            uint emote = CompanionVoiceLaw.OrderEmote(orderType, traits.Class);
            if (emote == 0 || _emoteTextSounds?.TryGet(emote, traits.Race, traits.Gender,
                    out uint kit) != true) continue;
            if (at <= now)
                PlayCompanionVoiceKit(guid, kit, variation: null);
            else
                _companionVoicePending.Add((at, guid, kit));
            at += CompanionVoiceLaw.ChorusSpacingSeconds;
        }
    }

    /// <summary>Cascade the chorus tail; call once per frame.</summary>
    private void UpdateCompanionVoicePending()
    {
        if (_companionVoicePending.Count == 0) return;
        if (!Settings.Controls.CompanionVoice || _spellSounds is null)
        {
            _companionVoicePending.Clear();
            return;
        }
        double now = NowSeconds();
        for (int i = _companionVoicePending.Count - 1; i >= 0; i--)
        {
            (double at, ulong guid, uint kit) = _companionVoicePending[i];
            if (now < at) continue;
            _companionVoicePending.RemoveAt(i);
            PlayCompanionVoiceKit(guid, kit, variation: null);
        }
    }

    /// <summary>
    /// Chorus roster for one order: shuffled so a standing army does not always
    /// answer with the same soldier, then picked to maximize distinct race/gender
    /// voices so the cascade sounds like different people, not an echo.
    /// </summary>
    private List<(ulong Guid, (byte Race, byte Class, byte Gender, byte PowerType) Traits)>
        PickCompanionChorus(IReadOnlyList<ulong> subjects)
    {
        var picked = new List<(ulong, (byte, byte, byte, byte))>();
        var candidates = new List<(ulong Guid, (byte, byte, byte, byte) Traits)>();
        foreach (ulong guid in subjects)
            if (guid != ControlledGuid && TryCompanionTraits(guid, out var traits))
                candidates.Add((guid, traits));
        if (candidates.Count == 0)
        {
            // Solo free view: the own body is the whole selection and may answer.
            foreach (ulong guid in subjects)
                if (TryCompanionTraits(guid, out var traits))
                {
                    picked.Add((guid, traits));
                    break;
                }
            return picked;
        }
        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }
        int want = CompanionVoiceLaw.ChorusSize(subjects.Count);
        var voicesHeard = new HashSet<(byte Race, byte Gender)>();
        foreach (var candidate in candidates)
            if (picked.Count < want &&
                voicesHeard.Add((candidate.Traits.Item1, candidate.Traits.Item3)))
                picked.Add(candidate);
        foreach (var candidate in candidates)
            if (picked.Count < want && !picked.Contains(candidate))
                picked.Add(candidate);
        return picked;
    }

    /// <summary>A specific unit speaks a specific vocal (refusals, the driven body's own line).</summary>
    private void PlayCompanionEmoteVoice(ulong guid, uint emote)
    {
        if (!CompanionVoiceReady()) return;
        if (!TryCompanionTraits(guid, out var traits)) return;
        PlayCompanionEmoteKit(guid, traits, emote);
    }

    /// <summary>
    /// Selection hello, escalating to the race's pissed lines after enough
    /// consecutive picks — the vanilla NPC click cycle applied to your own party.
    /// </summary>
    private void PlayCompanionSelectionVoice(ulong guid)
    {
        if (guid == 0 || guid == ControlledGuid) return;   // picking your own body is not a conversation
        if (_companionSelectGuid != guid)
        {
            _companionSelectGuid = guid;
            _companionSelectSequence = 0;
        }
        if (!CompanionVoiceReady()) return;
        if (!TryCompanionTraits(guid, out var traits)) return;

        int pissedVariations = 0;
        SoundEntry pissedEntry = default;
        if (CompanionVoiceLaw.PissedKitName(traits.Race, traits.Gender) is string pissedName &&
            _soundKits?.TryGet(pissedName, out pissedEntry) == true)
            pissedVariations = pissedEntry.Variants.Count;

        NpcSelectVocal line = NpcGreetingLaw.SelectLine(_companionSelectSequence, pissedVariations);
        long voice = line.Kind == NpcSelectVocalKind.Hello
            ? PlayCompanionEmoteKit(guid, traits, CompanionVoiceLaw.EmoteHello)
            : PlayCompanionVoiceKit(guid, pissedEntry.Id, line.Variation);
        // Same latch as the NPC law: an unheard line does not advance the cycle.
        if (voice != 0 && _spellSounds?.IsLive(voice) == true)
            _companionSelectSequence = line.NextSequence;
    }

    private bool TryCompanionTraits(ulong guid,
        out (byte Race, byte Class, byte Gender, byte PowerType) traits)
    {
        traits = default;
        if (guid == 0 || !_entities.TryGet(guid, out WorldEntity unit) ||
            !unit.IsPlayer || unit.IsDead) return false;
        traits = unit.Fields.Bytes0;
        return traits.Race != 0;
    }

    private long PlayCompanionEmoteKit(ulong guid,
        (byte Race, byte Class, byte Gender, byte PowerType) traits, uint emote)
    {
        if (emote == 0 || _emoteTextSounds?.TryGet(emote, traits.Race, traits.Gender,
                out uint kit) != true) return 0;
        return PlayCompanionVoiceKit(guid, kit, variation: null);
    }

    private long PlayCompanionVoiceKit(ulong guid, uint kit, int? variation)
    {
        if (kit == 0 || _spellSounds is null) return 0;
        Vector3 listener = _controller?.Position ?? Vector3.Zero;
        long voice = variation is int exact
            ? _spellSounds.PlayVariant(kit, exact, guid, listener, listener,
                forceLoop: false, trackHold: false, category: "voice")
            : _spellSounds.Play(kit, guid, listener, listener,
                forceLoop: false, trackHold: false, category: "voice");
        if (voice != 0)
        {
            _companionVoiceHandle = voice;
            _companionVoiceLastAt = NowSeconds();
        }
        return voice;
    }

    private void ResetCompanionVoiceState()
    {
        if (_companionVoiceHandle != 0) _spellSounds?.Stop(_companionVoiceHandle);
        _companionVoiceHandle = 0;
        _companionVoiceLastAt = 0;
        _companionSelectGuid = 0;
        _companionSelectSequence = 0;
        _companionVoicePending.Clear();
    }
}
