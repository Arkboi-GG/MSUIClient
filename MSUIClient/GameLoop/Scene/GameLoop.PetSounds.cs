using MSUIClient.Net;
using MSUIClient.World.Sound;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private uint ResolvePetActionSound(PetActionSoundPacket packet, ulong owner)
    {
        if (owner == 0 || owner != ControlledGuid || packet.PetGuid == 0 ||
            !_entities.TryGet(owner, out WorldEntity actor) || actor.Fields.Summon != packet.PetGuid ||
            !_entities.TryGet(packet.PetGuid, out WorldEntity pet) || !pet.IsUnit || pet.DisplayId <= 0 ||
            _creatureVoices?.TryGet((uint)pet.DisplayId, out var voice) != true) return 0;
        return packet.SoundKit(voice);
    }

    private void ApplyPetActionSound(byte[] body, ulong owner)
    {
        var packet = PetActionSoundPacket.Parse(body);
        uint kit = ResolvePetActionSound(packet, owner);
        if (kit == 0 || !AudioFeaturePolicy.ExpandedWorldAudioEnabled ||
            !_soundscapePlaybackArmed || _spellSounds is null ||
            !_entities.TryGet(packet.PetGuid, out WorldEntity pet)) return;
        var listener = _controller?.Position ?? pet.Position;
        _spellSounds.Play(kit, packet.PetGuid, pet.Position, listener,
            forceLoop: false, trackHold: false, category: "creature");
    }
}
