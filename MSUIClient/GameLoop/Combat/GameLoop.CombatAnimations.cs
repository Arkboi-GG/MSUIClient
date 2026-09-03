using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    /// <summary>
    /// ATTACKSTART is only the engagement bracket. The actual one-shot starts
    /// when the server reports a completed swing in ATTACKERSTATEUPDATE.
    /// </summary>
    private void ApplyCombatAnimation(CombatEvent combatEvent)
    {
        ApplyCombatFeedback(combatEvent);
        if (combatEvent is not CombatMeleeSwing swing || _net is null) return;

        bool offHand = (swing.HitInfo & 0x0004u) != 0;
        bool landedHit = swing.Damage > 0 && swing.VictimState is 0 or 1;
        QueueMeleeSound(swing);
        // The Swing Timer rail reads the same typed event and the same offhand bit, rather
        // than the localized chat text its 1.12 addon had to parse.
        NoteSwingTimerMelee(swing);

        if (swing.Attacker == ControlledGuid && !ControlledBodyIsStreamed)
            _character?.TriggerCombatSwing(offHand);
        else
            _creatures?.TriggerCombatSwing(swing.Attacker, offHand);

        if (swing.Victim == ControlledGuid && !ControlledBodyIsStreamed)
            _character?.TriggerCombatReaction(swing.VictimState, landedHit);
        else
            _creatures?.TriggerCombatReaction(swing.Victim, swing.VictimState, landedHit);
    }
}
