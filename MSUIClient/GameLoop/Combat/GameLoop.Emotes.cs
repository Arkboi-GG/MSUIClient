using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Sound;

namespace MSUIClient;

public sealed partial class GameLoop
{
    /// <summary>
    /// Emotes.dbc id -&gt; AnimationData id (0 if unknown or a zero-anim state row), from the
    /// live <see cref="EmoteCatalog"/>. The single resolver for the state-emote (Dance,
    /// UNIT_NPC_EMOTESTATE) path on both renderers - the same DBC the SMSG_EMOTE one-shot
    /// path (<see cref="ApplyEmote"/>) reads, so there is no second hand-maintained table to
    /// drift. Wired onto _character and _creatures where they are created.
    /// </summary>
    private int ResolveEmoteAnim(uint emoteId) =>
        _emotes is not null && _emotes.TryGet(emoteId, out EmoteInfo info) ? (int)info.AnimationId : 0;

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
        {
            if (!ControlledBodyTacticallyFrozen)
                _character?.TriggerOneShot((int)emote.AnimationId);
        }
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
