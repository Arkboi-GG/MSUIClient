using System.Numerics;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Sound;

namespace MSUIClient;

/// <summary>Benilla's two NPC vocal paths: left-click cycling and interaction-window transitions.</summary>
public sealed partial class GameLoop
{
    private NpcGreetingCatalog? _npcGreetings;
    private readonly Dictionary<ulong, long> _npcGreetingVoices = [];
    private ulong _npcGreetingSequenceGuid;
    private int _npcGreetingSequence;
    private ulong _activeInteractionNpc;

    /// <summary>
    /// Selection greeting request. Called before CommitSelection, matching the
    /// client gesture path; repeated clicks on the already-selected NPC still count.
    /// </summary>
    private void RequestNpcSelectionGreeting(ulong guid)
    {
        if (!AudioFeaturePolicy.ExpandedWorldAudioEnabled) return;
        if (!TryResolveNpcGreeting(guid, out WorldEntity npc, out NpcGreeting greeting)) return;
        if (_npcGreetingSequenceGuid != guid)
        {
            _npcGreetingSequenceGuid = guid;
            _npcGreetingSequence = 0;
        }
        if (NpcGreetingVoiceLive(guid)) return;

        int pissedVariations = 0;
        if (greeting.Pissed != 0 &&
            _spellSounds?.TryGetEntry(greeting.Pissed, out SoundEntry pissed) == true)
            pissedVariations = pissed.Variants.Count;
        NpcSelectVocal line = NpcGreetingLaw.SelectLine(_npcGreetingSequence,
            pissedVariations);
        uint kit = line.Kind == NpcSelectVocalKind.Hello
            ? greeting.Hello : greeting.Pissed;
        long voice = PlayNpcGreetingLine(npc, kit, line.Variation);
        // The latch precedes the counter: no authored/live voice means no advance.
        if (voice != 0 && _spellSounds?.IsLive(voice) == true)
            _npcGreetingSequence = line.NextSequence;
    }

    /// <summary>
    /// Diff the union after every panel lifecycle update. A swap speaks only B's
    /// hello; a real close-to-nothing speaks A's goodbye.
    /// </summary>
    private void UpdateNpcGreetingLifecycle()
    {
        if (!AudioFeaturePolicy.ExpandedWorldAudioEnabled)
        {
            ResetNpcGreetingSoundState();
            return;
        }
        ReapNpcGreetingVoices();
        ulong active = ActiveInteractionGuid();
        NpcWindowVocal vocal = NpcGreetingLaw.WindowTransition(_activeInteractionNpc, active);
        _activeInteractionNpc = active;
        if (vocal.Kind == NpcWindowVocalKind.None ||
            !TryResolveNpcGreeting(vocal.Guid, out WorldEntity npc, out NpcGreeting greeting))
            return;
        uint kit = vocal.Kind == NpcWindowVocalKind.Hello
            ? greeting.Hello : greeting.Goodbye;
        PlayNpcGreetingLine(npc, kit, variation: null);
    }

    private ulong ActiveInteractionGuid()
    {
        // Creature windows plus non-creature/player interaction surfaces. The latter
        // cannot resolve a greeting, but keeping them in the SetActiveNPC union
        // suppresses a displaced creature's goodbye exactly like the original client.
        if (_vendor is not null) return _vendor.VendorGuid;
        if (_gossipMenu is not null) return _gossipMenu.SourceGuid;
        ulong quest = QuestGiverGuid();
        if (quest != 0) return quest;
        if (_trainer is not null) return _trainer.TrainerGuid;
        if (_bankOpen) return _bankSource;
        if (_taxiOpen) return _taxiMasterGuid;
        if (_auctionOpen) return _auctioneerGuid;
        if (_tabardOpen) return _tabardVendorGuid;
        if (_binderConfirmOpen) return _binderGuid;
        if (_mailOpen) return _mailboxGuid;
        if (_tradeOpen) return _tradePartnerGuid;
        return 0;
    }

    private bool TryResolveNpcGreeting(ulong guid, out WorldEntity npc,
        out NpcGreeting greeting)
    {
        npc = null!;
        greeting = default;
        return guid != 0 && _npcGreetings is not null &&
               _entities.TryGet(guid, out npc) && npc.IsCreature && !npc.IsDead &&
               npc.DisplayId > 0 && _npcGreetings.TryGet((uint)npc.DisplayId, out greeting);
    }

    private bool NpcGreetingVoiceLive(ulong guid)
    {
        if (!_npcGreetingVoices.TryGetValue(guid, out long voice)) return false;
        if (_spellSounds?.IsLive(voice) == true) return true;
        _npcGreetingVoices.Remove(guid);
        return false;
    }

    private long PlayNpcGreetingLine(WorldEntity npc, uint kit, int? variation)
    {
        if (kit == 0 || !_soundscapePlaybackArmed || _spellSounds is null ||
            NpcGreetingVoiceLive(npc.Guid)) return 0;
        Vector3 listener = _controller?.Position ?? Vector3.Zero;
        long voice = variation is int exact
            ? _spellSounds.PlayVariant(kit, exact, npc.Guid, npc.Position, listener,
                forceLoop: false, trackHold: false, category: "sfx")
            : _spellSounds.Play(kit, npc.Guid, npc.Position, listener,
                forceLoop: false, trackHold: false, category: "sfx");
        if (voice != 0) _npcGreetingVoices[npc.Guid] = voice;
        return voice;
    }

    private void ReapNpcGreetingVoices()
    {
        foreach ((ulong guid, long voice) in _npcGreetingVoices.ToArray())
        {
            if (!_entities.TryGet(guid, out WorldEntity npc) || !npc.IsCreature)
            {
                _spellSounds?.Stop(voice);
                _npcGreetingVoices.Remove(guid);
            }
            else if (_spellSounds?.IsLive(voice) != true)
            {
                _npcGreetingVoices.Remove(guid);
            }
        }
    }

    private void ResetNpcGreetingSoundState()
    {
        foreach (long voice in _npcGreetingVoices.Values) _spellSounds?.Stop(voice);
        _npcGreetingVoices.Clear();
        _npcGreetingSequenceGuid = 0;
        _npcGreetingSequence = 0;
        _activeInteractionNpc = 0;
    }
}
