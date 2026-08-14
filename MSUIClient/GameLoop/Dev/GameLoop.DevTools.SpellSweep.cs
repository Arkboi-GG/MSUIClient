using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private int EmitKnownSpellInventory()
    {
        if (_spellCatalog is null) return 0;
        int count = 0;
        foreach (uint spellId in _actions.KnownSpells.OrderBy(x => x))
        {
            if (!_spellCatalog.TryGet(spellId, out SpellInfo spell)) continue;
            EmitSpellInventoryRow(spell, spell.Passive ? "ROSTER_PASSIVE" : "ROSTER_KNOWN");
            if (!spell.Passive) count++;
        }
        return count;
    }

    private void EmitSpellInventoryRow(in SpellInfo spell, string result)
    {
        byte classId = 0;
        uint power = 0;
        byte powerType = (byte)spell.PowerType;
        if (_net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player))
        { classId = player.Fields.Bytes0.Class; power = player.Fields.Power(powerType); }
        var verdict = new SpellSweepVerdict(NowSeconds(), _net?.PlayerName ?? "", classId,
            spell.Id, spell.Name, SchoolName(spell.School), spell.AutoRepeat ? "AUTO_REPEAT" :
            spell.OnNextSwing ? "NEXT_SWING" : spell.Passive ? "PASSIVE" : "CAST", result,
            _character?.CurrentAnimation ?? "none", SpellEffectCheck(spell), "UNBOUND",
            true, true, PowerName(powerType), power, spell.ManaCost, 0, false);
        _verdicts.Add(verdict);
    }

    private bool SpellResourceGate(in SpellInfo spell, out uint available, out uint cost)
    {
        available = 0; cost = spell.ManaCost;
        if (_net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return false;
        byte powerType = (byte)spell.PowerType;
        available = player.Fields.Power(powerType);
        uint baseAmount = spell.ManaCostPercent == 0 ? 0u :
            powerType == 0 ? player.Fields.BaseMana : player.Fields.MaxPower(powerType);
        cost += baseAmount * spell.ManaCostPercent / 100;
        return available >= cost;
    }

    private void EmitSpellSweep(uint spellId, CastTargetReason reason, ulong resolvedGuid, bool sent)
    {
        SpellInfo? info = _spellCatalog?.TryGet(spellId, out SpellInfo found) == true ? found : null;
        uint power = 0, cost = 0;
        byte powerType = (byte)(info?.PowerType ?? 0);
        byte classId = 0;
        if (_net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player))
        {
            power = player.Fields.Power(powerType);
            classId = player.Fields.Bytes0.Class;
            if (info is { } spell)
            {
                uint baseAmount = spell.ManaCostPercent == 0 ? 0u :
                    powerType == 0 ? player.Fields.BaseMana : player.Fields.MaxPower(powerType);
                cost = spell.ManaCost + baseAmount * spell.ManaCostPercent / 100;
            }
        }
        double clock = MovementInfo.ClientUptimeMs() / 1000.0;
        string targetType = resolvedGuid == 0 ? "SELF_IMPLICIT" :
            resolvedGuid == _net?.PlayerGuid ? "SELF" : "UNIT";
        var verdict = new SpellSweepVerdict(NowSeconds(), _net?.PlayerName ?? "",
            classId, spellId, info?.Name ?? $"Spell {spellId}", SchoolName(info?.School ?? 0),
            info is { AutoRepeat: true } ? "AUTO_REPEAT" : info is { OnNextSwing: true } ? "NEXT_SWING" : "CAST",
            sent ? "PRE_SEND_PASS" : $"LOCAL_{reason}", _character?.CurrentAnimation ?? "none",
            SpellEffectCheck(info), targetType, clock >= _globalCooldownUntil,
            !_actions.IsOnCooldown(spellId, clock, info?.Category ?? 0), PowerName(powerType), power, cost, resolvedGuid, sent);
        _verdicts.Add(verdict);
        Console.WriteLine($"[verdict:spell-sweep] {verdict.ToLine()}");
    }

    private void EmitSpellServerResult(uint spellId, string result)
    {
        SpellInfo? info = _spellCatalog?.TryGet(spellId, out SpellInfo found) == true ? found : null;
        byte classId = 0;
        uint power = 0;
        byte powerType = (byte)(info?.PowerType ?? 0);
        if (_net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player))
        { classId = player.Fields.Bytes0.Class; power = player.Fields.Power(powerType); }
        var verdict = new SpellSweepVerdict(NowSeconds(), _net?.PlayerName ?? "", classId,
            spellId, info?.Name ?? $"Spell {spellId}", SchoolName(info?.School ?? 0),
            _castBarPhase == CastBarPhase.Channel ? "CHANNEL" : "CAST", result,
            _character?.CurrentAnimation ?? "none", SpellEffectCheck(info), "SERVER",
            MovementInfo.ClientUptimeMs() / 1000.0 >= _globalCooldownUntil,
            !_actions.IsOnCooldown(spellId, MovementInfo.ClientUptimeMs() / 1000.0, info?.Category ?? 0),
            PowerName(powerType), power, 0, 0, true);
        _verdicts.Add(verdict);
        Console.WriteLine($"[verdict:spell-sweep] {verdict.ToLine()}");
    }

    private bool EmitAuraEffectCheck(uint spellId, bool expectedPresent)
    {
        WorldEntity? player = null;
        bool hasPlayer = _net is not null && _entities.TryGet(_net.PlayerGuid, out player);
        bool present = hasPlayer && player!.Fields.Auras().Any(a => a.SpellId == spellId);
        byte classId = hasPlayer ? player!.Fields.Bytes0.Class : (byte)0;
        SpellInfo? info = _spellCatalog?.TryGet(spellId, out SpellInfo found) == true ? found : null;
        var verdict = new SpellSweepVerdict(NowSeconds(), _net?.PlayerName ?? "", classId, spellId,
            info?.Name ?? $"Spell {spellId}", SchoolName(info?.School ?? 0), "EFFECT_CHECK",
            present ? "AURA_PRESENT" : "AURA_ABSENT", _character?.CurrentAnimation ?? "none",
            "PLAYER_DESCRIPTOR_AURA", "SELF", true, true, "NONE", 0, 0,
            _net?.PlayerGuid ?? 0, false);
        _verdicts.Add(verdict);
        Console.WriteLine($"[verdict:spell-sweep] {verdict.ToLine()}");
        return present == expectedPresent;
    }

    private void EmitSpellBlocked(uint spellId, string finding)
    {
        SpellInfo? info = _spellCatalog?.TryGet(spellId, out SpellInfo found) == true ? found : null;
        var verdict = new SpellSweepVerdict(NowSeconds(), _net?.PlayerName ?? "", 0, spellId,
            info?.Name ?? $"Spell {spellId}", SchoolName(info?.School ?? 0),
            info?.CastClassification ?? "UNKNOWN", $"BLOCKED-BY:{finding}",
            _character?.CurrentAnimation ?? "none", SpellEffectCheck(info), "LIVE_LEG",
            true, true, "NONE", 0, 0, 0, false);
        _verdicts.Add(verdict);
        Console.WriteLine($"[verdict:spell-sweep] {verdict.ToLine()}");
    }


    private string SpellEffectCheck(SpellInfo? info)
    {
        if (info is not { VisualId: > 0 } spell || _spellVisualCatalog is null ||
            !_spellVisualCatalog.TryGetStages(spell.VisualId, out SpellVisualStages stages)) return "NO_VISUAL";
        var paths = new List<string>();
        foreach (uint kitId in new[] { stages.Precast, stages.Cast, stages.Impact, stages.State, stages.Channel })
            if (kitId != 0 && _spellVisualCatalog.TryGetKit(kitId, out SpellVisualKitInfo kit))
                paths.AddRange(kit.Effects.Select(x => x.ModelPath));
        string? missile = _spellVisualCatalog.MissilePath(stages);
        if (missile is not null) paths.Add(missile);
        return paths.Count == 0 ? "VISUAL_CHAIN_NO_MODEL" : string.Join('|', paths.Distinct());
    }

    private static string SchoolName(uint school) => school switch
    {
        0 => "PHYSICAL", 1 => "HOLY", 2 => "FIRE", 3 => "NATURE",
        4 => "FROST", 5 => "SHADOW", 6 => "ARCANE", _ => $"SCHOOL_{school}",
    };

    private static string PowerName(byte powerType) => powerType switch
    { 0 => "MANA", 1 => "RAGE", 3 => "ENERGY", _ => $"POWER_{powerType}" };
}
