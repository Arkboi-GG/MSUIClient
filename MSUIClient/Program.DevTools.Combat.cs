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
    private bool? _serverGmMode;
    private bool? _lastAttackPreconditionGatePassed;

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

    private void ObserveCombatError(Op opcode, byte[] body)
    {
        string text = CombatAttackErrorText.ForOpcode(opcode);
        ShowUiError(text);
        EmitCombat("AttackErrorReceive", "server-law", _attackTargetGuid,
            $"opcode={opcode} value=0x{(ushort)opcode:X4} bytes={body.Length} text={text}", opcode);
    }

    // Devtools GM-mode probe only. Display now goes through HandleMessageChat /
    // HandleNotification; this crude whole-body scrape must NOT post to chat (it renders
    // the packet header bytes as garbage) - it just scans for the GM ON/OFF marker and
    // fires the GmChatResponse event the spell-matrix scenario waits on.
    private void ObserveGmChatResponse(byte[] body)
    {
        string text=string.Join('|', System.Text.Encoding.UTF8.GetString(body)
            .Split('\0',StringSplitOptions.RemoveEmptyEntries)
            .Select(x=>new string(x.Where(ch=>!char.IsControl(ch)).ToArray())).Where(x=>x.Length>1));
        if(text.Contains("GM mode is ON",StringComparison.OrdinalIgnoreCase)) _serverGmMode=true;
        else if(text.Contains("GM mode is OFF",StringComparison.OrdinalIgnoreCase)) _serverGmMode=false;
        EmitCombat("GmChatResponse","server-chat",0,text.Length==0?$"bytes={body.Length}":text);
    }

    private bool ObserveAttackPrecondition(WorldEntity target)
    {
        Vector3 player=_controller?.Position??Vector3.Zero;
        float distance=_controller is null?-1:Vector3.Distance(player,target.Position);
        bool present=_entities.TryGet(target.Guid,out WorldEntity current)&&ReferenceEquals(current,target);
        // The entity store is the live visibility set used by targeting: an
        // out-of-range descriptor is removed before this send path can act on it.
        bool visible=present;
        bool alive=!target.IsDead&&target.Fields.Health>0;
        EmitCombat("AttackPrecondition","live-send-path",target.Guid,
            $"player=0x{_net?.PlayerGuid??0:X16};position={player.X:R}|{player.Y:R}|{player.Z:R};"+
            $"gmMode={(_serverGmMode.HasValue?_serverGmMode.Value.ToString().ToLowerInvariant():"unmeasured")};"+
            $"gmSource=server-response;present={present.ToString().ToLowerInvariant()};"+
            $"visible={visible.ToString().ToLowerInvariant()};alive={alive.ToString().ToLowerInvariant()};"+
            $"health={target.Fields.Health};maxHealth={target.Fields.MaxHealth};"+
            $"unitFlags=0x{target.Fields.UnitFlags:X8};dynamicFlags=0x{target.Fields.DynamicFlags:X8};"+
            $"faction={target.Fields.FactionTemplate};entry={target.Entry};distance={distance:R};"+
            $"targetPosition={target.Position.X:R}|{target.Position.Y:R}|{target.Position.Z:R}");
        if(!_config.DevTools) return true;

        bool pass=present&&visible&&alive&&target.Fields.Health==100&&target.Fields.MaxHealth==100&&
            target.Fields.DynamicFlags==0&&target.Fields.UnitFlags==0&&_serverGmMode==false&&
            _controller is not null&&Vector3.DistanceSquared(player,target.Position)<=1e-6f;
        // The send is always allowed now (see the note at the return); record that
        // as the harness-visible gate result. The strict `pass` predicate below is
        // still emitted to the verdict stream as pure diagnostics.
        _lastAttackPreconditionGatePassed=true;
        string[] reasons=
        [
            ..(!present?["absent"]:Array.Empty<string>()),
            ..(!visible?["not-visible"]:Array.Empty<string>()),
            ..(!alive?["dead"]:Array.Empty<string>()),
            ..(target.Fields.Health!=100||target.Fields.MaxHealth!=100?["health-not-100/100"]:Array.Empty<string>()),
            ..(target.Fields.DynamicFlags!=0?["dynamicFlags-nonzero"]:Array.Empty<string>()),
            ..(target.Fields.UnitFlags!=0?["unitFlags-nonzero"]:Array.Empty<string>()),
            ..(_serverGmMode!=false?["gm-not-confirmed-off"]:Array.Empty<string>()),
            ..(_controller is null||Vector3.DistanceSquared(player,target.Position)>1e-6f?["distance-nonzero"]:Array.Empty<string>()),
        ];
        EmitCombat(pass?"AttackPreconditionGatePass":"AttackPreconditionGateRefusal",
            "devtools-pre-send-gate",target.Guid,
            $"packetConstructed={pass.ToString().ToLowerInvariant()};epsilon=distanceSquared<=1e-6;"+
            $"reasons={(reasons.Length==0?"none":string.Join('|',reasons))}");
        // This is a diagnostic probe, not a gameplay rule. Its `pass` predicate
        // (target at the player's exact position, 100/100 HP, GM-mode-off) was a
        // controlled instrument scenario and is never true in real melee. Gating
        // the live send on it disabled all auto-attack. Real target validity is
        // enforced by CanAttack at the call site; here we only record the verdict.
        return true;
    }

    private void ObserveGmChatWire(bool outgoing, ushort opcode, ReadOnlySpan<byte> body) =>
        EmitCombat("GmChatWire", outgoing ? "client-send" : "server-receive", 0,
            $"opcode=0x{opcode:X4};bytes={body.Length};hex={Convert.ToHexString(body)}");

    private void ObserveTeleportWire(bool outgoing, ushort opcode, ReadOnlySpan<byte> body) =>
        EmitCombat("TeleportWire", outgoing ? "client-send" : "server-receive", 0,
            $"opcode=0x{opcode:X4};bytes={body.Length};hex={Convert.ToHexString(body)}");

    private void ObserveTeleportApplied(ulong guid, uint counter, MovementInfo movement)
    {
        EmitCombat("TeleportApplied", "server-authoritative", guid,
            $"counter={counter};position={movement.Position.X:R}|{movement.Position.Y:R}|{movement.Position.Z:R};" +
            $"orientation={movement.Orientation:R};movementTime={movement.Timestamp};flags=0x{movement.Flags:X8}");
        ObserveHearthTeleport(guid, movement.Position);
    }

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
