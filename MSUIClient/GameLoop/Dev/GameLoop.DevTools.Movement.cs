using System.Globalization;
using System.Text;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Player;
using MSUIClient.World.Units;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private const string MovementTraceHeader =
        "frame,t,dt,posX,posY,posZ,velX,velY,velZ,horizSpeed,aimYaw,bodyYaw," +
        "inputFlags,grounded,verticalVel,fallTimeMs,clipId,clipName,clipTime," +
        "clipB,clipBTime,blendWeight,playbackRate,lastAnimChoice,wireSentThisTick," +
        // Ground resolution, so a trace can answer "why did it fall" on its own.
        // Reading grounded+posZ alone cannot separate the three causes that look
        // identical in a plot: no triangle under the feet, a triangle rejected
        // as too steep to stand on, and support lost to a probe that never
        // sampled the footprint. These columns name which one it was.
        "groundSource,terrainZ,terrainNx,terrainNy,terrainNz,terrainSteep,terrainImpassable," +
        "collisionZ,groundTri,groundModel,groundProbes," +
        "groundAdhesion,noGroundBelow,inTerrainHole";

    private StreamWriter? _movementTraceWriter;
    private string _movementTraceName = "manual";
    private string _movementTracePath = "";
    private int _movementTraceFrame;
    private double _movementTraceTime;
    private bool? _movementTraceGrounded;
    private string _movementTraceGait = "";
    private long _movementTraceClipSequence;
    private double _movementTraceCopiedUntil;

    private bool MovementTraceActive => _movementTraceWriter is not null;

    private void StartMovementTrace(string name, bool exactPath = false)
    {
        StopMovementTrace();
        string safe = string.Concat((string.IsNullOrWhiteSpace(name) ? "manual" : name.Trim())
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-'));
        if (safe.Length == 0) safe = "manual";
        string directory = exactPath ? Path.GetDirectoryName(name)! : Path.Combine(_config.RepoRoot, "dumps");
        Directory.CreateDirectory(directory);
        _movementTracePath = exactPath ? name : Path.Combine(directory, $"movetrace-{safe}.csv");
        _movementTraceWriter = new StreamWriter(_movementTracePath, append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _movementTraceWriter.WriteLine(MovementTraceHeader);
        _movementTraceFrame = 0;
        _movementTraceTime = 0;
        _movementTraceGrounded = null;
        _movementTraceGait = "";
        _movementTraceClipSequence = _character?.LastClipTransition.Sequence ?? 0;
        Console.WriteLine($"[move-trace] recording {_movementTracePath}");
    }

    private void StopMovementTrace()
    {
        if (_movementTraceWriter is null) return;
        _movementTraceWriter.Flush();
        _movementTraceWriter.Dispose();
        _movementTraceWriter = null;
        Console.WriteLine($"[move-trace] wrote {_movementTraceFrame} tick(s) to {_movementTracePath}");
    }

    private void SampleMovementTrace(float dt, in MovementInput input, float turn)
    {
        if (_movementTraceWriter is null || _controller is null) return;

        string flags = MovementInputFlags(input, turn);
        string gait = _character?.ClipName ?? "none";
        if (_movementTraceGrounded is bool wasGrounded && wasGrounded != _controller.Grounded)
            EmitMoveVerdict(new MoveVerdict(
                NowSeconds(), MoveTransitionKind.GroundState,
                wasGrounded ? "grounded" : "airborne",
                _controller.Grounded ? "grounded" : "airborne",
                _character?.ClipId ?? -1, _character?.ClipId ?? -1,
                _character?.ClipTime ?? 0f));
        _movementTraceGrounded = _controller.Grounded;

        if (_movementTraceGait.Length > 0 && !string.Equals(
                _movementTraceGait, gait, StringComparison.Ordinal))
            EmitMoveVerdict(new MoveVerdict(
                NowSeconds(), MoveTransitionKind.Gait, _movementTraceGait, gait,
                -1, _character?.ClipId ?? -1, _character?.ClipTime ?? 0f));
        _movementTraceGait = gait;

        if (_character is { } character &&
            character.LastClipTransition.Sequence != _movementTraceClipSequence)
        {
            CharacterRenderer.ClipTransition cut = character.LastClipTransition;
            _movementTraceClipSequence = cut.Sequence;
            EmitMoveVerdict(new MoveVerdict(
                NowSeconds(), MoveTransitionKind.Clip,
                cut.FromName, cut.ToName, cut.FromId, cut.ToId, cut.OutgoingTime));
        }

        string lastAnim = "NONE";
        if (_lastAnimChoices.TryGetValue(("player", 0), out var choice))
            lastAnim = string.Create(CultureInfo.InvariantCulture,
                $"{choice.Requested}->{choice.Played}:{choice.Kind}");
        string wire = _movementSender.LastUpdateOpcodes.Count == 0
            ? "NONE"
            : string.Join('|', _movementSender.LastUpdateOpcodes);
        var p = _controller.Position;
        var v = _controller.Velocity;
        _movementTraceWriter.WriteLine(string.Join(',',
            _movementTraceFrame.ToString(CultureInfo.InvariantCulture),
            _movementTraceTime.ToString("F6", CultureInfo.InvariantCulture),
            dt.ToString("F6", CultureInfo.InvariantCulture),
            p.X.ToString("R", CultureInfo.InvariantCulture),
            p.Y.ToString("R", CultureInfo.InvariantCulture),
            p.Z.ToString("R", CultureInfo.InvariantCulture),
            v.X.ToString("R", CultureInfo.InvariantCulture),
            v.Y.ToString("R", CultureInfo.InvariantCulture),
            v.Z.ToString("R", CultureInfo.InvariantCulture),
            _controller.PlanarSpeed.ToString("R", CultureInfo.InvariantCulture),
            _controller.Yaw.ToString("R", CultureInfo.InvariantCulture),
            (_character?.BodyYawRadians ?? _controller.Yaw).ToString("R", CultureInfo.InvariantCulture),
            Csv(flags),
            _controller.Grounded.ToString().ToLowerInvariant(),
            _controller.Velocity.Z.ToString("R", CultureInfo.InvariantCulture),
            _controller.FallTimeMs.ToString("R", CultureInfo.InvariantCulture),
            (_character?.ClipId ?? -1).ToString(CultureInfo.InvariantCulture),
            Csv(gait),
            (_character?.ClipTime ?? 0f).ToString("R", CultureInfo.InvariantCulture),
            Csv(_character?.BlendFrom ?? ""),
            _character is { BlendFrom.Length: > 0 } blendCharacter
                ? blendCharacter.BlendFromTime.ToString("R", CultureInfo.InvariantCulture) : "",
            _character is { BlendFrom.Length: > 0 } weightCharacter
                ? weightCharacter.IncomingBlendWeight.ToString("R", CultureInfo.InvariantCulture) : "",
            (_character?.ClipRate ?? 0f).ToString("R", CultureInfo.InvariantCulture),
            Csv(lastAnim),
            Csv(wire),
            Csv(_controller.GroundSource),
            _controller.TerrainGroundZ is float traceTerrainZ
                ? traceTerrainZ.ToString("R", CultureInfo.InvariantCulture) : "",
            _controller.TerrainGroundNormal.X.ToString("R", CultureInfo.InvariantCulture),
            _controller.TerrainGroundNormal.Y.ToString("R", CultureInfo.InvariantCulture),
            _controller.TerrainGroundNormal.Z.ToString("R", CultureInfo.InvariantCulture),
            _controller.TerrainGroundSteep.ToString().ToLowerInvariant(),
            _controller.TerrainChunkImpassable.ToString().ToLowerInvariant(),
            _controller.CollisionGroundZ is float traceCollisionZ
                ? traceCollisionZ.ToString("R", CultureInfo.InvariantCulture) : "",
            _controller.GroundTriangle.ToString(CultureInfo.InvariantCulture),
            Csv(_controller.GroundTriangle >= 0 && _collision is not null
                ? _collision.SourceOf(_controller.GroundTriangle) : ""),
            _controller.GroundProbesLastFrame.ToString(CultureInfo.InvariantCulture),
            _controller.GroundAdhesion.ToString().ToLowerInvariant(),
            _controller.NoGroundBelow.ToString().ToLowerInvariant(),
            _controller.InTerrainHole.ToString().ToLowerInvariant()));
        _movementTraceFrame++;
        _movementTraceTime += dt;
        _movementTraceWriter.Flush();
    }

    private void EmitMoveVerdict(in MoveVerdict verdict)
    {
        _verdicts.Add(verdict);
        Console.WriteLine($"[verdict:move] {verdict.ToLine()}");
    }

    private static string MovementInputFlags(in MovementInput input, float turn)
    {
        var flags = new List<string>(8);
        if (input.Forward > 0.01f) flags.Add("fwd");
        if (input.Forward < -0.01f) flags.Add("back");
        if (input.Strafe < -0.01f) flags.Add("strafeL");
        if (input.Strafe > 0.01f) flags.Add("strafeR");
        if (turn > 0.01f) flags.Add("turnL");
        if (turn < -0.01f) flags.Add("turnR");
        if (input.Jump) flags.Add("jump");
        return flags.Count == 0 ? "none" : string.Join('|', flags);
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    private void DrawMovementInstrumentsPanel()
    {
        if (!ImGui.CollapsingHeader("Movement instruments")) return;
        ImGui.SetNextItemWidth(180f);
        ImGui.InputText("Trace name##movement", ref _movementTraceName, 64u);
        if (!MovementTraceActive)
        {
            if (ImGui.Button("Start trace##movement")) StartMovementTrace(_movementTraceName);
        }
        else if (ImGui.Button("Stop trace##movement"))
        {
            StopMovementTrace();
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(MovementTraceActive
            ? $"recording {_movementTraceFrame} ticks"
            : _movementTracePath.Length == 0 ? "idle" : $"last: {_movementTraceFrame} ticks");
        if (_movementTracePath.Length > 0)
        {
            ImGui.TextWrapped(_movementTracePath);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left) ||
                ImGui.Button("Copy trace path##movement"))
            {
                ImGui.SetClipboardText(_movementTracePath);
                _movementTraceCopiedUntil = NowSeconds() + 0.8;
            }
            if (NowSeconds() < _movementTraceCopiedUntil)
            {
                ImGui.SameLine();
                ImGui.TextColored(new System.Numerics.Vector4(0.45f, 1f, 0.55f, 1f), "copied");
            }
        }
    }
}
