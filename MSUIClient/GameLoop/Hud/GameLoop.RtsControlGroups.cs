using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;
using Silk.NET.Input;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private static readonly Key[] RtsControlGroupKeys =
    [
        Key.Number1, Key.Number2, Key.Number3, Key.Number4, Key.Number5,
        Key.Number6, Key.Number7, Key.Number8, Key.Number9, Key.Number0,
    ];

    // Session-only by design. This state never enters settings.json, keybindings.json,
    // botbars.json, or a server group until the player explicitly chooses Auto-group.
    private readonly List<ulong>[] _rtsControlGroups = CreateRtsControlGroups();
    private readonly bool[] _rtsControlGroupKeyWasDown = new bool[RtsControlGroupLaw.GroupCount];
    private int _rtsControlGroupCommandIndex = -1;
    private int _rtsControlGroupMemberOffset;
    private bool _rtsControlGroupCommandOpen;
    private string _rtsControlGroupStatus = string.Empty;
    private double _rtsControlGroupStatusAt;

    // Opcodes 842/843 are beyond older MMO cores. A zero-guid request on the
    // backwards-compatible control opcode negotiates this bit before either is sent.
    private bool _factionControlGroupsProbeSent;
    private bool _factionControlGroupsProbePending;
    private bool _factionControlGroupsProtocolAvailable;
    private double _factionControlGroupsProbeDeadline;
    private const double FactionControlGroupsProbeTimeoutSeconds = 3.0;

    private static List<ulong>[] CreateRtsControlGroups()
    {
        var groups = new List<ulong>[RtsControlGroupLaw.GroupCount];
        for (int i = 0; i < groups.Length; i++) groups[i] = [];
        return groups;
    }

    private void ResetRtsControlGroups()
    {
        foreach (List<ulong> group in _rtsControlGroups) group.Clear();
        Array.Clear(_rtsControlGroupKeyWasDown);
        _rtsControlGroupCommandIndex = -1;
        _rtsControlGroupMemberOffset = 0;
        _rtsControlGroupCommandOpen = false;
        _rtsControlGroupStatus = string.Empty;
        _rtsControlGroupStatusAt = 0;
        _factionControlGroupsProbeSent = false;
        _factionControlGroupsProbePending = false;
        _factionControlGroupsProtocolAvailable = false;
        _factionControlGroupsProbeDeadline = 0;
    }

    /// <summary>Per-frame input, capability, and zone-roster work.</summary>
    private void UpdateRtsControlGroups(bool typing)
    {
        UpdateFactionControlGroupsCapability();
        UpdateRtsControlGroupKeys(typing);

        if (!_freeView)
        {
            _rtsControlGroupCommandOpen = false;
            return;
        }

        UpdateFreeViewFactionRoster();
    }

    private void UpdateFactionControlGroupsCapability()
    {
        if (_net is not { IsInWorld: true }) return;
        double now = NowSeconds();

        // Do not probe from the sky: legacy HandleRequest implementations lower
        // their streaming eye before discovering that the zero target is invalid.
        if (!_factionControlGroupsProbeSent && !_freeView &&
            _controlState == ControlState.OwnChar && _net.SuiControlRequest(0))
        {
            _factionControlGroupsProbeSent = true;
            _factionControlGroupsProbePending = true;
            _factionControlGroupsProbeDeadline = now + FactionControlGroupsProbeTimeoutSeconds;
            return;
        }

        if (_factionControlGroupsProbePending && now >= _factionControlGroupsProbeDeadline)
        {
            _factionControlGroupsProbePending = false;
            _factionControlGroupsProtocolAvailable = false;
        }
    }

    /// <summary>Called by the shared control-ACK capability parser.</summary>
    private void ApplyFactionControlGroupsCapability(uint capabilities, bool isProbeReply)
    {
        bool available = (capabilities & SuiCapabilityWire.FactionControlGroupsV1) != 0;
        if (available != _factionControlGroupsProtocolAvailable)
            Console.WriteLine(available
                ? "[rts-groups] server advertised faction-control-groups-v1"
                : "[rts-groups] server has no faction-control-groups-v1 advertisement");
        _factionControlGroupsProtocolAvailable = available;
        if (capabilities != 0) _factionControlGroupsProbeSent = true;
        if (isProbeReply)
        {
            _factionControlGroupsProbeSent = true;
            _factionControlGroupsProbePending = false;
            _factionControlGroupsProbeDeadline = 0;
        }
    }

    private bool CanUseFactionForceRoster() =>
        _factionControlGroupsProtocolAvailable ||
        CommanderMapUiLaw.ShowFactionControl(_rtsMode, _rtsModules);

    /// <summary>
    /// Keep the server-filtered force page under the detached camera current even
    /// when Commander Map is closed. Rendered friendliness is never used as proof
    /// that a Player entity is a genuine AiBot.
    /// </summary>
    private void UpdateFreeViewFactionRoster()
    {
        if (!CanUseFactionForceRoster() || _commanderMapOpen || _net is not { IsInWorld: true })
            return;

        double now = NowSeconds();
        if (_rtsForceLoading && now - _rtsForceRequestAt > RtsForceRequestTimeoutSeconds)
        {
            _rtsForceLoading = false;
            _rtsForceStaging.Clear();
        }

        uint zoneId = _areas?.ParentZoneId(_minimapAreaId) ?? 0;
        if (zoneId == 0) zoneId = _minimapAreaId;
        if (zoneId == 0) return;

        if (!_rtsForceLoading &&
            (_rtsForcePublishedZone != zoneId || now - _rtsForceAt > CommanderIntelRefreshSeconds))
            BeginRtsForceRosterLoad(zoneId);
    }

    private void UpdateRtsControlGroupKeys(bool typing)
    {
        bool assign = _freeView && ShiftHeld() && !typing;
        for (int i = 0; i < RtsControlGroupKeys.Length; i++)
        {
            bool down = InputKeyDown(RtsControlGroupKeys[i]);
            if (assign && down && !_rtsControlGroupKeyWasDown[i])
                AssignRtsControlGroup(i);
            // Track every physical edge, including typing/outside-Free-View frames,
            // so a held key can never become an assignment after the gate changes.
            _rtsControlGroupKeyWasDown[i] = down;
        }
    }

    /// <summary>Does the Shift+number group chord own this remappable action binding?</summary>
    private bool RtsControlGroupClaimsBinding(GameBinding binding)
    {
        if (!_freeView || !ShiftHeld()) return false;
        BindingPair bound = BoundKeys(binding);
        foreach (Key key in RtsControlGroupKeys)
            if (InputKeyDown(key) && bound.ContainsBase(key)) return true;
        return false;
    }

    private bool IsRtsControllableBot(ulong guid)
    {
        if (guid == 0 || guid == LocalPlayerGuid) return false;
        if (guid == _controlTargetGuid && _controlState == ControlState.Possessing) return true;
        if (_rtsForces.TryGetValue(guid, out RtsForceUnitWire force) &&
            force.Alive && !force.Busy && force.ControlEligibleNow && force.SameMapAndInstance)
            return true;
        foreach ((ulong rosterGuid, byte flags) in _suiRoster)
            if (rosterGuid == guid && (flags & SuiRosterControllable) != 0 &&
                ((flags & SuiRosterPossessed) == 0 || guid == _controlTargetGuid))
                return true;
        return false;
    }

    private void AssignRtsControlGroup(int index)
    {
        string number = RtsControlGroupLaw.DisplayNumber(index);
        int eligibleCount = _freecamSelection.Count(IsRtsControllableBot);
        ulong[] members = RtsControlGroupLaw.NormalizeMembers(
            _freecamSelection.Where(IsRtsControllableBot));
        if (members.Length == 0)
        {
            // Name the ACTUAL closed gate — "select faction bots" blamed the user for a
            // server condition and read as a faction check (it never was one). Faction
            // bot control is BASELINE SuperUI: a current core always advertises
            // faction-control-groups-v1 in the control-ACK trailer.
            if (!CanUseFactionForceRoster())
                ShowUiError("This server build does not advertise faction-control-groups-v1 — " +
                    "update SuperUI-Core. Until then only party bots and the possessed body " +
                    "are groupable.");
            else if (_rtsForces.Count == 0)
                ShowUiError("No faction census received for this zone yet — the force roster " +
                    "refreshes every few seconds in the free view.");
            else
                ShowUiError($"Select one or more controllable faction bots before assigning " +
                    $"group {number} (selected units are busy, possessed, or elsewhere).");
            return;
        }

        List<ulong> group = _rtsControlGroups[index];
        group.Clear();
        group.AddRange(members);
        if (eligibleCount > members.Length)
            ShowUiError($"Group {number} is limited to " +
                $"{RtsControlGroupLaw.MaximumWireSubjects} explicit bots by the order wire.");
        _rtsControlGroupStatus = $"Group {number} saved: {members.Length} bot" +
            (members.Length == 1 ? "." : "s.");
        _rtsControlGroupStatusAt = NowSeconds();
        AddChatMessage(_rtsControlGroupStatus);
    }

    private void SelectRtsControlGroup(int index, bool openCommands)
    {
        if ((uint)index >= (uint)_rtsControlGroups.Length || _rtsControlGroups[index].Count == 0)
            return;

        // A route belongs to the selection that authored its first leg. Choosing a
        // different card explicitly abandons the unfinished authoring gesture.
        if (_rtsWaypointChain.Count > 0 &&
            !SameRtsMembers(_rtsWaypointSubjects, _rtsControlGroups[index]))
            ClearRtsWaypointChain();

        _freecamSelection.Clear();
        _freecamSelection.AddRange(_rtsControlGroups[index]);
        if (_freecamSelection.Count == 1) EnsureBotBarForViewing(_freecamSelection[0]);
        _rtsControlGroupCommandIndex = index;
        _rtsControlGroupMemberOffset = 0;
        _rtsControlGroupCommandOpen = openCommands;
    }

    private static bool SameRtsMembers(IReadOnlyList<ulong> left, IReadOnlyList<ulong> right)
    {
        if (left.Count != right.Count) return false;
        var seen = new HashSet<ulong>(left);
        return seen.Count == left.Count && right.All(seen.Contains);
    }

    private bool SendRtsControlGroupOrder(int index, byte orderType)
    {
        if ((uint)index >= (uint)_rtsControlGroups.Length) return false;
        List<ulong> members = _rtsControlGroups[index];
        if (members.Count is 0 or > RtsControlGroupLaw.MaximumWireSubjects || _net is null)
            return false;
        return _net.SuiOrder(orderType, members, 0, 0, 0, 0);
    }

    private bool SendRtsControlGroupPatrol(int index)
    {
        if ((uint)index >= (uint)_rtsControlGroups.Length || _net is null) return false;
        List<ulong> members = _rtsControlGroups[index];
        if (members.Count is 0 or > RtsControlGroupLaw.MaximumWireSubjects) return false;

        // Current servers close each bot's loop at that bot's own position. The
        // non-zero fallback keeps older party-only cores from appending world
        // origin when they still use the packet coordinate as a shared anchor.
        foreach (ulong guid in members)
            if (_entities.TryGet(guid, out WorldEntity unit) && !unit.IsDead)
                return _net.SuiOrder(4, members, 0,
                    unit.Position.X, unit.Position.Y, unit.Position.Z);
        return false;
    }

    private void SetRtsControlGroupStatus(string status)
    {
        _rtsControlGroupStatus = status;
        _rtsControlGroupStatusAt = NowSeconds();
        AddChatMessage(status);
    }

    private int RtsControlGroupVisibleCount(IReadOnlyList<ulong> members) =>
        members.Count(guid => _entities.TryGet(guid, out WorldEntity unit) && !unit.IsDead);

    /// <summary>Free-View-only sticky cards and their non-blocking command palette.</summary>
    private void DrawRtsControlGroups()
    {
        if (!_freeView) return;

        var active = new List<int>();
        for (int i = 0; i < _rtsControlGroups.Length; i++)
            if (_rtsControlGroups[i].Count > 0) active.Add(i);

        float scale = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize;
        if (active.Count == 0)
        {
            const string hint = "Shift+1-0: save selected faction bots";
            Vector2 size = ImGui.CalcTextSize(hint);
            ImGui.GetForegroundDrawList().AddText(
                new Vector2((display.X - size.X) * 0.5f, 54f * scale), 0xCCB8C8D8u, hint);
            return;
        }

        float cardWidth = 104f * scale;
        float gap = 4f * scale;
        int perRow = Math.Max(1, (int)((display.X - 16f + gap) / (cardWidth + gap)));
        int rowCount = (active.Count + perRow - 1) / perRow;
        int widestRow = Math.Min(active.Count, perRow);
        float railWidth = widestRow * cardWidth + Math.Max(0, widestRow - 1) * gap;
        ImGui.SetNextWindowPos(new Vector2(MathF.Max(8f, (display.X - railWidth) * 0.5f),
            78f * scale), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.78f);
        ImGuiWindowFlags cardFlags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing;
        if (ImGui.Begin("##rts-control-groups", cardFlags))
        {
            for (int position = 0; position < active.Count; position++)
            {
                int index = active[position];
                List<ulong> group = _rtsControlGroups[index];
                int visible = RtsControlGroupVisibleCount(group);
                string first = CommanderForceName(group[0], request: true);
                string title = group.Count == 1 ? first : $"{first} +{group.Count - 1}";
                string number = RtsControlGroupLaw.DisplayNumber(index);
                string label = $"{number}  {title}\n{visible}/{group.Count} nearby##rts-group-{index}";
                if (ImGui.Button(label, new Vector2(cardWidth, 38f * scale)))
                    SelectRtsControlGroup(index, openCommands: true);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Select this session group and open its command palette.");
                if (position + 1 < active.Count && (position + 1) % perRow != 0)
                    ImGui.SameLine(0, gap);
            }
        }
        ImGui.End();

        DrawRtsControlGroupCommandPalette(scale, display,
            (132f + Math.Max(0, rowCount - 1) * 44f) * scale);
    }

    private void DrawRtsControlGroupCommandPalette(float scale, Vector2 display, float top)
    {
        int index = _rtsControlGroupCommandIndex;
        if (!_rtsControlGroupCommandOpen || (uint)index >= (uint)_rtsControlGroups.Length ||
            _rtsControlGroups[index].Count == 0) return;

        List<ulong> members = _rtsControlGroups[index];
        ImGui.SetNextWindowPos(new Vector2(MathF.Max(8f, display.X - 338f * scale),
            top), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.94f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoCollapse;
        string number = RtsControlGroupLaw.DisplayNumber(index);
        if (ImGui.Begin($"Control Group {number}##rts-control-group-command",
                ref _rtsControlGroupCommandOpen, flags))
        {
            int visible = RtsControlGroupVisibleCount(members);
            ImGui.Text($"{members.Count} faction bots  ·  {visible} nearby");
            ImGui.TextDisabled("RightClick still moves/attacks this selection.");
            ImGui.Separator();

            if (ImGui.Button("Select", new Vector2(92f * scale, 0)))
                SelectRtsControlGroup(index, openCommands: true);
            ImGui.SameLine();
            if (ImGui.Button("Hold", new Vector2(92f * scale, 0)) &&
                SendRtsControlGroupOrder(index, orderType: 2))
            {
                ClearRtsWaypointChain();
                SetRtsControlGroupStatus($"Group {number}: hold position.");
            }
            ImGui.SameLine();
            bool canAutoGroup = _factionControlGroupsProtocolAvailable;
            if (!canAutoGroup) ImGui.BeginDisabled();
            if (ImGui.Button("Auto-group", new Vector2(112f * scale, 0)) &&
                SendRtsControlGroupOrder(index, orderType: 7))
                SetRtsControlGroupStatus($"Group {number}: requested " +
                    RtsControlGroupLaw.FormationSummary(members.Count) + ".");
            if (!canAutoGroup) ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(canAutoGroup
                    ? "Create real parties/raids: five per party, forty per raid.\n" +
                      "Protected real-player parties are never pulled apart."
                    : "This server has not advertised faction-control-groups-v1.");

            bool routeReady = _rtsWaypointChain.Count > 0 &&
                SameRtsMembers(_rtsWaypointSubjects, members);
            if (routeReady)
            {
                if (ImGui.Button("Start patrol", new Vector2(150f * scale, 0)) &&
                    SendRtsControlGroupPatrol(index))
                    SetRtsControlGroupStatus($"Group {number}: patrol route started.");
                ImGui.SameLine();
                ImGui.TextDisabled($"{_rtsWaypointChain.Count} waypoint" +
                    (_rtsWaypointChain.Count == 1 ? string.Empty : "s"));
            }
            else
                ImGui.TextDisabled("Patrol: Shift+RightClick one or more waypoints, then return here.");

            ImGui.Separator();
            const int membersPerPage = 8;
            int lastPageOffset = ((members.Count - 1) / membersPerPage) * membersPerPage;
            _rtsControlGroupMemberOffset = Math.Clamp(
                _rtsControlGroupMemberOffset, 0, lastPageOffset);
            int pageEnd = Math.Min(members.Count,
                _rtsControlGroupMemberOffset + membersPerPage);
            int shownMembers = pageEnd - _rtsControlGroupMemberOffset;
            float memberHeight = Math.Max(shownMembers, 1) * 24f * scale;
            if (ImGui.BeginChild("##rts-control-group-members",
                    new Vector2(312f * scale, memberHeight), true))
            {
                // Page rather than eagerly resolving all 255 names. Every member
                // remains reachable, while opening a card produces at most eight
                // stock name queries.
                for (int memberIndex = _rtsControlGroupMemberOffset;
                     memberIndex < pageEnd; memberIndex++)
                {
                    ulong guid = members[memberIndex];
                    bool resident = _entities.TryGet(guid, out WorldEntity unit) && !unit.IsDead;
                    bool controllable = resident && IsRtsControllableBot(guid);
                    ImGui.TextUnformatted(CommanderForceName(guid, request: true));
                    if (controllable)
                    {
                        ImGui.SameLine(220f * scale);
                        if (ImGui.SmallButton($"Control##rts-control-{guid}"))
                            SwitchControlTo(guid);
                    }
                    else
                    {
                        ImGui.SameLine(220f * scale);
                        ImGui.TextDisabled(resident ? "unavailable" : "not nearby");
                    }
                }
            }
            ImGui.EndChild();

            if (members.Count > membersPerPage)
            {
                bool hasPrevious = _rtsControlGroupMemberOffset > 0;
                bool hasNext = pageEnd < members.Count;
                if (!hasPrevious) ImGui.BeginDisabled();
                if (ImGui.SmallButton("Previous members"))
                    _rtsControlGroupMemberOffset -= membersPerPage;
                if (!hasPrevious) ImGui.EndDisabled();
                ImGui.SameLine();
                ImGui.TextDisabled($"{_rtsControlGroupMemberOffset + 1}-{pageEnd} / {members.Count}");
                ImGui.SameLine();
                if (!hasNext) ImGui.BeginDisabled();
                if (ImGui.SmallButton("Next members"))
                    _rtsControlGroupMemberOffset += membersPerPage;
                if (!hasNext) ImGui.EndDisabled();
            }

            if (_rtsControlGroupStatus.Length > 0 &&
                NowSeconds() - _rtsControlGroupStatusAt < 8.0)
                ImGui.TextWrapped(_rtsControlGroupStatus);

            if (ImGui.Button("Clear temporary group"))
            {
                bool selectedThisGroup = SameRtsMembers(_freecamSelection, members);
                bool routeForThisGroup = SameRtsMembers(_rtsWaypointSubjects, members);
                members.Clear();
                if (selectedThisGroup) _freecamSelection.Clear();
                _rtsControlGroupCommandOpen = false;
                _rtsControlGroupCommandIndex = -1;
                _rtsControlGroupMemberOffset = 0;
                if (routeForThisGroup) ClearRtsWaypointChain();
            }
        }
        ImGui.End();
    }
}
