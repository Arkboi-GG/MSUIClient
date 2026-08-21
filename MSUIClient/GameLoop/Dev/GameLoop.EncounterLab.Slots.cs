using System.Numerics;
using ImGuiNET;
using MSUIClient.Formats;
using MSUIClient.World.Encounters;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Encounter Lab — the two-slot assignment surface.
//
// A body's behaviour is TWO independent, reusable slots, each a reference into a
// library:
//
//   Rotation    — what I press. Portable across every fight (CombatPlan). Authored
//                 once per class/role, assigned to many bodies, cloned per fight.
//   Positioning — where I stand. Authored per role AND per side, per boss
//                 (PositioningScript). Owns all spatial doctrine and the per-phase
//                 spots. "Left ranged" and "right ranged" are two scripts; author
//                 one and mirror it across the boss's facing for the other.
//
// This file owns: the identity controls (role + macro group, drawn in the Game
// Plan header), the two library pickers (rotation slot in the Rotation tab,
// positioning slot in Advanced), the positioning draft + its editor, the mirror,
// and the save/resolve path that
// stitches an assigned rotation and positioning back into the single inline
// CombatPlan the current sim still reads. When the sim later learns to read the
// positioning slot directly, that bridge in ResolveInlinePlan is the only thing
// that goes away.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class GameLoop
{
    private PositioningScript? _encounterPositioningDraft;
    private string? _encounterPositioningDraftKey;
    private bool _encounterPositioningDirty;

    private void InvalidateEncounterPositioningDraft()
    {
        _encounterPositioningDraft = null;
        _encounterPositioningDraftKey = null;
        _encounterPositioningDirty = false;
    }

    /// <summary>Load the positioning draft for a body: its assigned library script if it
    /// has one, otherwise a fresh doctrine-only script seeded from the body's role and
    /// side. Kept parallel to the rotation draft but deliberately without an undo stack
    /// in this first pass — spots are placed by clicking the world, which the scrub
    /// history already makes reversible.</summary>
    private void EnsureEncounterPositioningDraft(EncounterActorSpec actor)
    {
        if (_encounterPositioningDraft is not null &&
            string.Equals(_encounterPositioningDraftKey, actor.Key, StringComparison.Ordinal)) return;

        _encounterPositioningDraftKey = actor.Key;
        _encounterPositioningDirty = false;

        string? id = actor.PlayerRules?.PositioningId;
        PositioningScript? assigned = string.IsNullOrEmpty(id)
            ? null : EncounterPositioningStoreRef.Find(id!);
        _encounterPositioningDraft = assigned ?? new PositioningScript(
            Name: DefaultPositioningName(actor.Job, actor.Side),
            Role: actor.Job,
            Side: actor.Side,
            Movement: actor.PlayerRules?.Plan?.Movement ?? new CombatMovementPlan(),
            EncounterKey: _encounterDefinition?.Key);
    }

    private static string DefaultPositioningName(RaidJob job, RaidSide side)
    {
        string role = job == RaidJob.None ? "positioning" : job.ToString().ToLowerInvariant();
        return side == RaidSide.None ? role : $"{side.Label()} {role}";
    }

    private void SetPositioningDraft(PositioningScript next)
    {
        _encounterPositioningDraft = next;
        _encounterPositioningDirty = true;
    }

    /// <summary>Per-body positioning the sim drives from, keyed by actor key. The body
    /// being authored uses its live (possibly unsaved) draft so placing a spot moves
    /// the puppet immediately; every other body uses its assigned library script. A
    /// body with neither falls through to the job-wide playbook inside the sim.</summary>
    private IReadOnlyDictionary<string, PositioningScript>? BuildEncounterPositioningMap()
    {
        Dictionary<string, PositioningScript>? map = null;
        foreach (EncounterActorSpec actor in _encounterScenario)
        {
            if (actor.Role != EncounterActorRole.Friendly) continue;
            PositioningScript? script = null;
            if (_encounterPositioningDraft is { } draft &&
                string.Equals(_encounterPositioningDraftKey, actor.Key, StringComparison.Ordinal))
                script = draft;
            else if (actor.PlayerRules?.PositioningId is { Length: > 0 } id)
                script = EncounterPositioningStoreRef.Find(id);
            if (script is null) continue;
            (map ??= [])[actor.Key] = script;
        }
        return map;
    }

    /// <summary>Ground-click target: write (or update) this phase's MoveToSpot spot into
    /// the positioning draft. Reached from the armed PositioningSpot placement.</summary>
    private void SetPositioningPhaseSpot(string phaseKey, Vector3 point)
    {
        if (_encounterPositioningDraft is not { } script) return;
        List<PositioningPhaseStep> steps = (script.Phases ?? []).ToList();
        int i = steps.FindIndex(s => s.PhaseKey == phaseKey);
        PositioningPhaseStep step = i >= 0
            ? steps[i] with { Kind = RaidDirectiveKind.MoveToSpot, Spot = point }
            : new PositioningPhaseStep(phaseKey, RaidDirectiveKind.MoveToSpot, point);
        if (i >= 0) steps[i] = step; else steps.Add(step);
        SetPositioningDraft(script with { Phases = steps });
        RebuildEncounterSimKeepingView();   // live preview: the body walks to the new spot
        AddChatMessage($"Positioning '{script.Name}': spot placed for phase {phaseKey}.");
    }

    private void SetPositioningPhaseKind(string phaseKey, RaidDirectiveKind kind)
    {
        if (_encounterPositioningDraft is not { } script) return;
        List<PositioningPhaseStep> steps = (script.Phases ?? []).ToList();
        int i = steps.FindIndex(s => s.PhaseKey == phaseKey);
        if (kind == RaidDirectiveKind.Hold && i >= 0 && steps[i].Waypoints is not { Count: > 0 })
        {
            steps.RemoveAt(i);   // Hold is the absence of a directive; keep the list tidy
        }
        else
        {
            PositioningPhaseStep step = i >= 0
                ? steps[i] with { Kind = kind }
                : new PositioningPhaseStep(phaseKey, kind);
            if (i >= 0) steps[i] = step; else steps.Add(step);
        }
        SetPositioningDraft(script with { Phases = steps });
        RebuildEncounterSimKeepingView();   // live preview: Hold/Chase/Spot re-drives the body
    }

    // ── mirror ───────────────────────────────────────────────────────────────

    /// <summary>Reflect every placed spot across the boss's facing axis and flip the
    /// side, producing an unsaved draft the owner reviews and then saves. Left⇄Right;
    /// Center/None have nothing to reflect and are refused.</summary>
    private void MirrorPositioningDraft()
    {
        if (_encounterPositioningDraft is not { } script) return;
        if (script.Side is RaidSide.Center or RaidSide.None)
        {
            AddChatMessage("Mirror needs a Left or Right script — Center has no opposite side.");
            return;
        }

        (Vector3 origin, float facing) = BossAxis();
        Vector3 fwd = EncounterGeometryLaw.Forward(facing);
        Vector3 left = new(-fwd.Y, fwd.X, 0f);

        Vector3 Reflect(Vector3 p)
        {
            Vector3 d = p - origin;
            float along = d.X * fwd.X + d.Y * fwd.Y;
            float side = d.X * left.X + d.Y * left.Y;
            Vector3 r = origin + fwd * along - left * side;
            r.Z = p.Z;
            return r;
        }

        float MirrorFacing(float f) => float.IsNaN(f) ? f : 2f * facing - f;

        List<PositioningPhaseStep> steps = (script.Phases ?? []).Select(s => s with
        {
            Spot = s.Kind == RaidDirectiveKind.MoveToSpot ? Reflect(s.Spot) : s.Spot,
            ArrivalFacing = MirrorFacing(s.ArrivalFacing),
            Waypoints = s.Waypoints?.Select(w => w with
            {
                Position = Reflect(w.Position),
                ArrivalFacing = MirrorFacing(w.ArrivalFacing),
            }).ToArray(),
        }).ToList();

        RaidSide mirrored = script.Side.Mirror();
        string name = MirrorName(script.Name, script.Side, mirrored);
        // Id cleared: the mirror is a NEW library entry, saved on the owner's word.
        _encounterPositioningDraft = script with
        { Id = "", Side = mirrored, Name = name, Phases = steps };
        _encounterPositioningDirty = true;
        AddChatMessage($"Mirrored to {mirrored.Label()} across the boss axis — review, then Save.");
    }

    private static string MirrorName(string name, RaidSide from, RaidSide to)
    {
        string a = from.Label(), b = to.Label();
        if (name.Contains(a, StringComparison.OrdinalIgnoreCase))
            return System.Text.RegularExpressions.Regex.Replace(
                name, System.Text.RegularExpressions.Regex.Escape(a), b,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return $"{name} ({b})";
    }

    private (Vector3 Origin, float Facing) BossAxis()
    {
        if (_encounterScenario.FirstOrDefault(a => a.Role == EncounterActorRole.Boss) is { } boss)
            return (boss.Position, boss.Facing);
        return (Vector3.Zero, 0f);
    }

    // ── save / resolve ─────────────────────────────────────────────────────────

    /// <summary>Stitch an assigned rotation and positioning back into the single inline
    /// CombatPlan the sim reads today. The one bridge: the sim's follow model still lives
    /// on Plan.Movement, so the positioning slot's movement is fed into it here. The
    /// library rotation itself stays movement-free and portable.</summary>
    private static CombatPlan? ResolveInlinePlan(CombatPlan? rotation, PositioningScript? positioning)
    {
        if (rotation is null && positioning is null) return null;
        CombatPlan basePlan = rotation ?? new CombatPlan();
        CombatMovementPlan? movement = positioning?.Movement ?? basePlan.Movement;
        return basePlan with { Movement = movement };
    }

    /// <summary>Write the body's slot assignment: rotation id, positioning id, side, and
    /// the resolved inline plan, then rebuild. Any argument left null keeps the body's
    /// current assignment for that slot.</summary>
    private void AssignBodySlots(int actorIndex, EncounterActorSpec actor,
        CombatPlan? rotation = null, PositioningScript? positioning = null, RaidSide? side = null)
    {
        EncounterPlayerRules rules = actor.PlayerRules ?? new EncounterPlayerRules();

        string? rotId = rotation?.Id ?? rules.RotationId;
        string? posId = positioning?.Id ?? rules.PositioningId;
        CombatPlan? rot = rotation
            ?? (!string.IsNullOrEmpty(rotId) ? EncounterCombatPlanStoreRef.Find(rotId!) : rules.Plan);
        PositioningScript? pos = positioning
            ?? (!string.IsNullOrEmpty(posId) ? EncounterPositioningStoreRef.Find(posId!) : null);

        CombatPlan? inline = ResolveInlinePlan(rot, pos);
        RaidSide newSide = side ?? pos?.Side ?? actor.Side;
        bool facePrimary = pos?.Movement?.FacePrimaryEnemy
            ?? inline?.Movement?.FacePrimaryEnemy ?? rules.AlwaysFaceBoss;

        _encounterScenario[actorIndex] = actor with
        {
            Side = newSide,
            PlayerRules = rules with
            {
                RotationId = string.IsNullOrEmpty(rotId) ? null : rotId,
                PositioningId = string.IsNullOrEmpty(posId) ? null : posId,
                Plan = inline,
                AlwaysFaceBoss = facePrimary,
            },
        };
        RebuildEncounterSimKeepingView();
    }

    private void SavePositioningDraftAndAssign(int actorIndex, EncounterActorSpec actor)
    {
        if (_encounterPositioningDraft is not { } script) return;
        if (EncounterPositioningStoreRef.Upsert(script, out PositioningScript stored))
        {
            _encounterPositioningDraft = stored;
            _encounterPositioningDraftKey = actor.Key;
            _encounterPositioningDirty = false;
            AssignBodySlots(actorIndex, actor, positioning: stored, side: stored.Side);
            AddChatMessage($"{actor.Name}: positioning '{stored.Name}' saved and assigned.");
        }
        else
        {
            string detail = EncounterPositioningStoreRef.Errors.LastOrDefault() ??
                            "unknown persistence error";
            AddChatMessage($"{actor.Name}: positioning was not saved ({detail}).");
        }
    }

    // ── identity (role + macro group) ─────────────────────────────────────────
    // Drawn in the Game Plan header — it is the one owner decision the formation
    // cannot derive: what this body is FOR and which macro group it fights in.

    private void DrawEncounterSlotIdentity(int actorIndex, EncounterActorSpec actor)
    {
        float cs = CreatorUiScale;

        int job = (int)actor.Job;
        ImGui.SetNextItemWidth(160f * cs);
        if (ImGui.Combo("Role", ref job, "Friendly\0Tank\0Healer\0Melee\0Ranged\0"))
        {
            foreach (int i in GamePlanTargetIndices(actorIndex))
                _encounterScenario[i] = _encounterScenario[i] with { Job = (RaidJob)job };
            RebuildEncounterSimKeepingView();
        }
        ImGui.SameLine();
        int sideIdx = (int)actor.Side;
        ImGui.SetNextItemWidth(170f * cs);
        if (ImGui.Combo("Group", ref sideIdx, "Auto-split\0Group 1 (left)\0Center\0Group 2 (right)\0"))
        {
            foreach (int i in GamePlanTargetIndices(actorIndex))
                _encounterScenario[i] = _encounterScenario[i] with { Side = (RaidSide)sideIdx };
            RebuildEncounterSimKeepingView();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The MACRO group: which flank this body fights on. Auto-split " +
                             "balances unassigned bodies across both flanks per role.");
    }

    /// <summary>The assigned-rotation library combo, on its own so the Game Plan
    /// header can host it too — picking a rotation is a first-class decision, not
    /// something buried in an editor tab.</summary>
    private void DrawEncounterRotationAssignCombo(int actorIndex, EncounterActorSpec actor)
    {
        CombatPlan draft = _encounterPlayerPlanDraft ?? new CombatPlan();
        string assignedId = actor.PlayerRules?.RotationId ?? "";

        var library = EncounterCombatPlanStoreRef.CharacterPlans;
        string preview = !string.IsNullOrEmpty(assignedId) && library.TryGetValue(assignedId, out CombatPlan? cur)
            ? cur.Name
            : draft.Name.Length > 0 ? $"{draft.Name} (unsaved)" : "(none)";

        ImGui.SetNextItemWidth(260f * CreatorUiScale);
        if (ImGui.BeginCombo("Rotation", preview))
        {
            foreach ((string id, CombatPlan plan) in library.OrderBy(p => p.Value.Name))
            {
                bool selected = id == assignedId;
                string label = $"{plan.Name}##rot-{id}" +
                    (plan.ClassId != 0 ? $"  ({ClassSpellList.ClassName(plan.ClassId)})" : "");
                if (ImGui.Selectable(label, selected))
                    AssignRotationFromLibrary(actorIndex, actor, plan);
                if (selected) ImGui.SetItemDefaultFocus();
            }
            if (library.Count == 0) ImGui.TextDisabled("no saved rotations yet");
            ImGui.EndCombo();
        }
    }

    private void DrawEncounterRotationSlot(int actorIndex, EncounterActorSpec actor)
    {
        ImGui.SeparatorText("Rotation slot — what I press (portable)");
        CombatPlan draft = _encounterPlayerPlanDraft ?? new CombatPlan();
        DrawEncounterRotationAssignCombo(actorIndex, actor);

        string planName = draft.Name;
        ImGui.SetNextItemWidth(260f * CreatorUiScale);
        if (ImGui.InputText("Name##rot", ref planName, 80))
            SetEncounterPlayerPlanDraftContinuous(draft with { Name = planName });
        FinishEncounterPlayerPlanContinuousEdit();

        if (EncounterPanelButton("Clone rotation", compact: true))
        {
            // A clone is a fresh library entry (blank id) seeded from the current draft.
            string cloneName = UniqueRotationName($"{draft.Name} copy");
            SetEncounterPlayerPlanDraft(draft with { Id = "", Name = cloneName });
            AddChatMessage($"Rotation cloned as '{cloneName}' — edit, then Save & apply to store it.");
        }
        ImGui.SameLine();
        if (EncounterPanelButton("New rotation", compact: true))
        {
            SetEncounterPlayerPlanDraft(new CombatPlan(
                Name: UniqueRotationName("New rotation"),
                EnemyPriorities: [new CombatEnemyPriority(CombatEnemyKind.PrimaryEnemy)],
                Resources: new CombatResourcePolicy(),
                ClassId: DefaultClassForJob(actor.Job)));
            AddChatMessage("Started a blank rotation — edit, then Save & apply.");
        }
    }

    private string UniqueRotationName(string desired)
    {
        var names = new HashSet<string>(
            EncounterCombatPlanStoreRef.CharacterPlans.Values.Select(p => p.Name),
            StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(desired)) return desired;
        for (int n = 2; ; n++)
            if (!names.Contains($"{desired} {n}")) return $"{desired} {n}";
    }

    private void AssignRotationFromLibrary(int actorIndex, EncounterActorSpec actor, CombatPlan plan)
    {
        // Load it into the editable draft AND record the assignment on the body.
        _encounterPlayerPlanDraftKey = actor.Key;
        _encounterPlayerPlanBaseline = plan;
        _encounterPlayerPlanDraft = plan;
        _encounterPlayerPlanDirty = false;
        _encounterPlayerPlanUndo.Clear();
        _encounterPlayerPlanRedo.Clear();
        AssignBodySlots(actorIndex, actor, rotation: plan);
        // Broadcast: the rest of the selection takes the same assignment (their
        // own drafts are untouched — the draft belongs to the customized body).
        List<int> indices = GamePlanTargetIndices(actorIndex);
        foreach (int i in indices)
            if (i != actorIndex)
                AssignBodySlots(i, _encounterScenario[i], rotation: plan);
        AddChatMessage(indices.Count > 1
            ? $"{indices.Count} bodies: rotation '{plan.Name}' assigned."
            : $"{actor.Name}: rotation '{plan.Name}' assigned.");
    }

    private void DrawEncounterPositioningSlot(int actorIndex, EncounterActorSpec actor)
    {
        ImGui.SeparatorText("Positioning slot — where I stand (per role × side, per boss)");
        if (_encounterPositioningDraft is not { } script)
        {
            ImGui.TextDisabled("No positioning draft.");
            return;
        }

        string assignedId = actor.PlayerRules?.PositioningId ?? "";
        var library = EncounterPositioningStoreRef.Scripts;
        string preview = !string.IsNullOrEmpty(assignedId) && library.TryGetValue(assignedId, out PositioningScript? cur)
            ? cur.Name
            : $"{script.Name}{(_encounterPositioningDirty ? " (unsaved)" : "")}";

        ImGui.SetNextItemWidth(260f * CreatorUiScale);
        if (ImGui.BeginCombo("Assigned positioning", preview))
        {
            foreach ((string id, PositioningScript entry) in library.OrderBy(p => p.Value.Name))
            {
                bool selected = id == assignedId;
                string label = $"{entry.Name}##pos-{id}  [{entry.Role.ToString().ToLowerInvariant()} · {entry.Side.Label()}]";
                if (ImGui.Selectable(label, selected))
                    AssignPositioningFromLibrary(actorIndex, actor, entry);
                if (selected) ImGui.SetItemDefaultFocus();
            }
            if (library.Count == 0) ImGui.TextDisabled("no saved positioning scripts yet");
            ImGui.EndCombo();
        }

        // Name field + provenance warning when the spots came from another boss.
        string name = script.Name;
        ImGui.SetNextItemWidth(260f * CreatorUiScale);
        if (ImGui.InputText("Name##pos", ref name, 80)) SetPositioningDraft(script with { Name = name });
        if (script.HasPlacedSpots && script.EncounterKey is { } key &&
            _encounterDefinition is { } def && !string.Equals(key, def.Key, StringComparison.Ordinal))
            ImGui.TextColored(new Vector4(1f, .6f, .3f, 1f),
                $"! spots were placed against '{key}', not the loaded '{def.Key}'. Re-place per boss.");

        if (EncounterPanelButton("New##pos", compact: true))
        {
            _encounterPositioningDraft = new PositioningScript(
                Name: DefaultPositioningName(actor.Job, actor.Side),
                Role: actor.Job, Side: actor.Side,
                Movement: new CombatMovementPlan(),
                EncounterKey: _encounterDefinition?.Key);
            _encounterPositioningDirty = true;
        }
        ImGui.SameLine();
        if (EncounterPanelButton("Clone##pos", compact: true))
        {
            _encounterPositioningDraft = script with
            { Id = "", Name = $"{script.Name} copy" };
            _encounterPositioningDirty = true;
        }
        ImGui.SameLine();
        bool canMirror = script.Side is RaidSide.Left or RaidSide.Right;
        if (EncounterPanelButton($"Mirror -> {script.Side.Mirror().Label()}",
                enabled: canMirror, compact: true) && canMirror)
            MirrorPositioningDraft();

        DrawPositioningMovementDoctrine(script);
        DrawPositioningPhaseTable(script);

        ImGui.Separator();
        if (EncounterPanelButton("Save positioning & assign", enabled: _encounterPositioningDirty ||
                string.IsNullOrEmpty(assignedId)))
            SavePositioningDraftAndAssign(actorIndex, actor);
        ImGui.SameLine();
        ImGui.TextDisabled(_encounterPositioningDirty ? "unsaved changes" : "saved");
    }

    private void AssignPositioningFromLibrary(int actorIndex, EncounterActorSpec actor, PositioningScript entry)
    {
        _encounterPositioningDraft = entry;
        _encounterPositioningDraftKey = actor.Key;
        _encounterPositioningDirty = false;
        AssignBodySlots(actorIndex, actor, positioning: entry, side: entry.Side);
        AddChatMessage($"{actor.Name}: positioning '{entry.Name}' assigned.");
    }

    private void DrawPositioningMovementDoctrine(PositioningScript script)
    {
        ImGui.SeparatorText("Movement doctrine (spatial — lives here, not on the rotation)");
        CombatMovementPlan movement = script.Movement ?? new CombatMovementPlan();
        float cs = CreatorUiScale;

        int mode = (int)movement.Mode;
        ImGui.SetNextItemWidth(200f * cs);
        if (ImGui.Combo("Movement", ref mode, "Independent\0Hold position\0Follow\0"))
        {
            CombatMovementMode next = (CombatMovementMode)mode;
            CombatSubject? anchor = next == CombatMovementMode.Follow
                ? movement.Anchor ?? CombatSubject.Tank(1) : movement.Anchor;
            SetPositioningDraft(script with { Movement = movement with { Mode = next, Anchor = anchor } });
        }
        if (movement.Mode == CombatMovementMode.Follow)
        {
            CombatSubject anchor = movement.Anchor ?? CombatSubject.Tank(1);
            if (DrawEncounterSubjectCombo("Follow", anchor, allowLowestHealth: false,
                    out CombatSubject selected))
                SetPositioningDraft(script with { Movement = movement with { Anchor = selected } });
            float min = movement.MinRangeYards, max = movement.MaxRangeYards;
            ImGui.SetNextItemWidth(220f * cs);
            if (ImGui.SliderFloat("Min range", ref min, 0f, 40f, "%.0f yd"))
                SetPositioningDraft(script with
                { Movement = movement with { MinRangeYards = MathF.Min(min, max) } });
            ImGui.SetNextItemWidth(220f * cs);
            if (ImGui.SliderFloat("Max range", ref max, 1f, 60f, "%.0f yd"))
                SetPositioningDraft(script with
                { Movement = movement with { MaxRangeYards = MathF.Max(max, min) } });
        }
        bool face = movement.FacePrimaryEnemy;
        if (ImGui.Checkbox("Always face the primary encounter target", ref face))
            SetPositioningDraft(script with { Movement = movement with { FacePrimaryEnemy = face } });
    }

    private void DrawPositioningPhaseTable(PositioningScript script)
    {
        ImGui.SeparatorText("Per-phase positioning");
        if (_encounterDefinition is not { } definition || definition.Phases.Count == 0)
        {
            ImGui.TextDisabled("Load an encounter to author per-phase spots.");
            return;
        }
        ImGui.TextDisabled("Hold keeps the doctrine · Chase keeps melee reach · Spot runs to a placed point.");

        foreach (EncounterPhase phase in definition.Phases)
        {
            ImGui.PushID($"pos-phase-{phase.Key}");
            PositioningPhaseStep? step = script.Step(phase.Key);
            RaidDirectiveKind kind = step?.Kind ?? RaidDirectiveKind.Hold;

            ImGui.TextUnformatted(phase.Name);
            ImGui.SameLine(200f * CreatorUiScale);
            int k = (int)kind;
            ImGui.SetNextItemWidth(150f * CreatorUiScale);
            if (ImGui.Combo("##kind", ref k, "Hold\0Chase boss\0Move to spot\0"))
                SetPositioningPhaseKind(phase.Key, (RaidDirectiveKind)k);

            if ((RaidDirectiveKind)k == RaidDirectiveKind.MoveToSpot)
            {
                ImGui.SameLine();
                bool arming = _encounterPlacing == EncounterPlacement.PositioningSpot &&
                              _encounterPlacingPositioningPhase == phase.Key;
                if (EncounterPanelButton(arming ? "click ground…" : "Place spot",
                        compact: true))
                {
                    _encounterPlacing = EncounterPlacement.PositioningSpot;
                    _encounterPlacingPositioningPhase = phase.Key;
                    AddChatMessage($"Click the ground to place the {phase.Name} spot.");
                }
                if (step is { Kind: RaidDirectiveKind.MoveToSpot } placed)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled($"({placed.Spot.X:0.0}, {placed.Spot.Y:0.0})");
                }
                else
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(1f, .6f, .3f, 1f), "no spot yet");
                }
            }
            ImGui.PopID();
        }
    }
}
