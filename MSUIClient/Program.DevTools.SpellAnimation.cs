using MSUIClient.Engine;
using MSUIClient.Formats;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private uint _presentedEffectSpell;

    /// <summary>The unit the current presented kit is anchored to. Usually the local
    /// player; the creator loop anchors IMPACT on the spawned target instead.</summary>
    private ulong _presentedEffectGuid;

    /// <summary>Reap the presented effect wherever it was spawned (player and/or target).</summary>
    private void ReapPresentedEffect()
    {
        if (_presentedEffectSpell == 0 || _spellEffects is null) return;
        _spellEffects.Reap(LocalPlayerGuid, _presentedEffectSpell);
        if (_presentedEffectGuid != 0 && _presentedEffectGuid != LocalPlayerGuid)
            _spellEffects.Reap(_presentedEffectGuid, _presentedEffectSpell);
        _presentedEffectSpell = 0;
        _presentedEffectGuid = 0;
    }

    /// <summary>Present one visual-kit stage. <paramref name="onGuid"/> anchors the kit
    /// on another unit (the creator loop lands impact on the spawned target); the
    /// character animation always plays on the local player either way.</summary>
    private bool PresentSpellEffect(uint spellId, string stage, ulong? onGuid = null)
    {
        if (_spellCatalog?.TryGet(spellId, out SpellInfo info) != true ||
            _spellVisualCatalog?.TryGetStages(info.VisualId, out SpellVisualStages stages) != true ||
            _spellEffects is null) return false;
        uint kitId = stage.ToLowerInvariant() switch
        {
            "precast" => stages.Precast,
            "cast" => stages.Cast,
            "impact" => stages.Impact,
            "state" => stages.State,
            "channel" => stages.Channel,
            _ => 0,
        };
        if (kitId == 0 || !_spellVisualCatalog.TryGetKit(kitId, out SpellVisualKitInfo kit)) return false;
        ReapPresentedEffect();
        ulong anchor = onGuid ?? LocalPlayerGuid;
        _presentedEffectSpell = spellId;
        _presentedEffectGuid = anchor;
        _spellEffects.SpawnKit(anchor, spellId, kit,
            persistent: stage is "precast" or "state" or "channel", NowSeconds(), stage.ToUpperInvariant());
        if (anchor != LocalPlayerGuid)
        {
            // Anchored on another unit (creator-loop impact on the spawned
            // target): the kit's animation is the VICTIM's reaction - play it on
            // that unit, exactly like the networked impact path, and leave the
            // local character alone (ReleaseSpellVisual would make the PLAYER
            // flinch with the victim's wound animation).
            if (kit.AnimationId is { } victimAnim && victimAnim != 0)
                _creatures?.ReleaseSpellVisual(anchor, victimAnim);
            else
                // Kit authors no victim animation - fall back to the plain
                // landed-hit flinch (CombatWound) so the impact still reads.
                _creatures?.TriggerCombatReaction(anchor, 0, landedHit: true);
        }
        else if (stage.Equals("precast", StringComparison.OrdinalIgnoreCase) ||
                 stage.Equals("channel", StringComparison.OrdinalIgnoreCase))
            _character?.BeginSpellVisual(kit.AnimationId);
        else _character?.ReleaseSpellVisual(kit.AnimationId);
        EmitSpellAnimation(info, stage.ToUpperInvariant(), kitId, kit.AnimationId, "DBC_EFFECT_SUPPLIER");
        return true;
    }

    private bool PresentSpellAnimation(uint spellId, string stage, string source)
    {
        if (_spellCatalog?.TryGet(spellId, out SpellInfo info) != true || _character is null)
            return false;
        if (_spellVisualCatalog?.TryGetStages(info.VisualId, out SpellVisualStages stages) != true)
            return false;
        uint kitId = stage.ToLowerInvariant() switch
        {
            "precast" => stages.Precast,
            "cast" => stages.Cast,
            "channel" => stages.Channel,
            _ => 0,
        };
        if (kitId == 0 || _spellVisualCatalog.TryGetKit(kitId, out SpellVisualKitInfo kit) != true)
            return false;
        if (stage.Equals("precast", StringComparison.OrdinalIgnoreCase) ||
            stage.Equals("channel", StringComparison.OrdinalIgnoreCase))
            _character.BeginSpellVisual(kit.AnimationId);
        else
            _character.ReleaseSpellVisual(kit.AnimationId);
        EmitSpellAnimation(info, stage.ToUpperInvariant(), kitId, kit.AnimationId, source);
        return true;
    }

    private bool SampleSpellAnimation(uint spellId, string stage, string source)
    {
        if (_spellCatalog?.TryGet(spellId, out SpellInfo info) != true || _character is null ||
            _spellVisualCatalog?.TryGetStages(info.VisualId, out SpellVisualStages stages) != true)
            return false;
        uint kitId = stage.ToLowerInvariant() switch
        {
            "precast" => stages.Precast,
            "cast" => stages.Cast,
            "channel" => stages.Channel,
            _ => 0,
        };
        ushort? animation = kitId != 0 &&
            _spellVisualCatalog.TryGetKit(kitId, out SpellVisualKitInfo kit) ? kit.AnimationId : null;
        EmitSpellAnimation(info, stage.ToUpperInvariant(), kitId, animation, source);
        return kitId != 0;
    }

    private void EmitSpellAnimation(in SpellInfo info, string stage, uint kitId,
        ushort? authoredAnimation, string source)
    {
        int track = stage is "PRECAST" or "CHANNEL" ? 2 : 1;
        (int Requested, int Played, AnimChoiceKind Kind) state =
            _lastAnimChoices.TryGetValue(("player", track), out var found)
                ? found : (-1, -1, AnimChoiceKind.Missing);
        var verdict = new SpellAnimationVerdict(NowSeconds(), _net?.PlayerName ?? "",
            info.Id, info.Name, SchoolName(info.School), stage, kitId,
            authoredAnimation ?? -1, state.Requested, state.Played, state.Kind,
            _character?.CurrentPresentationAnimation ?? "none",
            (_character?.GroundSpeed ?? 0) > 0.3f,
            info.CastClassification == "INSTANT" || !info.MovementInterrupts,
            info.MovementInterrupts,
            _character?.CurrentBaseAnimation ?? "none",
            _character?.PreviousBaseAnimation ?? "none",
            _character?.CurrentActionAnimation ?? "none",
            _character?.CurrentSpellHoldAnimation ?? "none",
            _character?.CurrentBlendWeight ?? 1f,
            source);
        _verdicts.Add(verdict);
        Console.WriteLine($"[verdict:spell-animation] {verdict.ToLine()}");
    }
}
