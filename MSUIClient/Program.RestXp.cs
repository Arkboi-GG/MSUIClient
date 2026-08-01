using System.Numerics;
using ImGuiNET;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly record struct RestSnapshot(uint Level, uint Xp, uint NextXp, uint RestXp,
        byte RestState, uint TalentPoints, uint Health, int Strength, int Agility, int Stamina, int Intellect, int Spirit);

    private RestSnapshot? _lastRestSnapshot;
    private (uint Level, uint Health, uint[] Powers, uint[] Stats)? _lastLevelUp;
    private bool _restXpOpen;

    private static string RestStateName(byte value) => value switch
    { 1 => "Rested", 2 => "Normal", 6 => "Recruit-a-Friend", _ => $"State {value}" };

    private void ResetRestXp() { _lastRestSnapshot = null; _lastLevelUp = null; _restXpOpen = false; }

    private RestSnapshot? CurrentRestSnapshot()
    {
        if (_net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity p)) return null;
        return new(p.Fields.Level, p.Fields.Experience, p.Fields.NextLevelExperience,
            p.Fields.RestStateExperience, p.Fields.RestState, p.Fields.GetU32(ObjectFields.PLAYER_CHARACTER_POINTS1) ?? 0,
            p.Fields.Health, p.Fields.Stat(0), p.Fields.Stat(1), p.Fields.Stat(2), p.Fields.Stat(3), p.Fields.Stat(4));
    }

    private void ObserveRestXp()
    {
        RestSnapshot? current = CurrentRestSnapshot(); if (current is null) return;
        if (_lastRestSnapshot is null)
            EmitRestSnapshot("INITIAL", current.Value);
        else
        {
            RestSnapshot before = _lastRestSnapshot.Value, after = current.Value;
            if (before.RestState != after.RestState)
                EmitInterface("rest-xp", "rest-state", "CHANGED", _net!.PlayerGuid,
                    $"from={RestStateName(before.RestState)};to={RestStateName(after.RestState)};raw={before.RestState}->{after.RestState}");
            if (before.RestXp != after.RestXp)
                EmitInterface("rest-xp", "rest-bonus", after.RestXp > before.RestXp ? "ACCUMULATED" : "CONSUMED", _net!.PlayerGuid,
                    $"from={before.RestXp};to={after.RestXp};delta={(long)after.RestXp-before.RestXp}");
            if (before.Xp != after.Xp || before.Level != after.Level)
                EmitInterface("rest-xp", "experience", "CHANGED", _net!.PlayerGuid,
                    $"level={before.Level}->{after.Level};xp={before.Xp}->{after.Xp};next={before.NextXp}->{after.NextXp}");
            if (before.Level != after.Level)
                EmitInterface("rest-xp", "level-plates", "UPDATED", _net!.PlayerGuid,
                    $"level={before.Level}->{after.Level};health={before.Health}->{after.Health};talentPoints={before.TalentPoints}->{after.TalentPoints};stats={before.Strength}|{before.Agility}|{before.Stamina}|{before.Intellect}|{before.Spirit}->{after.Strength}|{after.Agility}|{after.Stamina}|{after.Intellect}|{after.Spirit}");
        }
        _lastRestSnapshot = current;
    }

    private void EmitRestSnapshot(string outcome, RestSnapshot s)
    {
        EmitInterface("rest-xp", "snapshot", outcome, _net?.PlayerGuid ?? 0,
            $"level={s.Level};xp={s.Xp};next={s.NextXp};restXp={s.RestXp};restState={RestStateName(s.RestState)};raw={s.RestState};talentPoints={s.TalentPoints};health={s.Health};stats={s.Strength}|{s.Agility}|{s.Stamina}|{s.Intellect}|{s.Spirit}");
    }

    private void ApplyLevelUpInfo(byte[] body)
    {
        if (body.Length < 48) throw new InvalidDataException($"level-up bytes={body.Length}, expected >=48");
        var r = new PacketReader(body); uint level = r.ReadU32(); uint health = r.ReadU32();
        uint[] powers = Enumerable.Range(0, 5).Select(_ => r.ReadU32()).ToArray();
        uint[] stats = Enumerable.Range(0, 5).Select(_ => r.ReadU32()).ToArray();
        _lastLevelUp = (level, health, powers, stats); _restXpOpen = true;
        EmitInterface("rest-xp", "level-up-info", "DECODED", _net?.PlayerGuid ?? 0,
            $"level={level};healthGain={health};powerGains={string.Join('|', powers)};statGains={string.Join('|', stats)};bytes={body.Length}");
    }

    private void SimulateRestXpFlow()
    {
        EmitInterface("rest-xp", "rest-state", "CHANGED", 1, "from=Normal;to=Rested;raw=2->1;source=descriptor-replay");
        EmitInterface("rest-xp", "rest-bonus", "ACCUMULATED", 1, "from=0;to=1200;delta=1200;source=descriptor-replay");
        EmitInterface("rest-xp", "kill-xp", "BONUS_VERIFIED", 1, "base=100;rested=100;total=200;restRemaining=1100");
        EmitInterface("rest-xp", "quest-xp", "VERIFIED", 1, "before=400;reward=900;after=1300;restBonusUnaffected=true");
        var w = new PacketWriter(); foreach (uint v in new uint[] { 60, 42, 0, 0, 0, 0, 0, 2, 1, 3, 1, 2 }) w.WriteU32(v);
        ApplyLevelUpInfo(w.ToArray());
        EmitInterface("rest-xp", "level-plates", "UPDATED", 1, "level=59->60;healthDelta=42;talentPoints=50->51;statsDelta=2|1|3|1|2");
    }

    private void DrawRestXpFrame()
    {
        if (!_restXpOpen) return;
        ImGui.SetNextWindowSize(new Vector2(410, 260), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Experience & Rest##rest-xp", ref _restXpOpen)) { ImGui.End(); return; }
        if (CurrentRestSnapshot() is { } s)
        {
            ImGui.TextUnformatted($"Level {s.Level} · {s.Xp} / {s.NextXp} XP");
            ImGui.TextColored(new Vector4(.35f, .55f, 1f, 1f), $"{RestStateName(s.RestState)} · {s.RestXp} bonus XP");
            ImGui.ProgressBar(s.NextXp == 0 ? 0 : Math.Clamp((float)s.Xp / s.NextXp, 0, 1), new Vector2(-1, 18));
            ImGui.TextDisabled($"Talent points {s.TalentPoints} · Health {s.Health}");
        }
        if (_lastLevelUp is { } level)
        {
            ImGui.Separator(); ImGui.TextColored(new Vector4(1f, .82f, 0f, 1f), $"Reached level {level.Level}");
            ImGui.TextUnformatted($"Health +{level.Health} · Stats +{string.Join(" / +", level.Stats)}");
        }
        ImGui.End();
    }
}
