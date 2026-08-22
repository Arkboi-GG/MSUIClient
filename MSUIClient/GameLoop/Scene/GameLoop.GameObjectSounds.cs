using System.Numerics;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private GameObjectSoundCatalog? _gameObjectSounds;
    private readonly Dictionary<ulong, uint> _knownGameObjectSoundStates = [];
    private readonly Dictionary<ulong, double> _gameObjectEventClocks = [];

    /// <summary>
    /// Observe GO_STATE edges and the event timelines of the dynamic M2s the
    /// doodad renderer already owns. First sight seeds both observers silently.
    /// </summary>
    private void UpdateGameObjectSounds()
    {
        if (_gameObjectSounds is null) return;
        double currentClock = NowSeconds();
        HashSet<ulong> seen = [];
        Vector3 listener = _controller?.Position ?? Vector3.Zero;
        foreach (WorldEntity go in _entities.Entities.Values.Where(entity => entity.IsGameObject))
        {
            seen.Add(go.Guid);
            uint state = go.Fields.GameObjectState;
            if (_knownGameObjectSoundStates.TryGetValue(go.Guid, out uint previousState) &&
                previousState != state)
            {
                int slot = GameObjectSoundLaw.StateSlot(state);
                if (slot >= 0) PlayGameObjectSlot(go, slot, listener);
            }
            _knownGameObjectSoundStates[go.Guid] = state;

            if (_doodads?.TryGetDynamicEventTimeline(go.Guid, out M2Model model,
                    out int sequence) != true)
            {
                _gameObjectEventClocks.Remove(go.Guid);
                continue;
            }
            if (!_gameObjectEventClocks.TryGetValue(go.Guid, out double previousClock))
            {
                _gameObjectEventClocks[go.Guid] = currentClock;
                continue;
            }
            _gameObjectEventClocks[go.Guid] = currentClock;
            foreach (GameObjectSlotEvent sound in GameObjectSoundLaw.CrossedEvents(
                         model, sequence, previousClock, currentClock))
                PlayGameObjectSlot(go, sound.Slot, listener);
        }

        foreach (ulong stale in _knownGameObjectSoundStates.Keys
                     .Where(guid => !seen.Contains(guid)).ToArray())
            _knownGameObjectSoundStates.Remove(stale);
        foreach (ulong stale in _gameObjectEventClocks.Keys
                     .Where(guid => !seen.Contains(guid)).ToArray())
            _gameObjectEventClocks.Remove(stale);
    }

    private void PlayGameObjectSlot(WorldEntity go, int slot, Vector3 listener)
    {
        // Loading/world-settle cover drops events rather than replaying them late.
        if (!_soundscapePlaybackArmed || _spellSounds is null) return;
        uint kit = _gameObjectSounds?.Sound(go.Fields.GameObjectDisplayId, slot) ?? 0;
        if (kit == 0) return;
        _spellSounds.Play(kit, go.Guid, go.Position, listener,
            forceLoop: false, trackHold: false, category: "sfx");
    }

    private void ResetGameObjectSoundState()
    {
        _knownGameObjectSoundStates.Clear();
        _gameObjectEventClocks.Clear();
    }
}
