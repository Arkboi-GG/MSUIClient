using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed class PendingMeleeSound(CombatMeleeSwing swing)
    {
        public CombatMeleeSwing Swing = swing;
        public bool ImpactConsumed;
    }

    private WeaponImpactCatalog? _weaponImpacts;
    private readonly Dictionary<ulong, PendingMeleeSound> _pendingMeleeSounds = [];

    private void WireMeleeSounds()
    {
        if (_mpq is not null) _weaponImpacts = WeaponImpactCatalog.Load(_mpq);
        if (_creatures is not null)
            _creatures.CombatAnimationSoundEvent = PlayMeleeAnimationSoundEvent;
        if (_character is not null)
            _character.CombatAnimationSoundEvent = identifier =>
                PlayMeleeAnimationSoundEvent(ControlledGuid, identifier);
        Console.WriteLine(_weaponImpacts is null
            ? "[combat-sound] WeaponImpactSounds unavailable"
            : $"[combat-sound] {_weaponImpacts.Count} weapon impact row(s)");
    }

    private void QueueMeleeSound(CombatMeleeSwing swing)
    {
        var pending = new PendingMeleeSound(swing);
        _pendingMeleeSounds[swing.Attacker] = pending;
        bool attackerResolved = swing.Attacker == ControlledGuid && !ControlledBodyIsStreamed
            ? _character?.Loaded == true
            : _creatures?.TryGetSpellPose(swing.Attacker, out _) == true;
        if (!attackerResolved)
        {
            PlayMeleeContact(pending, naturalAttack: null);
            pending.ImpactConsumed = true;
        }
        if (_pendingMeleeSounds.Count > 128)
            foreach (ulong guid in _pendingMeleeSounds.Keys
                         .Where(guid => !_entities.TryGet(guid, out _)).ToArray())
                _pendingMeleeSounds.Remove(guid);
    }

    private void PlayMeleeAnimationSoundEvent(ulong attackerGuid, string identifier)
    {
        if (!_soundscapePlaybackArmed ||
            !_pendingMeleeSounds.TryGetValue(attackerGuid, out PendingMeleeSound? pending)) return;
        CombatMeleeSwing swing = pending.Swing;
        if (identifier == "$CSS" && MeleeSoundLaw.NoContact(swing))
        {
            var weapon = SwingWeapon(attackerGuid, (swing.HitInfo & 0x4u) != 0);
            uint kit = MeleeSoundLaw.MissKit(weapon.Subclass);
            PlayMeleeKit(kit, attackerGuid, PositionOf(attackerGuid));
            return;
        }
        if (identifier == "$CAH")
        {
            if (_entities.TryGet(attackerGuid, out WorldEntity attacker) &&
                _creatureVoices?.TryGet((uint)attacker.DisplayId, out var voice) == true)
                PlayMeleeKit((swing.HitInfo & 0x80u) != 0
                    ? voice.ExertionCriticalSound : voice.ExertionSound,
                    attackerGuid, attacker.Position);
        }
        int? natural = identifier is "$AH0" or "$AH1" or "$AH2" or "$AH3"
            ? identifier[3] - '0' : null;
        if (!pending.ImpactConsumed && (natural is not null || identifier == "$CAH"))
        {
            PlayMeleeContact(pending, natural);
            pending.ImpactConsumed = true;
        }
    }

    private void PlayMeleeContact(PendingMeleeSound pending, int? naturalAttack)
    {
        CombatMeleeSwing swing = pending.Swing;
        if (MeleeSoundLaw.NoContact(swing)) return;
        _entities.TryGet(swing.Attacker, out WorldEntity? attacker);
        _entities.TryGet(swing.Victim, out WorldEntity? victim);
        var position = attacker?.Position ?? victim?.Position ?? default;
        bool crit = (swing.HitInfo & 0x80u) != 0;
        bool defended = MeleeSoundLaw.Defended(swing.VictimState);
        var weapon = SwingWeapon(swing.Attacker, (swing.HitInfo & 0x4u) != 0);
        WeaponImpactRow row = default;
        bool hasRow = _weaponImpacts?.TryGet(
            weapon.Subclass, metal: !weapon.Wooden, out row) == true;

        if (naturalAttack is int n && attacker is not null &&
            _creatureVoices?.TryGet((uint)attacker.DisplayId, out var attackVoice) == true)
        {
            uint naturalKit = n switch
            {
                0 => attackVoice.CustomAttack1Sound, 1 => attackVoice.CustomAttack2Sound,
                2 => attackVoice.CustomAttack3Sound, 3 => attackVoice.CustomAttack4Sound,
                _ => 0,
            };
            PlayMeleeKit(naturalKit, swing.Attacker, position);
        }
        else if (!defended && hasRow)
        {
            uint impactType = victim is not null &&
                _creatureVoices?.TryGet((uint)victim.DisplayId, out var victimVoice) == true
                    ? victimVoice.ImpactType : 0;
            int slot = MeleeSoundLaw.TargetSlot(impactType);
            PlayMeleeKit((crit ? row.Critical : row.Impact)[slot], swing.Attacker, position);
        }

        if (defended && hasRow)
        {
            bool victimWood = SwingWeapon(swing.Victim, offHand: false).Wooden;
            int slot = MeleeSoundLaw.DefenseSlot(swing.VictimState, victimWood);
            PlayMeleeKit(row.Impact[slot], swing.Victim, victim?.Position ?? position);
        }

        if (swing.Damage > 0 && (swing.HitInfo & 0x60u) == 0 && !defended &&
            victim is not null &&
            _creatureVoices?.TryGet((uint)victim.DisplayId, out var injury) == true)
        {
            uint kit = MeleeSoundLaw.InjuryKit(injury, swing.HitInfo);
            PlayMeleeKit(kit, swing.Victim, victim.Position);
        }
    }

    private (uint Subclass, bool Wooden) SwingWeapon(ulong guid, bool offHand)
    {
        if (!_entities.TryGet(guid, out WorldEntity unit)) return (13, false);
        var info = unit.Fields.VirtualItemInfo(offHand ? 1 : 0);
        return info.Class == 2 ? ((uint)info.Subclass, info.Material == 2) : (13u, false);
    }

    private System.Numerics.Vector3 PositionOf(ulong guid) =>
        _entities.TryGet(guid, out WorldEntity unit) ? unit.Position :
        _controller?.Position ?? default;

    private void PlayMeleeKit(uint kit, ulong owner, System.Numerics.Vector3 position)
    {
        if (kit == 0 || _spellSounds is null) return;
        _spellSounds.Play(kit, owner, position, _controller?.Position ?? position,
            forceLoop: false, trackHold: false, category: "sfx");
    }
}
