using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Sound;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private void ApplyEmote(EmotePacket packet)
    {
        if (_emotes?.TryGet(packet.EmoteId, out EmoteInfo emote) != true ||
            emote.AnimationId == 0 ||
            !_entities.TryGet(packet.UnitGuid, out WorldEntity unit)) return;

        // The receive-side client gate is deliberately EmoteFlags-blind. Sleep and swim suppress
        // every unit; channel/combat are the common one-shot player gate used by the reference.
        if (unit.Fields.UnitStandState == StandStateUiLaw.Sleep ||
            (unit.MoveFlags & (uint)MovementFlags.Swimming) != 0 ||
            unit.Fields.ChannelSpell != 0 || unit.InCombat) return;

        if (packet.UnitGuid == ControlledGuid && !ControlledBodyIsStreamed)
            _character?.TriggerOneShot((int)emote.AnimationId);
        else
            _creatures?.TriggerOneShot(packet.UnitGuid, (int)emote.AnimationId);

        if (AudioFeaturePolicy.ExpandedWorldAudioEnabled &&
            emote.EventSoundId != 0 && _spellSounds is not null && _controller is not null)
        {
            var source = TryGetWorldBodyPose(packet.UnitGuid, out WorldBodyPose bodyPose)
                ? bodyPose.Position
                : unit.Position;
            _spellSounds.Play(emote.EventSoundId, packet.UnitGuid, source, _controller.Position,
                forceLoop: false, trackHold: false, category: "creature");
        }
    }
}
