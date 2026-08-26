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
            // PLAN_20 P5 asks the server about exactly the NPCs we are marking —
            // recorded here because this is the only place the authoritative set
            // exists, and consumed on the HUD pass so the render path stays free
            // of network calls.
            if ((npc.NpcFlags & NpcQuestGiver) != 0) _questMarkerGuids.Add(guid);
        }

        return _questMarkerModels.Build(now, requests, SpellEffectUnitPose);
    }

    /// <summary>The questgivers we drew a marker over this frame, in draw order.</summary>
    private readonly List<ulong> _questMarkerGuids = [];

    /// <summary>
    /// PLAN_20 P5, owner decision 5: keep the exact vanilla art, font and yellow,
    /// and hang a parenthesised numeral over it — <c>(4)</c> when four of your
    /// group can take what this NPC offers, and the same for turn-ins.
    ///
    /// Additive by construction. The numeral only ever appears ABOVE a marker
    /// vanilla already drew; it never adds a marker, never moves or restyles one,
    /// and never draws at all for a solo player or while a parity proof is armed
    /// — so a screenshot of vanilla play is unchanged.
    /// </summary>
    private void DrawQuestMarkerNumerals()
    {
        if (_uiParityArmed) return;               // never perturb a parity proof
        if (_net is not { IsInWorld: true } || SettingsModalOpen) return;
        if (!Settings.Controls.ShowNpcNames) return;

        UpdatePartyGiverStatus(_questMarkerGuids);
        if (!_partyGiverStatusAvailable || _questMarkerGuids.Count == 0) return;

        Vector2 display = ImGui.GetIO().DisplaySize;
        float s = GameplayUiScale();
        ImDrawListPtr dl = ImGui.GetBackgroundDrawList();

        foreach (ulong guid in _questMarkerGuids)
        {
            if (GiverNumeralFor(guid) is not { } numeral) continue;
            if (!_entities.TryGet(guid, out WorldEntity npc)) continue;

            Vector3 anchor = UnitWorldPosition(npc) + new Vector3(0f, 0f,
                UnitOverheadHeight(npc) + QuestMarkerUiLaw.NumeralClearanceYards);
            if (!_window.Camera.TryWorldToScreen(anchor, display, out Vector2 screen)) continue;

            float width = GameText.MeasureWidth(QuestMarkerUiLaw.NumeralFontObject, numeral, s);
            GameText.Draw(dl, QuestMarkerUiLaw.NumeralFontObject, numeral,
                screen - new Vector2(width * 0.5f, 0f), s);
        }
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
        return System.Numerics.Vector3.DistanceSquared(self, UnitWorldPosition(unit)) <=
            NameplateRangeYards * NameplateRangeYards;
    }
}
