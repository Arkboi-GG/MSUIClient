int checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidDataException(message);
}

string Slice(string source, string start, string end)
{
    int first = source.IndexOf(start, StringComparison.Ordinal);
    if (first < 0) throw new InvalidDataException($"source section missing: {start}");
    int last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
    if (last < 0) throw new InvalidDataException($"source terminator missing: {end}");
    return source[first..last];
}

DirectoryInfo? cursor = new(AppContext.BaseDirectory);
while (cursor is not null &&
       (!Directory.Exists(Path.Combine(cursor.FullName, "MSUIClient")) ||
        !Directory.Exists(Path.Combine(cursor.FullName, "tools"))))
    cursor = cursor.Parent;
string clientRoot = cursor?.FullName ??
    throw new DirectoryNotFoundException("Could not locate the MSUIClient repository root.");
string coreRoot = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(clientRoot, "..", "SuperUI-Core"));
if (!Directory.Exists(coreRoot))
    throw new DirectoryNotFoundException($"SuperUI-Core not found: {coreRoot}");

string Read(params string[] parts) => File.ReadAllText(
    Path.Combine([coreRoot, .. parts])).Replace("\r\n", "\n", StringComparison.Ordinal);

string portal = Read("src", "game", "SuperUiBots", "SuiPortal.h");
string possessHeader = Read("src", "game", "SuperUiBots", "SuiPossess.h");
string possess = Read("src", "game", "SuperUiBots", "SuiPossess.cpp");
string controlPackets = Read("src", "game", "Server", "Packets", "SuiControl.h");
string rtsPackets = Read("src", "game", "Server", "Packets", "SuiRts.h");
string opcodes = Read("src", "game", "Server", "Protocol", "Opcodes_1_12_1.h");
string opcodeHandlers = Read("src", "game", "Server", "Protocol", "Opcodes.cpp");
string sessionHeader = Read("src", "game", "Server", "WorldSession.h");
string session = Read("src", "game", "Server", "WorldSession.cpp");
string wireDoc = Read("docs", "SUI_WIRE_PROTOCOL.md");

Check(portal.Contains("CAPABILITY_FACTION_CONTROL_GROUPS_V1 = 1u << 2",
        StringComparison.Ordinal) &&
      possess.Contains("CAPABILITY_FACTION_CONTROL_GROUPS_V1", StringComparison.Ordinal),
    "capability bit 2 is not declared and advertised");
Check(opcodes.Contains("CMSG_SUI_FORCE_ROSTER                  = 842", StringComparison.Ordinal) &&
      opcodes.Contains("SMSG_SUI_FORCE_ROSTER                  = 843", StringComparison.Ordinal),
    "842/843 faction-roster allocation drifted");
Check(opcodeHandlers.Contains("DEFINE_HANDLER(CMSG_SUI_FORCE_ROSTER", StringComparison.Ordinal) &&
      opcodeHandlers.Contains("INVALID_PACKET(SMSG_SUI_FORCE_ROSTER", StringComparison.Ordinal) &&
      sessionHeader.Contains("HandleSuiForceRosterOpcode", StringComparison.Ordinal),
    "typed request/server-only reply routing is incomplete");
Check(rtsPackets.Contains("recv_data.size() != 14", StringComparison.Ordinal),
    "force-roster request stopped requiring its exact 14-byte body");
Check(controlPackets.Contains("size_t expectedBytes = 22 + size_t(count) * 8;",
        StringComparison.Ordinal) &&
      controlPackets.Contains("recv_data.size() != expectedBytes", StringComparison.Ordinal),
    "order parser lost exact u8-count body-size validation");

string tryBegin = Slice(possess, "static AckResult TryBegin", "static std::unordered_map");
Check(tryBegin.Contains("FreecamEyeOf(possessor)", StringComparison.Ordinal) &&
      tryBegin.Contains("IsGenuineFactionBot(possessor, bot)", StringComparison.Ordinal),
    "direct control lost its server-derived Free-View faction bypass");
string request = Slice(possess, "void HandleRequest", "bool IsCommandedFromFreeView");
Check(!request.Contains("RemoveFreecamEye", StringComparison.Ordinal),
    "control request can collapse Free View on denial or grant");

string autoGroup = Slice(possess, "static void HandleAutoGroup", "void HandleOrder");
Check(autoGroup.Contains("AUTO_GROUP_COOLDOWN_MS = 2000", StringComparison.Ordinal) &&
      autoGroup.Contains("GetSuiAutoGroupLastMs", StringComparison.Ordinal) &&
      sessionHeader.Contains("m_suiAutoGroupLastMs = 0", StringComparison.Ordinal),
    "auto-group lost its per-session churn cooldown");
Check(autoGroup.Contains("HasExactAutoGroupFormation(candidates)", StringComparison.Ordinal),
    "an already exact formation is no longer a read-only no-op");
Check(autoGroup.Contains("oldGroup->isBGGroup()", StringComparison.Ordinal) &&
      autoGroup.Contains("validatedGuids.find(slot.guid.GetRawValue())",
          StringComparison.Ordinal),
    "auto-group no longer protects battleground/partially selected groups as a whole");
Check(autoGroup.Contains("candidates.size() > MAX_GROUP_SIZE", StringComparison.Ordinal) &&
      autoGroup.Contains("MAX_RAID_SIZE", StringComparison.Ordinal) &&
      autoGroup.Contains("group->ConvertToRaid();", StringComparison.Ordinal),
    "party/raid split law drifted from 5/40");
Check(autoGroup.Contains("GetPossessor(bot) != requester", StringComparison.Ordinal) &&
      autoGroup.Contains("IsGenuineFactionBot(requester, bot)", StringComparison.Ordinal),
    "auto-group can steal a lease or trust client faction identity");

string order = Slice(possess, "void HandleOrder", "void OnPlayerRemovedFromGroup");
Check(order.Contains("MaNGOS::IsValidMapCoord(x, y, z)", StringComparison.Ordinal),
    "move/waypoint coordinates can reach pathfinding outside world bounds");
Check(order.Contains("if (!subjects.empty() && !SameMapAndInstance(player, pMember))",
          StringComparison.Ordinal) &&
      order.Contains("subjects.empty() || !FreecamEyeOf(player)", StringComparison.Ordinal),
    "explicit orders lost their universal same-map/instance gate or faction Free View gate");
Check(order.Contains("orderType == ORDER_FOLLOW || orderType == ORDER_LINK",
        StringComparison.Ordinal),
    "follow/link escaped real-group-only semantics");
Check(order.Contains("ai->SuiQueueWaypoint(pMember->GetPositionX()", StringComparison.Ordinal),
    "patrol no longer closes independently at each bot's current position");
Check(session.Contains("case CMSG_SUI_FORCE_ROSTER:", StringComparison.Ordinal) &&
      session.Contains("FLOOD_SLOW_OPCODES", StringComparison.Ordinal),
    "force-roster registry scan lost slow-packet flood accounting");
Check(possessHeader.Contains("ORDER_AUTO_GROUP = 7", StringComparison.Ordinal) &&
      wireDoc.Contains("bit 2 advertises MMO faction control groups", StringComparison.Ordinal) &&
      wireDoc.Contains("two-second per-`WorldSession` cooldown", StringComparison.Ordinal),
    "server enum or normative wire documentation drifted");

Console.WriteLine($"SUI faction-control server source checks passed ({checks}).");
