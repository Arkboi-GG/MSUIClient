using System.Numerics;
using ImGuiNET;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private const float TaxiInteractDistance = 6f;
    private ulong _taxiMasterGuid;
    private uint _taxiCurrentNode;
    private readonly SortedSet<uint> _taxiKnownNodes = [];
    private bool _taxiOpen, _taxiLocked;
    private CreatureSpline? _taxiSpline;
    private Vector3 _taxiStart;

    private void ResetTaxi()
    {
        _taxiMasterGuid = 0; _taxiCurrentNode = 0; _taxiKnownNodes.Clear();
        _taxiOpen = false; _taxiLocked = false; _taxiSpline = null;
    }

    private bool TaxiMasterEligible(ulong guid, out WorldEntity? master, out float distance)
    {
        master = null; distance = float.PositiveInfinity;
        return _net is { IsInWorld: true } && _controller is not null &&
            _entities.TryGet(guid, out master) && master.IsCreature && !master.IsDead &&
            (master.NpcFlags & NpcFlightMaster) != 0 &&
            (distance = Vector3.Distance(_controller.Position, master.Position)) <= TaxiInteractDistance;
    }

    private bool RequestTaxiStatus(ulong guid)
    {
        bool eligible = TaxiMasterEligible(guid, out WorldEntity? master, out float distance);
        bool sent = eligible && _net!.TaxiNodeStatusQuery(guid);
        EmitInterface("taxi", "status-query", sent ? "SENT" : "REFUSED", guid,
            $"eligible={eligible};distance={distance:R};npcFlags=0x{master?.NpcFlags ?? 0:X8};body={Convert.ToHexString(BitConverter.GetBytes(guid))}");
        return sent;
    }

    private bool RequestTaxiMap(ulong guid)
    {
        bool eligible = TaxiMasterEligible(guid, out WorldEntity? master, out float distance);
        bool sent = eligible && _net!.TaxiQueryAvailableNodes(guid);
        if (sent) { _taxiMasterGuid = guid; _taxiOpen = true; }
        EmitInterface("taxi", "map-query", sent ? "SENT" : "REFUSED", guid,
            $"eligible={eligible};distance={distance:R};npcFlags=0x{master?.NpcFlags ?? 0:X8};body={Convert.ToHexString(BitConverter.GetBytes(guid))}");
        return sent;
    }

    private void ApplyTaxiNodeStatus(byte[] body)
    {
        if (body.Length < 9) throw new InvalidDataException($"taxi status bytes={body.Length}");
        var r = new PacketReader(body); ulong guid = r.ReadU64(); byte status = r.ReadU8();
        EmitInterface("taxi", "node-status", "RECEIVED", guid, $"status={status};known={status != 0};bytes={body.Length}");
    }

    private void ApplyTaxiNodes(byte[] body)
    {
        if (body.Length < 20) throw new InvalidDataException($"taxi map bytes={body.Length}");
        var r = new PacketReader(body); uint mode = r.ReadU32(); ulong guid = r.ReadU64(); uint current = r.ReadU32();
        _taxiMasterGuid = guid; _taxiCurrentNode = current; _taxiKnownNodes.Clear();
        int words = r.Remaining / 4;
        for (int word = 0; word < words; word++)
        {
            uint mask = r.ReadU32();
            for (int bit = 0; bit < 32; bit++) if ((mask & (1u << bit)) != 0) _taxiKnownNodes.Add((uint)(word * 32 + bit + 1));
        }
        _taxiOpen = true;
        EmitInterface("taxi", "map", "DISPLAYED", guid,
            $"mode={mode};current={current};maskWords={words};known={string.Join(',', _taxiKnownNodes)};bytes={body.Length}");
    }

    private bool ActivateTaxi(uint destination)
    {
        bool known = _taxiKnownNodes.Contains(destination), distinct = destination != _taxiCurrentNode;
        bool sent = known && distinct && _taxiMasterGuid != 0 && _net?.ActivateTaxi(_taxiMasterGuid, _taxiCurrentNode, destination) == true;
        EmitInterface("taxi", "activate", sent ? "SENT" : "REFUSED", _taxiMasterGuid,
            $"source={_taxiCurrentNode};destination={destination};known={known};distinct={distinct};body={Convert.ToHexString(WorldSession.BuildActivateTaxiBody(_taxiMasterGuid, _taxiCurrentNode, destination))}");
        return sent;
    }

    private void ApplyTaxiReply(byte[] body)
    {
        if (body.Length < 4) throw new InvalidDataException($"taxi reply bytes={body.Length}");
        uint code = BitConverter.ToUInt32(body, 0); bool accepted = code == 0;
        _taxiLocked = accepted; _taxiStart = _controller?.Position ?? Vector3.Zero;
        EmitInterface("taxi", "purchase", accepted ? "ACCEPTED" : $"REJECTED_{code}", _taxiMasterGuid,
            $"code={code};controlLocked={_taxiLocked};bytes={body.Length}");
    }

    private void ObserveTaxiSpline(MonsterMove move)
    {
        if (_net is null || move.Guid != _net.PlayerGuid || !move.Flying || move.Points.Length < 2) return;
        _taxiLocked = true; _taxiSpline = new CreatureSpline(move.Points, move.DurationMs, true, MovementInfo.ClientUptimeMs());
        _taxiStart = move.Points[0]; _taxiOpen = true;
        EmitInterface("taxi", "flight", "STARTED", move.Guid,
            $"points={move.Points.Length};durationMs={move.DurationMs};flying={move.Flying};controlLocked=true");
    }

    private void ApplyTaxiInputLockout(ref float forward, ref float strafe, ref float turn, ref bool jump)
    {
        if (!_taxiLocked) return;
        bool attempted = MathF.Abs(forward) > .01f || MathF.Abs(strafe) > .01f || MathF.Abs(turn) > .01f || jump;
        forward = strafe = turn = 0; jump = false;
        if (attempted) EmitInterface("taxi", "input", "LOCKED_OUT", _net?.PlayerGuid ?? 0, "axes=0;jump=false");
    }

    private void UpdateTaxiSpline()
    {
        if (_taxiSpline is null || _controller is null) return;
        bool running = _taxiSpline.Sample(MovementInfo.ClientUptimeMs(), out Vector3 position, out float? facing);
        _controller.Teleport(position.X, position.Y, position.Z);
        if (facing is { } yaw) { _controller.Yaw = yaw; _window.Camera.Yaw = yaw; }
        _window.Camera.Target = position;
        if (running) return;
        float distance = Vector3.Distance(_taxiStart, position); _taxiSpline = null; _taxiLocked = false;
        EmitInterface("taxi", "arrival", "HANDED_OFF", _net?.PlayerGuid ?? 0,
            $"distance={distance:R};controlLocked=false;position={position.X:R}|{position.Y:R}|{position.Z:R}");
    }

    private void SimulateTaxiFlow()
    {
        ulong guid = 0xF130000160000001;
        var map = new PacketWriter(); map.WriteU32(1); map.WriteU64(guid); map.WriteU32(2); map.WriteU32((1u << 1) | (1u << 5) | (1u << 11)); ApplyTaxiNodes(map.ToArray());
        EmitInterface("taxi", "activate", "SENT", guid, $"source=2;destination=6;known=true;distinct=true;body={Convert.ToHexString(WorldSession.BuildActivateTaxiBody(guid, 2, 6))};source=replay");
        var reply = new PacketWriter(); reply.WriteU32(0); ApplyTaxiReply(reply.ToArray());
        _taxiLocked = true; EmitInterface("taxi", "input", "LOCKED_OUT", 1, "axes=0;jump=false;source=replay");
        EmitInterface("taxi", "flight", "STARTED", 1, "points=5;durationMs=8500;flying=true;source=replay");
        _taxiLocked = false; EmitInterface("taxi", "arrival", "HANDED_OFF", 1, "distance=412.5;controlLocked=false;source=replay");
    }

    private void DrawTaxiFrame()
    {
        if (!_taxiOpen||_gameplayArt is null) return;float s=GameplayUiScale();Vector2 origin=new(0,8*s),logicalSize=new(384,512);
        ImGui.SetNextWindowPos(origin,ImGuiCond.Always);ImGui.SetNextWindowSize(logicalSize*s,ImGuiCond.Always);ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##taxi",ImGuiWindowFlags.NoDecoration|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoBackground|ImGuiWindowFlags.NoNav)) { ImGui.End(); return; }
        ImDrawListPtr dl=ImGui.GetWindowDrawList();if(_uiParityArmed&&_uiParityPanel=="taxi"){BeginUiParityFrame(origin,s);CollectUiParityDraw("TaxiFrame","Frame",origin,logicalSize*s,"",new("",0,"IMGUI_HOST","ANCHOR:ABSOLUTE","","",0,8));}
        (string Element,string Path,Vector2 Offset,Vector2 Size)[] art=[
            ("TaxiFrame/Texture",@"Interface\TaxiFrame\UI-TaxiFrame-TopLeft",Vector2.Zero,new(256,256)),
            ("TaxiFrame/Texture#2",@"Interface\TaxiFrame\UI-TaxiFrame-TopRight",new(256,0),new(128,256)),
            ("TaxiFrame/Texture#3",@"Interface\TaxiFrame\UI-TaxiFrame-BotLeft",new(0,256),new(256,256)),
            ("TaxiFrame/Texture#4",@"Interface\TaxiFrame\UI-TaxiFrame-BotRight",new(256,256),new(128,256))];
        foreach(var r in art){Vector2 m=origin+r.Offset*s;DrawArt(dl,r.Path,m,r.Size,s);if(_uiParityArmed&&_uiParityPanel=="taxi")CollectUiParityDraw(r.Element,"Texture",m,r.Size*s,"TaxiFrame",new(r.Path,0xffffffff,"IMGUI_IMAGE","TOPLEFT","TaxiFrame","TOPLEFT",r.Offset.X,-r.Offset.Y));}
        ImGui.SetCursorScreenPos(origin+new Vector2(50,82)*s);ImGui.BeginChild("##taxi-content",new Vector2(270,300)*s,false);
        ImGui.TextColored(new Vector4(1f,.82f,0f,1f), $"Current node: {_taxiCurrentNode}");
        ImGui.TextDisabled(_taxiLocked ? "In flight — movement controls locked" : "Choose a discovered destination");
        ImGui.Separator();
        foreach (uint node in _taxiKnownNodes)
        {
            bool current = node == _taxiCurrentNode;
            if (current) ImGui.TextDisabled($"● Node {node} (current)");
            else if (ImGui.Button($"Fly to node {node}##taxi-{node}")) ActivateTaxi(node);
        }
        if (_taxiKnownNodes.Count == 0) ImGui.TextDisabled("No discovered flight nodes received.");
        ImGui.EndChild();Vector2 close=origin+new Vector2(323,8)*s;DrawImageButton(dl,"##taxi-close",close,new Vector2(32)*s,@"Interface\Buttons\UI-Panel-MinimizeButton-Up",@"Interface\Buttons\UI-Panel-MinimizeButton-Down",@"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");if(ImGui.IsItemClicked())_taxiOpen=false;
        if(_uiParityArmed&&_uiParityPanel=="taxi")MarkUiParityFrameComplete();
        ImGui.End();
    }
}
