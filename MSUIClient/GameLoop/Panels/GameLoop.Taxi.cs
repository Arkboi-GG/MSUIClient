using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private ulong _taxiMasterGuid;
    private uint _taxiCurrentNode;
    private readonly SortedSet<uint> _taxiKnownNodes = [];
    private readonly Dictionary<ulong, bool> _taxiNodeKnown = [];
    private readonly HashSet<ulong> _taxiStatusAsked = [];
    private ulong _taxiStatusLastHover;
    private bool _taxiOpen, _taxiLocked;
    private CreatureSpline? _serverRideSpline;
    private uint? _serverRideStoppedId;
    private Vector3 _serverRideStart;
    private TaxiNodeCatalog? _taxiNodes;
    private TaxiPathCatalog? _taxiPaths;
    private TaxiContinentCatalog? _taxiContinents;
    private TaxiRouteView[] _taxiRoutes = [];
    private bool _taxiNodesLoaded;

    private void ResetTaxi()
    {
        _taxiMasterGuid = 0; _taxiCurrentNode = 0; _taxiKnownNodes.Clear();
        _taxiNodeKnown.Clear(); _taxiStatusAsked.Clear(); _taxiStatusLastHover = 0;
        _taxiOpen = false; _taxiLocked = false; _serverRideSpline = null;
        _serverRideStoppedId = null;
        _taxiRoutes = [];
    }

    private bool TaxiMasterEligible(ulong guid, out WorldEntity? master, out float distance)
    {
        master = null;
        distance = float.PositiveInfinity;
        if (_net is not { IsInWorld: true } || _controller is null ||
            !_entities.TryGet(guid, out master) || !master.IsCreature || master.IsDead ||
            (master.NpcFlags & NpcFlightMaster) == 0)
            return false;
        Vector3 delta = _controller.Position - master.Position;
        distance = delta.Length();
        return NpcSessionUiLaw.InRange(delta.LengthSquared());
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
        if (sent) _taxiMasterGuid = guid;
        EmitInterface("taxi", "map-query", sent ? "SENT" : "REFUSED", guid,
            $"eligible={eligible};distance={distance:R};npcFlags=0x{master?.NpcFlags ?? 0:X8};body={Convert.ToHexString(BitConverter.GetBytes(guid))}");
        return sent;
    }

    private void ApplyTaxiNodeStatus(byte[] body)
    {
        TaxiNodeStatusPacket packet = TaxiPackets.ParseNodeStatus(body);
        _taxiNodeKnown[packet.FlightMasterGuid] = packet.Known;
        EmitInterface("taxi", "node-status", "RECEIVED", packet.FlightMasterGuid,
            $"known={packet.Known};bytes={body.Length}");
    }

    /// <summary>
    /// Current Benilla's unit-refresh plus mouseover query triggers. An unknown node feeds the
    /// green TalkToMe marker through the shared overhead marker renderer.
    /// </summary>
    private void UpdateTaxiNodeStatusQueries()
    {
        if (_net is not { IsInWorld: true } net) return;
        HashSet<ulong> visible = _entities.Entities.Values
            .Where(unit => unit.IsCreature && !unit.IsDead &&
                (unit.NpcFlags & NpcFlightMaster) != 0 && !CanAttack(unit))
            .Select(unit => unit.Guid)
            .ToHashSet();
        _taxiStatusAsked.RemoveWhere(guid => !visible.Contains(guid));
        foreach (ulong guid in _taxiNodeKnown.Keys.Where(guid => !visible.Contains(guid)).ToArray())
            _taxiNodeKnown.Remove(guid);

        ulong hoveredFlightMaster = visible.Contains(_hoveredGuid) ? _hoveredGuid : 0;
        if (hoveredFlightMaster != _taxiStatusLastHover && hoveredFlightMaster != 0)
            _taxiStatusAsked.Remove(hoveredFlightMaster);
        _taxiStatusLastHover = hoveredFlightMaster;

        foreach (ulong guid in visible)
            if (_taxiStatusAsked.Add(guid)) net.TaxiNodeStatusQuery(guid);
    }

    private void ApplyTaxiNodes(byte[] body)
    {
        ShowTaxiNodesPacket packet = TaxiPackets.ParseShowNodes(body);
        if (packet.Gate == 0)
        {
            EmitInterface("taxi", "map", "GATED_OFF", 0, $"bytes={body.Length}");
            return;
        }
        bool switched = _taxiOpen && _taxiMasterGuid != packet.FlightMasterGuid;
        if (switched) CloseTaxiMap(playSound: true);
        _taxiMasterGuid = packet.FlightMasterGuid;
        _taxiCurrentNode = packet.NearestNode;
        _taxiKnownNodes.Clear();
        for (int word = 0; word < packet.KnownMask.Length; word++)
        {
            uint mask = packet.KnownMask[word];
            for (int bit = 0; bit < 32; bit++) if ((mask & (1u << bit)) != 0) _taxiKnownNodes.Add((uint)(word * 32 + bit + 1));
        }
        EnsureTaxiCatalogs();
        RebuildTaxiRoutes();
        bool catalogsReady = _taxiNodes is not null && _taxiPaths is not null &&
            _taxiContinents is not null;
        bool hasVisibleDestination = catalogsReady
            ? _taxiRoutes.Any(route => !route.Current)
            : _taxiKnownNodes.Any(node => node != _taxiCurrentNode);
        bool hasOneHop = catalogsReady
            ? _taxiRoutes.Any(route => !route.Current && route.Segments.Length == 1)
            : hasVisibleDestination;
        if (!_taxiOpen)
        {
            _taxiOpen = true;
            PlayUiSound(TaxiFrameUiLaw.OpenSound, TaxiFrameUiLaw.SoundCategory);
        }
        if (!hasOneHop)
        {
            CloseTaxiMap(playSound: true);
            ShowUiError(TaxiFrameUiLaw.NoConnectedFlightPaths);
        }
        EmitInterface("taxi", "map", "DISPLAYED", packet.FlightMasterGuid,
            $"gate={packet.Gate};current={packet.NearestNode};maskWords={packet.KnownMask.Length};hasVisibleDestination={hasVisibleDestination};hasOneHop={hasOneHop};visibleRoutes={_taxiRoutes.Length};known={string.Join(',', _taxiKnownNodes)};bytes={body.Length}");
    }

    private void ApplyNewTaxiPath(byte[] body)
    {
        TaxiPackets.RequireNewPathBody(body);
        ShowUiInfo(TaxiFrameUiLaw.DiscoveredText);
        PlayUiSound(TaxiFrameUiLaw.DiscoveredSound, TaxiFrameUiLaw.SoundCategory);
        EmitInterface("taxi", "discover", "DISPLAYED", _taxiMasterGuid,
            $"text={TaxiFrameUiLaw.DiscoveredText};sound={TaxiFrameUiLaw.DiscoveredSound}");
    }

    private bool CloseTaxiMap(bool playSound = true)
    {
        if (!_taxiOpen) return false;
        _taxiOpen = false;
        if (playSound) PlayUiSound(TaxiFrameUiLaw.CloseSound, TaxiFrameUiLaw.SoundCategory);
        EmitInterface("taxi", "close", "CLOSED", _taxiMasterGuid, $"sound={playSound}");
        return true;
    }

    private bool ActivateTaxi(uint destination)
    {
        TaxiRouteView route = _taxiRoutes.FirstOrDefault(candidate => candidate.Node.Id == destination);
        bool known = _taxiKnownNodes.Contains(destination), distinct = destination != _taxiCurrentNode;
        bool resolved = route.Node.Id != 0 && route.Chain.Length >= 2;
        bool direct = resolved && _taxiPaths?.TryBetween(
            _taxiCurrentNode, destination, out _) == true;
        bool sent = false;
        byte[] body = [];
        string wire = "none";
        if (known && distinct && resolved && _taxiMasterGuid != 0 && _net is not null)
        {
            if (direct)
            {
                body = WorldSession.BuildActivateTaxiBody(
                    _taxiMasterGuid, _taxiCurrentNode, destination);
                sent = _net.ActivateTaxi(_taxiMasterGuid, _taxiCurrentNode, destination);
                wire = "CMSG_ACTIVATETAXI";
            }
            else
            {
                body = TaxiPackets.BuildActivateExpressBody(
                    _taxiMasterGuid, route.Fare, route.Chain);
                sent = _net.ActivateTaxiExpress(_taxiMasterGuid, route.Fare, route.Chain);
                wire = "CMSG_ACTIVATETAXIEXPRESS";
            }
        }
        EmitInterface("taxi", "activate", sent ? "SENT" : "REFUSED", _taxiMasterGuid,
            $"source={_taxiCurrentNode};destination={destination};known={known};distinct={distinct};resolved={resolved};direct={direct};fare={route.Fare};chain={string.Join(',', route.Chain)};wire={wire};body={Convert.ToHexString(body)}");
        return sent;
    }

    private void ApplyTaxiReply(byte[] body)
    {
        uint code = TaxiPackets.ParseActivateReply(body); bool accepted = code == 0;
        _taxiLocked = accepted; _serverRideStart = _controller?.Position ?? Vector3.Zero;
        if (accepted) CloseTaxiMap(playSound: true);
        else if (TaxiFrameUiLaw.ActivateErrorText(code) is { } error) ShowUiError(error);
        EmitInterface("taxi", "purchase", accepted ? "ACCEPTED" : $"REJECTED_{code}", _taxiMasterGuid,
            $"code={code};controlLocked={_taxiLocked};bytes={body.Length}");
    }

    private void ObserveServerRideSpline(MonsterMove move)
    {
        if (_net is null || move.Guid != _net.PlayerGuid) return;
        if (move.Stop || move.DurationMs == 0 || move.Points.Length < 2)
        {
            _serverRideSpline = null;
            _serverRideStoppedId = move.SplineId;
            return;
        }

        _serverRideStoppedId = null;
        _serverRideSpline = new CreatureSpline(move.Points, move.DurationMs, move.Flying,
            MovementInfo.ClientUptimeMs(), move.SplineId);
        _serverRideStart = move.Points[0];
        if (move.Flying) _taxiLocked = true;
        EmitInterface("taxi", "flight", "STARTED", move.Guid,
            $"points={move.Points.Length};durationMs={move.DurationMs};" +
            $"flying={move.Flying};splineId={move.SplineId};controlLocked=true");
    }

    private bool UpdateTaxiLifecycle()
    {
        if (!_taxiOpen || _taxiLocked || _controller is null) return false;
        bool sourceAvailable = _entities.TryGet(_taxiMasterGuid, out WorldEntity master);
        float distanceSquared = sourceAvailable
            ? Vector3.DistanceSquared(_controller.Position, master.Position)
            : float.PositiveInfinity;
        if (!NpcSessionUiLaw.ShouldClose(true, true, sourceAvailable, distanceSquared))
            return false;
        CloseTaxiMap(playSound: true);
        EmitInterface("taxi", "lifecycle-close", "CLOSED", _taxiMasterGuid,
            sourceAvailable
                ? $"distanceSquared={distanceSquared:R};limitSquared={NpcSessionUiLaw.ServiceRangeSquared:R}"
                : "source-despawned");
        return true;
    }

    private void ApplyTaxiInputLockout(ref float forward, ref float strafe, ref float turn, ref bool jump)
    {
        if (!_taxiLocked && _serverRideSpline is null && _serverRideStoppedId is null) return;
        bool attempted = MathF.Abs(forward) > .01f || MathF.Abs(strafe) > .01f || MathF.Abs(turn) > .01f || jump;
        forward = strafe = turn = 0; jump = false;
        if (attempted) EmitInterface("taxi", "input", "LOCKED_OUT", _net?.PlayerGuid ?? 0, "axes=0;jump=false");
    }

    private bool UpdateServerRide()
    {
        if (_controller is null) return false;
        if (_serverRideStoppedId is { } stoppedId)
        {
            _serverRideStoppedId = null;
            _taxiLocked = false;
            AcknowledgeServerRide(stoppedId, _controller.Position, _controller.Yaw);
            return false;
        }
        if (_serverRideSpline is not { } ride) return false;

        bool running = ride.Sample(MovementInfo.ClientUptimeMs(), out Vector3 position,
            out float? facing);
        _controller.Teleport(position.X, position.Y, position.Z);
        if (facing is { } yaw) { _controller.Yaw = yaw; _window.Camera.Yaw = yaw; }
        _window.Camera.Target = position;
        if (running) return true;

        float distance = Vector3.Distance(_serverRideStart, position);
        uint completedId = ride.Id;
        _serverRideSpline = null;
        _taxiLocked = false;
        AcknowledgeServerRide(completedId, position, _controller.Yaw);
        EmitInterface("taxi", "arrival", "HANDED_OFF", _net?.PlayerGuid ?? 0,
            $"distance={distance:R};splineId={completedId};controlLocked=false;" +
            $"position={position.X:R}|{position.Y:R}|{position.Z:R}");
        return false;
    }

    private void AcknowledgeServerRide(uint splineId, Vector3 position, float orientation)
    {
        var movement = new MovementInfo
        {
            Flags = 0,
            Timestamp = MovementInfo.ClientUptimeMs(),
            Position = position,
            Orientation = orientation,
            FallTime = 0,
        };
        bool sent = _net?.MoveSplineDone(movement, splineId) == true;
        EmitInterface("movement", "spline-done", sent ? "SENT" : "REFUSED",
            _net?.PlayerGuid ?? 0, $"splineId={splineId};body=" +
            Convert.ToHexString(WorldSession.BuildMoveSplineDoneBody(movement, splineId)));
    }

    private void AbortServerRideForTeleport()
    {
        _serverRideSpline = null;
        _serverRideStoppedId = null;
        _taxiLocked = false;
    }

    private void SimulateTaxiFlow()
    {
        ulong guid = 0xF130000160000001;
        var map = new PacketWriter(); map.WriteU32(1); map.WriteU64(guid); map.WriteU32(2);
        map.WriteU32((1u << 1) | (1u << 5) | (1u << 11));
        for (int i = 1; i < TaxiPackets.MaskWords; i++) map.WriteU32(0);
        ApplyTaxiNodes(map.ToArray());
        EmitInterface("taxi", "activate", "SENT", guid, $"source=2;destination=6;known=true;distinct=true;body={Convert.ToHexString(WorldSession.BuildActivateTaxiBody(guid, 2, 6))};source=replay");
        var reply = new PacketWriter(); reply.WriteU32(0); ApplyTaxiReply(reply.ToArray());
        _taxiLocked = true; EmitInterface("taxi", "input", "LOCKED_OUT", 1, "axes=0;jump=false;source=replay");
        EmitInterface("taxi", "flight", "STARTED", 1, "points=5;durationMs=8500;flying=true;source=replay");
        _taxiLocked = false; EmitInterface("taxi", "arrival", "HANDED_OFF", 1, "distance=412.5;controlLocked=false;source=replay");
    }

    private void DrawTaxiFrame()
    {
        if (!_taxiOpen||_gameplayArt is null) return;float s=GameplayUiScale();Vector2 origin=UiPanelFrameOrigin(UiPanelOwnershipRegistry[6], s),size=TaxiFrameUiLaw.FrameSize(s);
        ImGui.SetNextWindowPos(origin,ImGuiCond.Always);ImGui.SetNextWindowSize(size,ImGuiCond.Always);ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##taxi",ImGuiWindowFlags.NoDecoration|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoBackground|ImGuiWindowFlags.NoNav)) { ImGui.End(); return; }
        ImDrawListPtr dl=ImGui.GetWindowDrawList();if(_uiParityArmed&&_uiParityPanel=="taxi"){BeginUiParityFrame(origin,s);CollectUiParityDraw("TaxiFrame","Frame",origin,size,"",new("",0,"IMGUI_HOST","ANCHOR:ABSOLUTE","","",0,8));}
        if (_entities.TryGet(_taxiMasterGuid, out WorldEntity portraitMaster))
            DrawUnitPortraitImage(dl, portraitMaster,
                origin + TaxiFrameUiLaw.PortraitOffset * s,
                TaxiFrameUiLaw.PortraitSize * s, 0, false);
        string[] elements = ["TaxiFrame/Texture", "TaxiFrame/Texture#2",
            "TaxiFrame/Texture#3", "TaxiFrame/Texture#4"];
        string[] paths = [@"Interface\TaxiFrame\UI-TaxiFrame-TopLeft",
            @"Interface\TaxiFrame\UI-TaxiFrame-TopRight",
            @"Interface\TaxiFrame\UI-TaxiFrame-BotLeft",
            @"Interface\TaxiFrame\UI-TaxiFrame-BotRight"];
        for (int index = 0; index < TaxiFrameUiLaw.ShellPieces.Length; index++)
        {
            TaxiFrameUiLaw.LogicalRect piece = TaxiFrameUiLaw.ShellPieces[index];
            Vector2 minimum = piece.ScaledMin(origin, s);
            DrawArt(dl, paths[index], minimum, piece.Size, s);
            if (_uiParityArmed && _uiParityPanel == "taxi")
                CollectUiParityDraw(elements[index], "Texture", minimum, piece.ScaledSize(s),
                    "TaxiFrame", new(paths[index], 0xffffffff, "IMGUI_IMAGE", "TOPLEFT",
                        "TaxiFrame", "TOPLEFT", piece.X, -piece.Y));
        }
        if(_gameplayArt is not null)
        {
            DrawVanillaTaxiMap(dl,origin,s);
            if(_uiParityArmed&&_uiParityPanel=="taxi")MarkUiParityFrameComplete();
            ImGui.End();return;
        }
        ImGui.SetCursorScreenPos(TaxiFrameUiLaw.FallbackContent.ScaledMin(origin, s));
        ImGui.BeginChild("##taxi-content", TaxiFrameUiLaw.FallbackContent.ScaledSize(s), false);
        ImGui.TextColored(new Vector4(1f,.82f,0f,1f), $"Current node: {_taxiCurrentNode}");
        ImGui.TextDisabled(_taxiLocked ? "In flight — movement controls locked" : "Choose a discovered destination");
        ImGui.Separator();
        foreach (uint node in _taxiKnownNodes)
        {
            bool current = node == _taxiCurrentNode;
            if (current) ImGui.TextDisabled($"• Node {node} (current)");
            else if (ImGui.Button($"Fly to node {node}##taxi-{node}")) ActivateTaxi(node);
        }
        if (_taxiKnownNodes.Count == 0) ImGui.TextDisabled("No discovered flight nodes received.");
        ImGui.EndChild();
        Vector2 close = TaxiFrameUiLaw.Close.ScaledMin(origin, s);
        DrawImageButton(dl, "##taxi-close", close, TaxiFrameUiLaw.Close.ScaledSize(s),
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if(ImGui.IsItemClicked())CloseTaxiMap();
        if(_uiParityArmed&&_uiParityPanel=="taxi")MarkUiParityFrameComplete();
        ImGui.End();
    }

    private void DrawVanillaTaxiMap(ImDrawListPtr dl,Vector2 origin,float s)
    {
        EnsureTaxiNodes();
        uint mapId=_taxiNodes?.TryGet(_taxiCurrentNode,out TaxiNodeInfo currentInfo)==true?currentInfo.MapId:0;
        string merchant="Flight Master";
        if(_entities.TryGet(_taxiMasterGuid,out WorldEntity master))
        {
            if(master.Entry!=0&&TryBeginCreatureQuery(master.Entry))
                _net?.CreatureQuery(master.Entry,master.Guid);
            merchant=_creatureNames.GetValueOrDefault(master.Entry,"Flight Master");
        }
        float titleEm=GameText.EmPixels("GameFontNormal",s);
        DrawNpcModalTitle(dl, merchant, TaxiFrameUiLaw.TitleCenter(origin, s, titleEm), s);
        EnsureTaxiCatalogs();
        RebuildTaxiRoutes();
        Vector2 mapMin=origin+TaxiFrameUiLaw.MapOffset*s,mapSize=TaxiFrameUiLaw.MapSize*s;
        uint map=_gameplayArt!.Handle($@"Interface\TaxiFrame\TAXIMAP{mapId}.blp");
        if(map==0)map=_gameplayArt.Handle($@"textures\TaxiMaps\TaxiMap0{mapId}.blp");
        if(map!=0)dl.AddImage((nint)map,mapMin,mapMin+mapSize);
        TaxiRouteView? hoveredRoute = null;
        Vector2 mouse = ImGui.GetIO().MousePos;
        foreach (TaxiRouteView route in _taxiRoutes)
        {
            Vector2 center = TaxiFrameUiLaw.NodeCenter(route.Position, mapMin, s);
            float hit = TaxiFrameUiLaw.NodeHighlightSize * s * .5f;
            if (MathF.Abs(mouse.X - center.X) <= hit && MathF.Abs(mouse.Y - center.Y) <= hit)
                hoveredRoute = route;
        }

        IEnumerable<Vector4> visibleSegments = hoveredRoute is { Current: false } hoveredNode
            ? hoveredNode.Segments
            : _taxiRoutes.Where(route => !route.Current && route.Segments.Length == 1)
                .Select(route => route.Segments[0]);
        foreach (Vector4 segment in visibleSegments)
            DrawTaxiRouteLine(dl, mapMin, segment, s);

        foreach(TaxiRouteView route in _taxiRoutes)
        {
            Vector2 center=TaxiFrameUiLaw.NodeCenter(route.Position,mapMin,s);
            uint icon=_gameplayArt.Handle(route.Current
                ? TaxiFrameUiLaw.CurrentIcon : TaxiFrameUiLaw.ReachableIcon);
            Vector2 half = TaxiFrameUiLaw.NodeHalf(TaxiFrameUiLaw.NodeSize, s);
            if(icon!=0)dl.AddImage((nint)icon,center-half,center+half);
            ImGui.SetCursorScreenPos(center-half);ImGui.InvisibleButton($"##taxi-{route.Node.Id}",half*2);
            bool isHovered=ImGui.IsItemHovered();
            if(isHovered)
            {
                uint highlight=_gameplayArt.Handle(TaxiFrameUiLaw.HighlightIcon);
                Vector2 highlightHalf = TaxiFrameUiLaw.NodeHalf(
                    TaxiFrameUiLaw.NodeHighlightSize, s);
                if(highlight!=0)dl.AddImage((nint)highlight,center-highlightHalf,center+highlightHalf);
                OfferTaxiNodeTooltip(route,center+half);
            }
            if(!route.Current&&!_taxiLocked&&ImGui.IsItemClicked())ActivateTaxi(route.Node.Id);
        }
        if(!_taxiRoutes.Any(route=>!route.Current&&route.Segments.Length==1))
            DrawCenteredText(dl,mapMin+mapSize*.5f,TaxiFrameUiLaw.NoConnectedFlightPaths,11*s,0xffffffff);
        Vector2 close = TaxiFrameUiLaw.Close.ScaledMin(origin, s);
        DrawImageButton(dl,"##taxi-close-shipping",close,TaxiFrameUiLaw.Close.ScaledSize(s),
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up",@"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if(ImGui.IsItemClicked()&&!_taxiLocked)CloseTaxiMap();
    }

    private void DrawTaxiRouteLine(
        ImDrawListPtr draw, Vector2 mapMinimum, Vector4 normalized, float scale)
    {
        Vector2 source = TaxiFrameUiLaw.NodeCenter(
            TaxiFrameUiLaw.SegmentSource(normalized), mapMinimum, scale);
        Vector2 destination = TaxiFrameUiLaw.NodeCenter(
            TaxiFrameUiLaw.SegmentDestination(normalized), mapMinimum, scale);
        TaxiFrameUiLaw.RouteQuad quad = TaxiFrameUiLaw.RouteLine(source, destination, scale);
        uint texture = _gameplayArt?.Handle(TaxiFrameUiLaw.RouteTexture) ?? 0;
        if (texture != 0)
            draw.AddImageQuad((nint)texture, quad.A, quad.B, quad.C, quad.D,
                TaxiFrameUiLaw.RouteUvA, TaxiFrameUiLaw.RouteUvB,
                TaxiFrameUiLaw.RouteUvC, TaxiFrameUiLaw.RouteUvD);
        else
            draw.AddLine(source, destination, 0xffc0c0c0,
                TaxiFrameUiLaw.RouteWidth * scale);
    }

    private bool OfferTaxiNodeTooltip(in TaxiRouteView route, Vector2 ownerTopRight)
    {
        if (!_sharedTooltipFrameOpen || _sharedTooltipFrameResolved || _skin is null) return false;
        GameTooltipOwnerToken token = ClaimSharedGameTooltip(
            new("taxi-node", route.Node.Id));
        GameTooltipLine[] lines = route.Current
            ? [new(route.Node.Name, GameTooltipTextTone.White),
               new("You are here", GameTooltipTextTone.Green)]
            : [new(route.Node.Name, GameTooltipTextTone.White)];
        if (!PublishSharedGameTooltip(token,
                new GameTooltipContent(GameTooltipAnchorKind.OwnerRight, lines))) return false;
        if (!route.Current && !SetSharedGameTooltipMoney(token, route.Fare)) return false;
        PreparedSharedGameTooltipRenderer? prepared =
            PrepareSharedGameTooltipRenderer(SharedGameTooltipSnapshot(), ownerTopRight);
        return prepared is not null && QueueSharedGameTooltipRenderer(token,
            SharedGameTooltipLeavePolicy.ImmediateHide,
            () => DrawPreparedSharedGameTooltip(prepared));
    }

    private void EnsureTaxiCatalogs()
    {
        if(_taxiNodesLoaded)return;_taxiNodesLoaded=true;
        try
        {
            if(_mpq is not null)
            {
                _taxiNodes=TaxiNodeCatalog.Load(_mpq);
                _taxiPaths=TaxiPathCatalog.Load(_mpq);
                _taxiContinents=TaxiContinentCatalog.Load(_mpq);
            }
        }
        catch(Exception e){Console.WriteLine($"[taxi] taxi catalogs load failed: {e.Message}");}
    }

    private void EnsureTaxiNodes() => EnsureTaxiCatalogs();

    private void RebuildTaxiRoutes()
    {
        if (_taxiNodes is null || _taxiPaths is null || _taxiContinents is null ||
            !_taxiNodes.TryGet(_taxiCurrentNode, out TaxiNodeInfo current) ||
            !_taxiContinents.TryGet(current.MapId, out TaxiContinentInfo continent))
        {
            _taxiRoutes = [];
            return;
        }
        _taxiRoutes = TaxiRoutePlanner.BuildVisible(_taxiNodes, _taxiPaths,
            continent, _taxiKnownNodes, _taxiCurrentNode);
    }
}
