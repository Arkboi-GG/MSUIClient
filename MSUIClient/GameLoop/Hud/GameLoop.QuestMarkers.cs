using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;
using MSUIClient.World.Units;

namespace MSUIClient;

public sealed partial class GameLoop
{
    /// <summary>
    /// Compose quest-status and unknown-flight-master state into authored TalkToMe M2 draws.
    /// Quest status wins when both sources name the same NPC, matching Benilla's sync order.
    /// </summary>
    private IReadOnlyList<SpellMeshDraw> QuestMarkerMeshInstances(double now)
    {
        if (_questMarkerModels is null || _net is not { IsInWorld: true }) return [];

        var markers = new Dictionary<ulong, QuestMarkerStyle>();
        foreach ((ulong guid, uint status) in _questStatuses)
            if (QuestMarkerUiLaw.Style(status) is { } style) markers[guid] = style;
        foreach ((ulong guid, bool known) in _taxiNodeKnown)
            if (!known) markers.TryAdd(guid, QuestMarkerUiLaw.UnknownFlightMaster);

        // PLAN_20, owner 2026-08-27: a questgiver only our COMPANIONS can use draws
        // no vanilla marker (our own dialog status there is NONE), so it was
        // invisible to us. Sweep every nearby questgiver, remember the set so the
        // giver-status pull can ask about all of them, and hang the party's
        // aggregate marker over any our own character has none of its own.
        //
        // The sweep must NOT gate on QuestMarkerRaised: that predicate embeds
        // nameplate LAW (the V-key switches), and friendly nameplates default
        // OFF — which silently reduced the entire party overlay to "whatever the
        // last-controlled character's vanilla statuses say". Raised keeps
        // deciding marker HEIGHT only.
        _nearbyQuestGiverGuids.Clear();
        foreach (WorldEntity giver in _entities.Entities.Values)
        {
            if (!giver.IsCreature || giver.IsDead ||
                (giver.NpcFlags & NpcQuestGiver) == 0 ||
                !PartyGiverSweepEligible(giver)) continue;
            _nearbyQuestGiverGuids.Add(giver.Guid);
            // The party AGGREGATE marker (a ! / ? for business that is not the
            // driven character's own) is a COMMANDER instrument, free view only
            // — owner 2026-08-28: embodied direct control is pure vanilla, the
            // world shows the driven character's markers and nobody else's.
            if (_freeView && !markers.ContainsKey(giver.Guid) &&
                QuestMarkerUiLaw.StyleForFamily(PartyGiverAggregateFamily(giver.Guid))
                    is { } companionStyle)
                markers[giver.Guid] = companionStyle;
        }

        var requests = new List<QuestMarkerModelRequest>(markers.Count);
        _questMarkerGuids.Clear();
        foreach ((ulong guid, QuestMarkerStyle style) in markers)
        {
            if (!_entities.TryGet(guid, out WorldEntity npc) || !npc.IsCreature || npc.IsDead ||
                (npc.NpcFlags & (NpcQuestGiver | NpcFlightMaster)) == 0) continue;

            bool mounted = npc.MountDisplayId > 0 &&
                _creatures?.TryGetMountSeat(guid, out _) == true;
            requests.Add(new QuestMarkerModelRequest(guid, style.ModelPath,
                QuestMarkerRaised(npc), mounted));
            // Ask the server about exactly the NPCs we are marking — recorded here
            // because this is the only place the authoritative set exists, and
            // consumed on the HUD pass so the render path stays free of net calls.
            if ((npc.NpcFlags & NpcQuestGiver) != 0) _questMarkerGuids.Add(guid);
        }

        return _questMarkerModels.Build(now, requests, SpellEffectUnitPose);
    }

    /// <summary>Every nearby questgiver we could ask giver-status about — the
    /// superset of the ones our own character draws a marker over.</summary>
    private readonly List<ulong> _nearbyQuestGiverGuids = [];

    /// <summary>
    /// Whether a giver belongs in the party quest sweep: standing near the
    /// controller/camera or near ANY party member. Independent of the nameplate
    /// switches and of ShowNpcNames — the party's business at an NPC is a fact
    /// about the world, not a nameplate preference.
    /// </summary>
    private bool PartyGiverSweepEligible(WorldEntity unit)
    {
        if (SettingsModalOpen || unit.IsDead ||
            (unit.Fields.UnitFlags & NotSelectable) != 0) return false;
        float rangeSq = NameplateRangeYards * NameplateRangeYards;
        if (_controller is { } rig && System.Numerics.Vector3.DistanceSquared(
                rig.Position, UnitWorldPosition(unit)) <= rangeSq)
            return true;
        if (_entities.TryGet(ControlledGuid, out WorldEntity driven) &&
            System.Numerics.Vector3.DistanceSquared(
                driven.Position, UnitWorldPosition(unit)) <= rangeSq)
            return true;
        foreach (PartyMember member in _partyMembers)
            if (_entities.TryGet(member.Guid, out WorldEntity m) &&
                System.Numerics.Vector3.DistanceSquared(
                    m.Position, UnitWorldPosition(unit)) <= rangeSq)
                return true;
        return false;
    }

    /// <summary>The party's combined business at this giver: a turn-in anyone can
    /// make wins the "?" over a take, so the art matches what most needs doing.</summary>
    private QuestMarkerFamily PartyGiverAggregateFamily(ulong giver)
    {
        QuestMarkerFamily family =
            QuestMarkerUiLaw.FamilyOf(_questStatuses.GetValueOrDefault(giver));
        if (family == QuestMarkerFamily.TurnIn) return family;
        if (_giverMemberStatuses.TryGetValue(giver, out Dictionary<ulong, byte>? members))
            foreach ((ulong guid, byte status) in members)
            {
                // The wire speaks for EVERY member, self included — and while a
                // bot is driven, the vanilla store above speaks for the BOT, so
                // the own character's business exists ONLY here. Never skip self:
                // that skip is what erased the main's "?" the moment anyone else
                // was possessed.
                QuestMarkerFamily mine = QuestMarkerUiLaw.FamilyOf(status);
                if (mine == QuestMarkerFamily.TurnIn) return QuestMarkerFamily.TurnIn;
                if (mine == QuestMarkerFamily.Take) family = QuestMarkerFamily.Take;
            }
        return family;
    }

    /// <summary>The questgivers we drew a marker over this frame, in draw order.</summary>
    private readonly List<ulong> _questMarkerGuids = [];

    /// <summary>
    /// PLAN_20 P5 → owner 2026-08-27: instead of a parenthesised COUNT, hang the
    /// NAMES of the companions who have business at this NPC over the marker — and
    /// (in the mesh pass above) draw the marker itself wherever anyone in the party
    /// has business, even a giver our own character cannot see. Our own business
    /// stays the plain vanilla marker with no label, so solo play is unchanged and
    /// only the companions who add something get named. Keeps the vanilla quest
    /// font and yellow, and never draws while a parity proof is armed.
    /// </summary>
    private void DrawQuestMarkerNumerals()
    {
        if (_uiParityArmed) return;               // never perturb a parity proof
        if (_net is not { IsInWorld: true } || SettingsModalOpen) return;
        // The free-view commander always gets the names — from the sky they are
        // the only way to say WHO has business where. Embodied play keeps
        // honoring the NPC-names preference.
        if (!Settings.Controls.ShowNpcNames && !_freeView) return;

        UpdatePartyGiverStatus(_nearbyQuestGiverGuids);
        if (!_partyGiverStatusAvailable || _questMarkerGuids.Count == 0) return;
        // The name labels are a COMMANDER instrument: from the sky they are the
        // only way to say WHO has business where. Embodied play (direct control
        // included) keeps the plain vanilla markers and nothing else — owner
        // 2026-08-28. The pull above still runs in both modes, because the
        // aggregate companion markers draw from its answers everywhere.
        if (!_freeView) return;

        Vector2 display = ImGui.GetIO().DisplaySize;
        float s = GameplayUiScale();
        ImDrawListPtr dl = ImGui.GetBackgroundDrawList();

        float linePitch = GameText.LinePitch(QuestMarkerUiLaw.NumeralFontObject, s);
        foreach (ulong guid in _questMarkerGuids)
        {
            if (GiverMemberNameLines(guid) is not { Count: > 0 } names) continue;
            if (!_entities.TryGet(guid, out WorldEntity npc)) continue;

            // Anchor on the marker's BASE (which is on screen whenever the NPC
            // is) and lift to the cleared height when that point projects too.
            // Indoors, close up, the cleared point lands above the viewport or
            // outside the frustum — anchoring on it alone made every interior
            // label silently vanish. The label is finally clamped into the
            // screen so a close camera can never push it out of view.
            Vector3 head = UnitWorldPosition(npc) +
                new Vector3(0f, 0f, UnitOverheadHeight(npc));
            if (!_window.Camera.TryWorldToScreen(head, display, out Vector2 headScreen))
                continue;
            Vector3 cleared = head +
                new Vector3(0f, 0f, QuestMarkerUiLaw.NumeralClearanceYards);
            float bottom = _window.Camera.TryWorldToScreen(cleared, display,
                    out Vector2 clearedScreen)
                ? MathF.Min(clearedScreen.Y, headScreen.Y - 2f * linePitch)
                : headScreen.Y - 2f * linePitch;
            // Names STACK VERTICALLY (owner 2026-08-28), one per line, the
            // whole column sitting above the ! / ? art and never off-screen.
            bottom = MathF.Max(bottom, 24f * s + names.Count * linePitch);

            for (int i = 0; i < names.Count; i++)
            {
                string line = "(" + names[i] + ")";
                float width = GameText.MeasureWidth(
                    QuestMarkerUiLaw.NumeralFontObject, line, s);
                GameText.Draw(dl, QuestMarkerUiLaw.NumeralFontObject, line,
                    new Vector2(headScreen.X - width * 0.5f,
                        bottom - (names.Count - i) * linePitch), s);
            }
        }
    }

    /// <summary>The party members who can take or turn in something at this
    /// giver, one name per row for the vertical stack over the marker, or null
    /// when nobody (worth naming) has business here. In the free view EVERYONE
    /// with business is named, own character included — the sky camera has no
    /// "you" to imply. Embodied, the unit being DRIVEN stays unnamed: the
    /// marker itself already speaks for it.</summary>
    private List<string>? GiverMemberNameLines(ulong giver)
    {
        if (!_partyGiverStatusAvailable ||
            !_giverMemberStatuses.TryGetValue(giver, out Dictionary<ulong, byte>? members))
            return null;

        var names = new List<string>();
        foreach ((ulong guid, byte status) in members)
        {
            if (!_freeView && guid == ControlledGuid) continue;
            if (QuestMarkerUiLaw.FamilyOf(status) == QuestMarkerFamily.None) continue;
            names.Add(guid == LocalPlayerGuid && _net is not null
                ? _net.PlayerName : ResolveUnitName(guid));
        }
        return names.Count == 0 ? null : names;
    }

    private bool QuestMarkerRaised(WorldEntity unit)
    {
        if (SettingsModalOpen || unit.Guid == ControlledGuid || unit.IsDead ||
            IsViewAnchorUnit(unit.Guid) ||
            (unit.Fields.UnitFlags & NotSelectable) != 0 ||
            !Settings.Controls.ShowNpcNames ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return false;

        if (!NameplateUiLaw.ModeAllows(ReactionTargetTowardPlayer(unit),
                _enemyNameplatesVisible, _friendlyNameplatesVisible)) return false;

        var self = _controller?.Position ?? player.Position;
        float rangeSq = NameplateRangeYards * NameplateRangeYards;
        if (System.Numerics.Vector3.DistanceSquared(self, UnitWorldPosition(unit)) <= rangeSq)
            return true;
        // [SUI] P4b: also raise the marker for a giver any PARTY MEMBER stands near,
        // so the commander sees "who has business here" from the sky without flying
        // the camera down to each NPC — this is what made the companion !/? lag until
        // the camera or the main got close. The giver-status pull rides the same set.
        foreach (PartyMember member in _partyMembers)
            if (_entities.TryGet(member.Guid, out WorldEntity m) &&
                System.Numerics.Vector3.DistanceSquared(m.Position, UnitWorldPosition(unit)) <= rangeSq)
                return true;
        return false;
    }
}
