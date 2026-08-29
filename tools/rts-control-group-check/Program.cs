using MSUIClient.Engine.UI;

int checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidDataException(message);
}

void Formation(int input, params (int Members, bool Raid)[] expected)
{
    RtsFormationUnit[] actual = RtsControlGroupLaw.PlanFormation(input);
    Check(actual.Length == expected.Length,
        $"formation {input}: expected {expected.Length} unit(s), got {actual.Length}");
    for (int i = 0; i < expected.Length; i++)
    {
        Check(actual[i].MemberCount == expected[i].Members,
            $"formation {input}, unit {i}: expected {expected[i].Members} members, " +
            $"got {actual[i].MemberCount}");
        Check(actual[i].IsRaid == expected[i].Raid,
            $"formation {input}, unit {i}: raid flag drifted");
    }
}

Check(RtsControlGroupLaw.GroupCount == 10,
    "Free View must expose the complete 1-9,0 numbered control-group row");
Check(RtsControlGroupLaw.MaximumWireSubjects == byte.MaxValue,
    "control-group packet limit drifted from the u8 subject count");
Check(Enumerable.Range(0, RtsControlGroupLaw.GroupCount)
        .Select(RtsControlGroupLaw.DisplayNumber)
        .SequenceEqual(["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"]),
    "control-group display numbers drifted from the physical 1-9,0 key order");

bool badIndexRejected = false;
try { _ = RtsControlGroupLaw.DisplayNumber(RtsControlGroupLaw.GroupCount); }
catch (ArgumentOutOfRangeException) { badIndexRejected = true; }
Check(badIndexRejected, "display-number law accepted an out-of-range group index");

ulong[] normalized = RtsControlGroupLaw.NormalizeMembers([0, 7, 7, 3, 0, 9, 3]);
Check(normalized.SequenceEqual([7UL, 3UL, 9UL]),
    "normalization did not drop zeros/duplicates while preserving first-seen order");

ulong[] oversized = RtsControlGroupLaw.NormalizeMembers(
    Enumerable.Range(1, 300).Select(value => (ulong)value));
Check(oversized.Length == byte.MaxValue && oversized[0] == 1 && oversized[^1] == 255,
    "normalization did not retain the first 255 explicit subjects");

bool nullRejected = false;
try { _ = RtsControlGroupLaw.NormalizeMembers(null!); }
catch (ArgumentNullException) { nullRejected = true; }
Check(nullRejected, "normalization accepted a null member sequence");

Formation(-1);
Formation(0);
Formation(1, (1, false));
Formation(5, (5, false));
Formation(6, (6, true));
Formation(40, (40, true));
Formation(41, (40, true), (1, true));
Formation(80, (40, true), (40, true));
Formation(81, (40, true), (40, true), (1, true));
Formation(255,
    (40, true), (40, true), (40, true), (40, true),
    (40, true), (40, true), (15, true));
Formation(999,
    (40, true), (40, true), (40, true), (40, true),
    (40, true), (40, true), (15, true));

Check(RtsControlGroupLaw.PlanFormation(6)[0].SubgroupCount == 2,
    "six-member raid did not occupy two five-player subgroups");
Check(RtsControlGroupLaw.FormationSummary(5) == "1 party (5/5)",
    "party formation summary drifted");
Check(RtsControlGroupLaw.FormationSummary(41) == "2 raids (9 parties)",
    "multi-raid formation summary drifted");

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
    "..", "..", "..", "..", ".."));
string hud = File.ReadAllText(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
    "GameLoop.RtsControlGroups.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
string control = File.ReadAllText(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
    "GameLoop.Control.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
string targeting = File.ReadAllText(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
    "GameLoop.Targeting.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
string actionBars = File.ReadAllText(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
    "GameLoop.ActionBars.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
string commandShelf = File.ReadAllText(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
    "GameLoop.CommandShelf.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
string settings = File.ReadAllText(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
    "GameLoop.Settings.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
string portalWire = File.ReadAllText(Path.Combine(root, "MSUIClient", "Net",
    "PortalWire.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
string session = File.ReadAllText(Path.Combine(root, "MSUIClient", "Net",
    "WorldSession.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
string groundFx = File.ReadAllText(Path.Combine(root, "MSUIClient", "World", "Units",
    "SpellEffectMeshRenderer.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);

Check(hud.Contains("Key.Number1, Key.Number2, Key.Number3", StringComparison.Ordinal) &&
      hud.Contains("Key.Number9, Key.Number0", StringComparison.Ordinal) &&
      hud.Contains("Shift+1-0: recall · Ctrl+1-0: save selected faction bots",
          StringComparison.Ordinal),
    "the physical 1-9,0 chord or Free-View-only group rail is no longer wired");
Check(hud.Contains("_freecamSelection.Where(IsRtsGroupMember)", StringComparison.Ordinal) &&
      hud.Contains("IsRtsDirectlyControllableBot", StringComparison.Ordinal) &&
      hud.Contains("_rtsForces.TryGetValue", StringComparison.Ordinal) &&
      control.Contains("force.Alive && force.SameMapAndInstance", StringComparison.Ordinal),
    "temporary groups and direct control stopped using their distinct server-roster gates");

// YOU MAY BE IN A GROUP, AND YOU ARE NEVER CONSCRIPTED. A group is a selection; every RTS lets
// the commander band themselves with their units, and FreeCamSelectableGuids already yields
// LocalPlayerGuid so the body can join marquee orders. Only the group filter disagreed, and it
// silently dropped the player - three selected, "saved: 2 bots". Reported 2026-08-26. The
// conscription guard is what keeps the server from ever being asked to order the player.
Check(hud.Contains("guid == LocalPlayerGuid || IsRtsGroupableBot(guid)", StringComparison.Ordinal),
    "the local player must be eligible for a control group (selection), not filtered out of it");
Check(hud.Contains("if (guid != LocalPlayerGuid)   // your own character never enlists",
          StringComparison.Ordinal),
    "conscription must still skip the local player: group membership is selection, not enlistment");
Check(control.Contains("ResetRtsControlGroups();", StringComparison.Ordinal) &&
      !hud.Contains("Settings.", StringComparison.Ordinal) &&
      !hud.Contains("File.Write", StringComparison.Ordinal) &&
      !hud.Contains("JsonSerializer", StringComparison.Ordinal),
    "session-only groups gained persistence or lost their terminal session reset");
Check(actionBars.Contains("RtsControlGroupClaimsBinding(ActionBinding(i))", StringComparison.Ordinal) &&
      actionBars.Contains("RtsControlGroupClaimsBinding(MultiActionBinding(bar, i))",
          StringComparison.Ordinal) &&
      actionBars.Contains("RtsControlGroupClaimsBinding(BonusActionBinding(i))",
          StringComparison.Ordinal),
    "a free-view numeral stopped suppressing a colliding main/multi/pet action-bar binding");

// Group chords must be exact. Plain bindable action keys now belong to the visible primary card;
// physical Ctrl+digits save groups and Shift+digits recall them.
Check(hud.Contains("bool assign = _freeView && CtrlHeld() && !ShiftHeld() && !AltHeld() && !typing;",
          StringComparison.Ordinal) &&
      hud.Contains("bool recall = _freeView && ShiftHeld() && !CtrlHeld() && !AltHeld() && !typing;",
          StringComparison.Ordinal) &&
      actionBars.Contains("TryUseRtsPrimaryAbilityBinding(i)", StringComparison.Ordinal) &&
      commandShelf.Contains("RtsPrimaryAbilities(primary)", StringComparison.Ordinal) &&
      commandShelf.Contains("BindingText(ActionBinding(drawn))", StringComparison.Ordinal),
    "free-view group assign/recall stopped discriminating every modifier exactly");
Check(control.Contains("bool queue = click.ShiftDown;", StringComparison.Ordinal),
    "queued waypoints stopped using the gesture-captured Shift state");
Check(commandShelf.Contains("ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)",
          StringComparison.Ordinal) &&
      commandShelf.Contains("focusPick = guid", StringComparison.Ordinal) &&
      commandShelf.Contains("FocusRtsCameraOnUnit(focusPick)", StringComparison.Ordinal) &&
      commandShelf.Contains("_controller.Teleport(unit.Position.X, unit.Position.Y, unit.Position.Z)",
          StringComparison.Ordinal),
    "double-clicking a selected-unit mini portrait stopped centering the camera on that unit");
Check(!hud.Contains("RtsGroupDoubleTapSeconds", StringComparison.Ordinal) &&
      !hud.Contains("FocusRtsCameraOnPrimary", StringComparison.Ordinal),
    "camera focus must not be attached to the Shift+number group recall chord");
Check(control.Contains("QueueRtsAttack(queuedSubjects, pickedUnit);", StringComparison.Ordinal) &&
      control.Contains("UpdateRtsAttackQueue();", StringComparison.Ordinal) &&
      control.Contains("QuestMarkerUiLaw.NumeralFontObject", StringComparison.Ordinal) &&
      control.Contains("RtsMaxWheelAltitudeYards = 120f", StringComparison.Ordinal) &&
      control.Contains("RtsMaxWheelAltitudeYards - _rtsWheelRetreatYards",
          StringComparison.Ordinal),
    "bounded RTS wheel zoom or numbered hostile kill queue is no longer wired");
Check(control.Contains("if (wheel > 0f) _window.Camera.Zoom(wheel);", StringComparison.Ordinal),
    "ordinary RTS wheel-in stopped shortening the boom below its pre-Free-View distance");
Check(control.Contains("RtsFriendlyTargetTint", StringComparison.Ordinal) &&
      control.Contains("Target: true", StringComparison.Ordinal) &&
      groundFx.Contains("private Texture RtsTargetTexture()", StringComparison.Ordinal) &&
      groundFx.Contains("r.Target ? target : ring", StringComparison.Ordinal),
    "RTS targets lost their distinct red/blue/yellow crosshair decal");
Check(targeting.Contains("HandleFreeCamWorldClick(click, pressPick);", StringComparison.Ordinal) &&
      control.Contains("pressPick.Armed", StringComparison.Ordinal) &&
      control.Contains("if (click.ShiftDown)", StringComparison.Ordinal) &&
      control.Contains("_freecamSelection.Remove(guid)", StringComparison.Ordinal) &&
      control.Contains("if (click.AltDown)", StringComparison.Ordinal) &&
      control.Contains("RTS selection and direct body possession are separate operations",
          StringComparison.Ordinal),
    "Free View lost stable/additive selection or conflated selection with possession again");
Check(hud.Contains("zoneId = _minimapReportedZoneId", StringComparison.Ordinal) &&
      hud.Contains("zoneId = _net.Player?.Zone", StringComparison.Ordinal),
    "detached-camera faction census lost its session-zone startup fallback");
Check(control.Contains("NormalizeMembers(_freecamSelection)", StringComparison.Ordinal) &&
      session.Contains("subjects.Count > byte.MaxValue", StringComparison.Ordinal),
    "explicit orders lost their client-side wire bound");
Check(hud.Contains("SameRtsMembers(_rtsWaypointSubjects, members)", StringComparison.Ordinal) &&
      hud.Contains("SendRtsControlGroupPatrol(index)", StringComparison.Ordinal) &&
      hud.Contains("orderType: 7", StringComparison.Ordinal),
    "the patrol closure or real auto-group palette command is no longer wired");
Check(hud.Contains("_factionControlGroupsProtocolAvailable", StringComparison.Ordinal) &&
      portalWire.Contains("FactionControlGroupsV1 = 1u << 2", StringComparison.Ordinal),
    "MMO faction commands stopped requiring the advertised capability bit");
Check(settings.Contains("_rtsControlGroupCommandOpen", StringComparison.Ordinal),
    "the control-group command palette escaped the normal Escape panel stack");
Check(hud.Contains("const int membersPerPage = 8;", StringComparison.Ordinal) &&
      hud.Contains("_rtsControlGroupMemberOffset", StringComparison.Ordinal) &&
      !hud.Contains("Math.Min(members.Count, 32)", StringComparison.Ordinal),
    "large control groups stopped using bounded, fully reachable member pages");

Console.WriteLine($"RTS control-group checks passed ({checks}).");
