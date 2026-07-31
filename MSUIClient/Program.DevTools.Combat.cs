using System.Globalization;
using System.Numerics;
using System.Text;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private const string CombatTraceHeader =
        "row,t,dt,event,cause,intentOn,targetGuid,opcode,swingTimerOwner,weaponSpeedMs," +
        "rangeEligibility,arcEligibility,clientAction,distance,bearingDelta,facingDelta,clipId,clipName," +
        "clipTime,clipB,clipBTime,blendWeight,animChoice,detail";
    private StreamWriter? _combatTraceWriter;
    private string _combatTraceName = "manual";
    private string _combatTracePath = "";
    private int _combatTraceRow;
    private double _combatTraceTime;
    private string _lastCombatStopCause = "server-stop";

    private void StartCombatTrace()
    {
        StopCombatTrace();
        string safe = string.Concat((_combatTraceName.Trim().Length == 0 ? "manual" : _combatTraceName.Trim())
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-'));
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string directory = Path.Combine(_config.RepoRoot, "dumps");
        Directory.CreateDirectory(directory);
        _combatTracePath = Path.Combine(directory, $"combattrace-{safe}-{stamp}.csv");
        _combatTraceWriter = new StreamWriter(_combatTracePath, false, new UTF8Encoding(false));
        _combatTraceWriter.WriteLine(CombatTraceHeader);
        _combatTraceRow = 0;
        _combatTraceTime = 0;
        EmitCombat("TraceStart", "operator", _attackTargetGuid,
            "server-authoritative swing timing; client range/arc gates absent");
    }

    private void StopCombatTrace()
    {
        if (_combatTraceWriter is null) return;
        _combatTraceWriter.Flush();
        _combatTraceWriter.Dispose();
        _combatTraceWriter = null;
        Console.WriteLine($"[combat-trace] wrote {_combatTraceRow} rows to {_combatTracePath}");
    }

    private void ObserveCombatSend(Op opcode, ulong target) =>
        EmitCombat(opcode == Op.CMSG_ATTACKSWING ? "AttackSwingSend" : "AttackStopSend",
            "network-session-send", target, opcode.ToString(), opcode);

    private void ObserveCombatIntent(bool on, ulong target, string cause) =>
        EmitCombat(on ? "IntentOn" : "IntentOff", cause, target, "local attack latch edge");

    private void ObserveCombatReceive(Op opcode, CombatEvent value)
    {
        (string evt, ulong target, string cause, string detail) = value switch
        {
            CombatAttackStarted x => ("AttackStartReceive", x.Victim, "server", $"attacker=0x{x.Attacker:X16}"),
            CombatAttackStopped x => ("AttackStopReceive", x.Victim, x.VictimDied ? "target-death" : "server",
                $"attacker=0x{x.Attacker:X16} victimDied={x.VictimDied}"),
            CombatMeleeSwing x => (x.Attacker == _net?.PlayerGuid ? "SwingReceive" : "ForeignSwingReceive", x.Victim, "server-swing",
                $"attacker=0x{x.Attacker:X16} damage={x.Damage} hitInfo=0x{x.HitInfo:X8}"),
            _ => ("CombatReceive", 0UL, "server", value.GetType().Name),
        };
        if (value is CombatAttackStopped stopped)
            _lastCombatStopCause = stopped.VictimDied ? "target-death" : "server-stop";
        EmitCombat(evt, cause, target, detail, opcode);
    }

    private void ObserveCombatError(Op opcode, byte[] body) =>
        EmitCombat("AttackErrorReceive", "server-law", _attackTargetGuid,
            $"opcode={opcode} value=0x{(ushort)opcode:X4} bytes={body.Length}", opcode);

    private void ObserveGmChatResponse(byte[] body)
    {
        string text=string.Join('|', System.Text.Encoding.UTF8.GetString(body)
            .Split('\0',StringSplitOptions.RemoveEmptyEntries)
            .Select(x=>new string(x.Where(ch=>!char.IsControl(ch)).ToArray())).Where(x=>x.Length>1));
        EmitCombat("GmChatResponse","server-chat",0,text.Length==0?$"bytes={body.Length}":text);
    }

    private void ObserveGmChatWire(bool outgoing, ushort opcode, ReadOnlySpan<byte> body) =>
        EmitCombat("GmChatWire", outgoing ? "client-send" : "server-receive", 0,
            $"opcode=0x{opcode:X4};bytes={body.Length};hex={Convert.ToHexString(body)}");

    private void ObserveTeleportWire(bool outgoing, ushort opcode, ReadOnlySpan<byte> body) =>
        EmitCombat("TeleportWire", outgoing ? "client-send" : "server-receive", 0,
            $"opcode=0x{opcode:X4};bytes={body.Length};hex={Convert.ToHexString(body)}");

    private void ObserveCombatAnimationChoice(in AnimChoice choice)
    {
        if (!choice.Unit.Equals("player", StringComparison.OrdinalIgnoreCase) ||
            (_attackTargetGuid == 0 && (_net is null || !_combat.IsEngaged(_net.PlayerGuid)))) return;
        EmitCombat("AnimChoice", "animator", _attackTargetGuid,
            $"track={choice.Track} requested={choice.RequestedId} played={choice.PlayedId} kind={choice.Kind} " +
            $"base={_character?.ClipId ?? -1}:{_character?.ClipName ?? "none"} " +
            $"clipB={_character?.BlendFrom ?? ""} blendWeight={_character?.IncomingBlendWeight ?? 0:R}");
    }

    private void SampleCombatTrace(float dt)
    {
        if (_combatTraceWriter is null ||
            (_attackTargetGuid == 0 && (_net is null || !_combat.IsEngaged(_net.PlayerGuid)))) return;
        _combatTraceTime += dt;
        WriteCombatRow(dt, "Tick", "live-sample", _attackTargetGuid, "", null,
            "none; server owns swing cadence");
    }

    private void EmitCombat(string evt, string cause, ulong target, string detail, Op? opcode = null)
    {
        var verdict = new CombatVerdict(NowSeconds(), evt, cause, target, detail);
        _verdicts.Add(verdict);
        Console.WriteLine($"[verdict:combat] {verdict.ToLine()}");
        if (_combatTraceWriter is not null)
            WriteCombatRow(0, evt, cause, target, opcode?.ToString() ?? "", opcode, detail);
    }

    private void WriteCombatRow(float dt, string evt, string cause, ulong target, string opcodeText,
        Op? opcode, string detail)
    {
        if (_combatTraceWriter is null) return;
        float distance = -1, bearing = float.NaN;
        if (_controller is not null && target != 0 && _entities.TryGet(target, out WorldEntity entity))
        {
            Vector2 delta = new(entity.Position.X - _controller.Position.X,
                entity.Position.Y - _controller.Position.Y);
            distance = delta.Length();
            if (distance > 1e-5f)
                bearing = MathF.Atan2(delta.Y, delta.X) - _controller.Yaw;
        }
        string anim = _lastAnimChoices.TryGetValue(("player", 0), out var choice)
            ? $"{choice.Requested}->{choice.Played}:{choice.Kind}" : "NONE";
        _combatTraceWriter.WriteLine(string.Join(',',
            _combatTraceRow++, _combatTraceTime.ToString("F6", CultureInfo.InvariantCulture),
            dt.ToString("F6", CultureInfo.InvariantCulture), Csv(evt), Csv(cause),
            (_attackTargetGuid != 0).ToString().ToLowerInvariant(), $"0x{target:X16}", Csv(opcodeText),
            "server", "", "unchecked", "unchecked", "none",
            distance.ToString("R", CultureInfo.InvariantCulture),
            float.IsNaN(bearing) ? "" : bearing.ToString("R", CultureInfo.InvariantCulture),
            float.IsNaN(bearing) ? "" : bearing.ToString("R", CultureInfo.InvariantCulture),
            _character?.ClipId ?? -1, Csv(_character?.ClipName ?? "none"),
            (_character?.ClipTime ?? 0).ToString("R", CultureInfo.InvariantCulture),
            Csv(_character?.BlendFrom ?? ""),
            _character is { BlendFrom.Length: > 0 } c ? c.BlendFromTime.ToString("R", CultureInfo.InvariantCulture) : "",
            _character is { BlendFrom.Length: > 0 } b ? b.IncomingBlendWeight.ToString("R", CultureInfo.InvariantCulture) : "",
            Csv(anim), Csv(detail)));
        _combatTraceWriter.Flush();
    }

    private void DrawCombatInstrumentsPanel()
    {
        if (!ImGui.CollapsingHeader("Combat instruments")) return;
        ImGui.InputText("Trace name##combat", ref _combatTraceName, 64);
        if (_combatTraceWriter is null)
        {
            if (ImGui.Button("Start combat trace")) StartCombatTrace();
        }
        else if (ImGui.Button("Stop combat trace")) StopCombatTrace();
        ImGui.SameLine();
        ImGui.TextUnformatted(_combatTraceWriter is null ? "idle" : $"recording {_combatTraceRow} rows");
        if (_combatTracePath.Length > 0)
        {
            ImGui.TextWrapped(_combatTracePath);
            if (ImGui.Button("Copy combat trace path")) ImGui.SetClipboardText(_combatTracePath);
        }
    }
}
