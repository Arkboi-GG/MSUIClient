using System.Numerics;
using ImGuiNET;
using MSUIClient.World.Units;
using MSUIClient.World.Encounters;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Encounter Lab rendering, in the same two-pass shape the NPC dev window uses.
//
//   3-D pass  — real ground-projected decals for everything that is a DISC:
//               circles, projectile impacts, and every sphere of a breath lane.
//               These go through SpellEffectMeshRenderer.RenderGroundDiscs, so
//               they follow terrain AND WMO floors (GatherGroundEffectTriangles
//               gathers collision triangles as well as terrain — which is what
//               makes this usable inside Onyxia's lair at all).
//
//   screen pass — everything a decal cannot express: cone sectors, swept lines,
//               the flight route, actor markers, the probe capsule and labels.
//               Filled polygons on the background draw list.
//
// Colour is meaning here: every footprint is tinted by its FIDELITY, so a shape
// you cannot trust never looks like one you can.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class GameLoop
{
    /// <summary>Sample count for a projected cone arc. 24 is smooth at raid distances
    /// and cheap enough to draw a dozen of.</summary>
    private const int EncounterConeSegments = 24;

    // ── 3-D pass ─────────────────────────────────────────────────────────────

    private void RenderEncounterLab3D()
    {
        if (!_encounterLabOpen || _spellEffectMeshes is null) return;
        if (_encounterSim is not { } sim) return;
        var settings = Settings.EncounterLab;
        if (!settings.ShowFootprints && !settings.ShowStructural && !settings.ShowActors) return;

        _spellEffectMeshes.GatherGround ??= GatherGroundEffectTriangles;
        List<SpellEffectMeshRenderer.GroundDisc> discs = [];

        if (settings.ShowFootprints)
            foreach (SimEvent simEvent in ActiveEncounterFootprints(sim))
                AddFootprintDiscs(discs, simEvent.Footprint!, FidelityTint(simEvent.Fidelity), 0.68f);

        // Structural view: every catalogued footprint at once, ignoring timing. This
        // is the "where could this EVER land" question, which a seeded run cannot
        // answer on its own — one run is one roll of the dice.
        if (settings.ShowStructural && _encounterDefinition is { } definition && sim.Boss is { } boss)
        {
            foreach (EncounterAbility ability in definition.AbilitiesIn(sim.PhaseKey)
                         .Concat(definition.Abilities.Where(a =>
                             a.Trigger.Kind == EncounterTriggerKind.Manual)))
            {
                if (!ability.HasFootprint) continue;
                Footprint footprint = EncounterGeometryLaw.Resolve(
                    ability, boss.Position, boss.Facing, boss.Position, _encounterFacts);
                AddFootprintDiscs(discs, footprint, FidelityTint(ability.Fidelity), 0.16f);
            }
        }

        // THE PULL RING, until the moment it is crossed: the fight's start line,
        // drawn where the decision gets made. Gone once she is engaged.
        if (settings.ShowActors && sim.Boss is { } ringBoss &&
            (sim.EngagedAtMs < 0 || _encounterViewMs < sim.EngagedAtMs))
        {
            float ring = sim.Options.PullRangeYards;
            discs.Add(new SpellEffectMeshRenderer.GroundDisc(
                ringBoss.Position, ring * .96f, ring, new Vector3(1f, .45f, .25f), .8f));
        }

        // Every body as a ground decal at its REAL size - the boss disc is her
        // melee reach, which is what makes "am I inside the tail cone from here"
        // readable at a glance instead of a 9-pixel circle floating in space.
        if (settings.ShowActors)
        {
            foreach (SimActor actor in sim.Actors)
            {
                if (!actor.Alive) continue;
                bool isBoss = actor.Spec.Role == EncounterActorRole.Boss;
                float radius = isBoss
                    ? MathF.Max(actor.Spec.CombatReach, actor.Spec.BoundingRadius)
                    : MathF.Max(actor.Spec.BoundingRadius, .9f);
                discs.Add(new SpellEffectMeshRenderer.GroundDisc(
                    actor.Position, isBoss ? radius * .85f : 0f, radius,
                    ActorHitNow(sim, actor.Key) ? new Vector3(1f, .25f, .2f)
                    : isBoss ? new Vector3(1f, .55f, .3f)
                    : new Vector3(.45f, .85f, 1f),
                    isBoss ? .8f : .6f));
            }
        }

        // The probe body, as a ring on the ground.
        if (settings.ShowActors && _encounterProbe.Count > 0)
        {
            Vector3 at = _encounterProbe.PositionAt(_encounterViewMs);
            bool hitNow = _encounterProbeReport.Threats.Any(t =>
                t.Covered && Math.Abs(t.TimeMs - _encounterViewMs) <= sim.Options.StepMs * 4);
            discs.Add(new SpellEffectMeshRenderer.GroundDisc(
                at, _encounterProbe.Radius * .55f, _encounterProbe.Radius,
                hitNow ? new Vector3(1f, .3f, .25f) : new Vector3(.35f, .95f, 1f), .85f));
        }

        if (discs.Count > 0) _spellEffectMeshes.RenderGroundDiscs(_window.Camera, discs);
    }

    /// <summary>Discs are the only primitive that gets a true ground projection, so
    /// every shape that IS a disc uses one. Cones and lines fall to the screen pass.</summary>
    private void AddFootprintDiscs(
        List<SpellEffectMeshRenderer.GroundDisc> discs, Footprint footprint,
        Vector3 tint, float opacity)
    {
        switch (footprint.Kind)
        {
            case FootprintKind.Circle:
                discs.Add(new SpellEffectMeshRenderer.GroundDisc(
                    footprint.Origin, 0f, footprint.Radius, tint, opacity));
                break;
            case FootprintKind.Projectile:
                discs.Add(new SpellEffectMeshRenderer.GroundDisc(
                    footprint.End, 0f, footprint.Radius, tint, opacity));
                break;
            case FootprintKind.PointChain:
                foreach (Vector3 point in footprint.Points ?? [])
                    discs.Add(new SpellEffectMeshRenderer.GroundDisc(
                        point, 0f, footprint.Radius, tint, opacity));
                break;
        }
    }

    /// <summary>Was this body inside a landing within a few steps of the view? The
    /// red flash that turns "melee 2 was hit at 34.2s" from a log line into a thing
    /// seen happening.</summary>
    private bool ActorHitNow(EncounterSim sim, string actorKey) =>
        sim.Events.Any(e => e.Kind == SimEventKind.ActorHit && e.TargetKey == actorKey &&
                            Math.Abs(e.TimeMs - _encounterViewMs) <= sim.Options.StepMs * 4);

    private IEnumerable<SimEvent> ActiveEncounterFootprints(EncounterSim sim)
    {
        int linger = Math.Max(Settings.EncounterLab.FootprintLingerMs, 100);
        foreach (SimEvent simEvent in sim.Events)
        {
            if (simEvent.Footprint is null || simEvent.Kind != SimEventKind.CastLand) continue;
            if (simEvent.TimeMs > _encounterViewMs) continue;
            if (_encounterViewMs - simEvent.TimeMs > linger) continue;
            yield return simEvent;
        }
    }

    // ── screen pass ──────────────────────────────────────────────────────────

    private void DrawEncounterLabOverlay()
    {
        if (!_encounterLabOpen || _encounterSim is not { } sim) return;
        var settings = Settings.EncounterLab;
        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
        Vector2 display = ImGui.GetIO().DisplaySize;

        if (settings.ShowFootprints)
            foreach (SimEvent simEvent in ActiveEncounterFootprints(sim))
                DrawFootprintScreen(draw, display, simEvent.Footprint!,
                    FidelityColorU32(simEvent.Fidelity, 0.28f),
                    FidelityColorU32(simEvent.Fidelity, 0.95f));

        if (settings.ShowStructural && _encounterDefinition is { } definition && sim.Boss is { } boss)
        {
            foreach (EncounterAbility ability in definition.Abilities)
            {
                if (!ability.HasFootprint) continue;
                if (ability.Geometry.Kind is not (FootprintKind.Cone or FootprintKind.Line)) continue;
                Footprint footprint = EncounterGeometryLaw.Resolve(
                    ability, boss.Position, boss.Facing, boss.Position, _encounterFacts);
                DrawFootprintScreen(draw, display, footprint,
                    FidelityColorU32(ability.Fidelity, 0.10f),
                    FidelityColorU32(ability.Fidelity, 0.45f));
            }
        }

        if (settings.ShowRoute) DrawEncounterRoute(draw, display);
        if (settings.ShowActors) DrawEncounterActors(draw, display, sim);
        DrawEncounterOrientPreview(draw, display);   // the live free-spin arrow, if any
        if (settings.ShowLabels) DrawEncounterLabels(draw, display, sim);
        DrawEncounterPlacementBanner(draw, display);
        DrawEncounterLegend(draw, display, sim);
    }

    /// <summary>An armed placement announces itself in the middle of the screen.
    /// Silent placement modes read as broken buttons: the owner clicked "move",
    /// nothing said "now click the ground", and the order never happened.</summary>
    private void DrawEncounterPlacementBanner(ImDrawListPtr draw, Vector2 display)
    {
        if (_encounterPlacing == EncounterPlacement.None) return;
        string subject = _encounterScenario
            .FirstOrDefault(a => a.Key == _encounterPlacingActorKey)?.Name ?? "";
        string text = _encounterPlacing switch
        {
            EncounterPlacement.Boss => "CLICK THE GROUND to place the boss · right-click cancels",
            EncounterPlacement.Actor => $"CLICK THE GROUND to place {subject} · right-click cancels",
            EncounterPlacement.ActorMove =>
                $"CLICK THE GROUND: {subject} runs there at {_encounterViewMs / 1000f:0.0}s · right-click cancels",
            EncounterPlacement.Probe => "CLICK THE GROUND to place the probe · right-click cancels",
            EncounterPlacement.ProbeWaypoint =>
                $"CLICK THE GROUND: probe waypoint at {_encounterViewMs / 1000f:0.0}s · right-click cancels",
            _ => "",
        };
        if (text.Length == 0) return;
        Vector2 size = ImGui.CalcTextSize(text);
        Vector2 at = new(display.X * .5f - size.X * .5f, display.Y * .18f);
        draw.AddRectFilled(at - new Vector2(10f, 6f), at + size + new Vector2(10f, 6f),
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, .7f)), 6f);
        draw.AddText(at, ImGui.GetColorU32(new Vector4(1f, .85f, .4f, 1f)), text);
    }

    /// <summary>What every mark on screen means, on the screen it is on. Nobody
    /// alt-tabs to a manual to decode an overlay - the first human to see this
    /// tool called it, correctly, not usable without one of these.</summary>
    private void DrawEncounterLegend(ImDrawListPtr draw, Vector2 display, EncounterSim sim)
    {
        var settings = Settings.EncounterLab;
        var rows = new List<(Vector4 Colour, string Text)>();
        if (settings.ShowActors)
        {
            if (sim.EngagedAtMs < 0 || _encounterViewMs < sim.EngagedAtMs)
                rows.Add((new Vector4(1f, .45f, .25f, 1f),
                    "large ring = her pull range - a body crossing it starts the fight"));
            rows.Add((new Vector4(1f, .55f, .3f, 1f),
                $"{sim.Boss?.Spec.Name ?? "boss"} - disc is her melee reach, tick is her facing"));
            if (sim.Actors.Any(a => a.Spec.Role == EncounterActorRole.Friendly))
                rows.Add((new Vector4(.5f, .9f, 1f, 1f), "raid bodies (red flash = hit at this instant)"));
            if (_encounterProbe.Count > 0)
                rows.Add((new Vector4(.35f, .95f, 1f, 1f), "position probe + its walked path"));
        }
        if (settings.ShowRoute)
            rows.Add((new Vector4(.65f, .8f, 1f, 1f), "dashed = authored route (air points are flight)"));
        if (settings.ShowFootprints)
            rows.Add((new Vector4(.85f, .85f, .85f, 1f), "painted ground = ability landings · colour = data fidelity"));
        if (rows.Count == 0) return;

        float line = ImGui.GetTextLineHeight() + 4f;
        Vector2 corner = new(16f, display.Y - rows.Count * line - 24f);
        float width = rows.Max(r => ImGui.CalcTextSize(r.Text).X) + 34f;
        draw.AddRectFilled(corner - new Vector2(8f, 8f),
            corner + new Vector2(width, rows.Count * line + 6f),
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, .55f)), 6f);
        for (int i = 0; i < rows.Count; i++)
        {
            Vector2 at = corner + new Vector2(0f, i * line);
            draw.AddCircleFilled(at + new Vector2(6f, line * .45f), 5f,
                ImGui.GetColorU32(rows[i].Colour));
            draw.AddText(at + new Vector2(18f, 2f), ImGui.GetColorU32(rows[i].Colour),
                rows[i].Text);
        }
    }

    private void DrawFootprintScreen(
        ImDrawListPtr draw, Vector2 display, Footprint footprint, uint fill, uint outline)
    {
        switch (footprint.Kind)
        {
            case FootprintKind.Cone:
                DrawConeScreen(draw, display, footprint, fill, outline);
                break;
            case FootprintKind.Line:
                DrawLineScreen(draw, display, footprint, fill, outline);
                break;
            case FootprintKind.PointChain:
                // The discs are already drawn in 3-D; the connecting spine makes the
                // lane read as one sweep instead of a row of unrelated puddles.
                DrawChainSpine(draw, display, footprint, outline);
                break;
        }
    }

    /// <summary>
    /// A cone sector, projected point by point. The arc centre flips to the caster's
    /// back for a NEGATIVE cone — the spell_cone sign convention, drawn rather than
    /// merely stated, because "do not stand behind her" is the whole lesson of Tail
    /// Sweep and a front-facing wedge would teach the opposite.
    /// </summary>
    private void DrawConeScreen(
        ImDrawListPtr draw, Vector2 display, Footprint footprint, uint fill, uint outline)
    {
        float centre = footprint.IsRearCone ? footprint.Facing + MathF.PI : footprint.Facing;
        float half = MathF.Abs(footprint.ConeDegrees) * .5f * (MathF.PI / 180f);

        Span<Vector2> points = stackalloc Vector2[EncounterConeSegments + 2];
        int count = 0;
        if (!_window.Camera.TryWorldToScreen(footprint.Origin, display, out Vector2 apex)) return;
        points[count++] = apex;

        for (int i = 0; i <= EncounterConeSegments; i++)
        {
            float angle = centre - half + half * 2f * (i / (float)EncounterConeSegments);
            Vector3 edge = footprint.Origin + new Vector3(
                MathF.Cos(angle) * footprint.Radius, MathF.Sin(angle) * footprint.Radius, 0f);
            if (!_window.Camera.TryWorldToScreen(edge, display, out Vector2 pixel)) return;
            points[count++] = pixel;
        }

        unsafe
        {
            fixed (Vector2* data = points)
            {
                draw.AddConvexPolyFilled(ref data[0], count, fill);
                draw.AddPolyline(ref data[0], count, outline, ImDrawFlags.Closed, 1.8f);
            }
        }
    }

    private void DrawLineScreen(
        ImDrawListPtr draw, Vector2 display, Footprint footprint, uint fill, uint outline)
    {
        Vector3 direction = footprint.End - footprint.Origin;
        Vector2 flat = new(direction.X, direction.Y);
        if (flat.LengthSquared() < 1e-5f) return;
        Vector2 side = Vector2.Normalize(new Vector2(-flat.Y, flat.X)) * (footprint.Width * .5f);
        Vector3 offset = new(side.X, side.Y, 0f);

        Span<Vector3> corners =
        [
            footprint.Origin + offset, footprint.End + offset,
            footprint.End - offset, footprint.Origin - offset,
        ];
        Span<Vector2> pixels = stackalloc Vector2[4];
        for (int i = 0; i < 4; i++)
            if (!_window.Camera.TryWorldToScreen(corners[i], display, out pixels[i])) return;

        unsafe
        {
            fixed (Vector2* data = pixels)
            {
                draw.AddConvexPolyFilled(ref data[0], 4, fill);
                draw.AddPolyline(ref data[0], 4, outline, ImDrawFlags.Closed, 1.8f);
            }
        }
    }

    private void DrawChainSpine(ImDrawListPtr draw, Vector2 display, Footprint footprint, uint colour)
    {
        IReadOnlyList<Vector3> points = footprint.Points ?? [];
        Vector2? previous = null;
        foreach (Vector3 point in points)
        {
            if (!_window.Camera.TryProjectToScreen(point, display, out Vector2 pixel, out _)) { previous = null; continue; }
            if (previous is { } from) draw.AddLine(from, pixel, colour, 2f);
            previous = pixel;
        }
    }

    /// <summary>The boss's authored flight route: MoveTo steps from phase entries and
    /// transitions, as a dashed polyline with numbered stops.</summary>
    private void DrawEncounterRoute(ImDrawListPtr draw, Vector2 display)
    {
        if (_encounterDefinition is not { } definition) return;
        // Her flight route wears the boss's red, thick enough to read against the lair.
        var (colour, thickness) = EncounterRoleStyle(EncounterActorRole.Boss, RaidJob.None);
        int index = 0;
        Vector2? previous = null;

        foreach (EncounterStep step in RouteSteps(definition))
        {
            if (!_window.Camera.TryProjectToScreen(step.Point, display, out Vector2 pixel, out bool onScreen))
            {
                previous = null;
                continue;
            }
            if (previous is { } from) DrawDashedLineClipped(draw, from, pixel, display, colour, 6f, 4f, thickness);
            previous = pixel;
            if (!onScreen) continue;   // the line rides through; dots/labels wait for the frame
            draw.AddCircleFilled(pixel, 4f, colour);
            // The first stop names the whole polyline. Unlabelled, these points
            // hang in the AIR (they are flight waypoints) and read as noise.
            draw.AddText(pixel + new Vector2(6f, -6f), colour,
                ++index == 1 ? "1  authored route (phase moves / flight)" : index.ToString());
        }
    }

    private static IEnumerable<EncounterStep> RouteSteps(EncounterDefinition definition)
    {
        foreach (EncounterPhase phase in definition.Phases)
        {
            foreach (EncounterStep step in phase.OnEnter ?? [])
                if (step.Kind == EncounterStepKind.MoveTo) yield return step;
            foreach (EncounterTransition transition in phase.Transitions ?? [])
                foreach (EncounterStep step in transition.Steps ?? [])
                    if (step.Kind == EncounterStepKind.MoveTo) yield return step;
        }
    }

    private void DrawEncounterActors(ImDrawListPtr draw, Vector2 display, EncounterSim sim)
    {
        foreach (SimActor actor in sim.Actors)
        {
            if (!_window.Camera.TryWorldToScreen(actor.Position, display, out Vector2 pixel)) continue;

            // A dead body stays on screen as a grey cross where it died - "who
            // did that arrangement kill" is the question the scenario answers,
            // and a body that vanishes on death answers it with a shrug.
            if (!actor.Alive)
            {
                uint grey = ImGui.GetColorU32(new Vector4(.7f, .7f, .7f, .9f));
                draw.AddLine(pixel + new Vector2(-5f, -5f), pixel + new Vector2(5f, 5f), grey, 2f);
                draw.AddLine(pixel + new Vector2(-5f, 5f), pixel + new Vector2(5f, -5f), grey, 2f);
                draw.AddText(pixel + new Vector2(8f, -8f), grey, $"{actor.Spec.Name} (dead)");
                continue;
            }

            var (roleColour, roleThickness) = EncounterRoleStyle(actor.Spec.Role, actor.Spec.Job);
            bool hitNow = ActorHitNow(sim, actor.Key);
            // The hit flash is a bright white-gold now that the boss owns red.
            uint colour = hitNow ? ImGui.GetColorU32(new Vector4(1f, .95f, .4f, 1f)) : roleColour;
            float size = actor.Spec.Role == EncounterActorRole.Boss ? 9f : 5f;
            draw.AddCircle(pixel, size, colour, 0, roleThickness);

            // The boss carries a health BAR above her mark - the health gates drive her
            // phases, so how full she is IS the state of the fight, readable at a glance.
            if (actor.Spec.Role == EncounterActorRole.Boss)
            {
                float hp = Math.Clamp(actor.HealthFraction, 0f, 1f);
                Vector2 barSize = new(72f, 8f);
                Vector2 barMin = pixel + new Vector2(-barSize.X * .5f, -size - 20f);
                Vector2 barMax = barMin + barSize;
                uint fill = hp > .5f ? ImGui.GetColorU32(new Vector4(.85f, .30f, .28f, 1f))
                          : hp > .2f ? ImGui.GetColorU32(new Vector4(.90f, .55f, .20f, 1f))
                                     : ImGui.GetColorU32(new Vector4(.95f, .80f, .20f, 1f));
                draw.AddRectFilled(barMin, barMax, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, .6f)));
                draw.AddRectFilled(barMin, new Vector2(barMin.X + barSize.X * hp, barMax.Y), fill);
                draw.AddRect(barMin, barMax, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, .85f)), 0f, 0, 1.5f);
            }

            // The assigned aggro holder wears the mark: she is facing THIS body,
            // and every cone on the floor is anchored to that fact.
            if (actor.Key == AggroHolderKeyAt(_encounterViewMs))
            {
                uint aggro = ImGui.GetColorU32(new Vector4(1f, .45f, .35f, 1f));
                draw.AddCircle(pixel, size + 5f, aggro, 0, 2.5f);
                draw.AddText(pixel + new Vector2(-24f, size + 8f), aggro, "AGGRO");
            }

            // Every mark gets its name. The boss also carries her health, and any
            // body that has been inside a landing carries the count - the scenario
            // verdict, readable off the world itself.
            string label = actor.Spec.Role == EncounterActorRole.Boss
                ? $"{actor.Spec.Name}  {actor.HealthFraction * 100f:0}%"
                : actor.HitsTaken > 0
                    ? $"{actor.Spec.Name} · {actor.HitsTaken} hit{(actor.HitsTaken == 1 ? "" : "s")}"
                    : actor.Spec.Name;
            draw.AddText(pixel + new Vector2(size + 5f, -size - 3f), colour, label);

            // Facing matters for every cone in the game; draw it.
            if (actor.Spec.Role == EncounterActorRole.Boss)
            {
                Vector3 nose = actor.Position +
                               EncounterGeometryLaw.Forward(actor.Facing) * MathF.Max(actor.Spec.BoundingRadius * 2f, 6f);
                if (_window.Camera.TryWorldToScreen(nose, display, out Vector2 tip))
                    draw.AddLine(pixel, tip, colour, roleThickness);
            }
        }

        // Each body's ordered plan as a dashed path, labelled the way each order
        // fires - the raid plan drawn on the floor it will be executed on.
        // Teleport what-ifs stand apart in orange: they are questions, not walks.
        uint teleportColour = ImGui.GetColorU32(new Vector4(1f, .72f, .35f, .9f));
        foreach (SimActor actor in sim.Actors)
        {
            if (actor.Spec.Moves is not { Count: > 0 } moves) continue;
            var (roleColour, roleThickness) = EncounterRoleStyle(actor.Spec.Role, actor.Spec.Job);
            Vector2? from = _window.Camera.TryProjectToScreen(actor.Spec.Position, display, out Vector2 start, out _)
                ? start : null;
            foreach (TimedMove move in moves)
            {
                if (!_window.Camera.TryProjectToScreen(move.Position, display, out Vector2 to, out bool onScreen))
                { from = null; continue; }
                uint colour = move.Teleport ? teleportColour : roleColour;
                if (from is { } a && !move.Teleport)
                    DrawDashedLineClipped(draw, a, to, display, colour, 5f, 4f, roleThickness);
                from = to;
                if (!onScreen) continue;
                draw.AddCircleFilled(to, 4f, colour);
                string when = move.Anchor switch
                {
                    MoveAnchor.AfterPrevious => "then",
                    MoveAnchor.OnPhaseEnter => $"on {move.PhaseKey}",
                    _ => $"@ {move.TimeMs / 1000f:0.0}s",
                };
                draw.AddText(to + new Vector2(5f, 4f), colour,
                    move.Teleport ? $"{actor.Spec.Name} what-if {when}"
                                  : $"{actor.Spec.Name} {when}");
                // The authored arrival facing as a ground orientation ring + arrow -
                // the tank's back-to-the-wall, visible before the run is ever made.
                if (move.HasArrivalFacing)
                    DrawWaypointOrientation(draw, move.Position, move.ArrivalFacing, colour, display, roleThickness);
            }
        }

        // THE STAGED PLAN: waypoints queued for GO, dotted from where each body
        // stands right now — the "where will everyone go" picture, readable
        // before anything is committed. Tight dashes and a cool tint keep it
        // visually distinct from committed (green) orders.
        if (_encounterStagedOrders.Count > 0)
        {
            foreach ((string key, var legs) in _encounterStagedOrders)
            {
                if (legs.Count == 0) continue;
                SimActor? actor = sim.Actors.FirstOrDefault(a => a.Key == key);
                if (actor is null) continue;
                var (planColour, planThickness) = EncounterRoleStyle(actor.Spec.Role, actor.Spec.Job);
                Vector2? from = _window.Camera.TryProjectToScreen(actor.Position, display,
                    out Vector2 start, out _) ? start : null;
                for (int i = 0; i < legs.Count; i++)
                {
                    if (!_window.Camera.TryProjectToScreen(legs[i].Position, display,
                            out Vector2 to, out bool onScreen))
                    { from = null; continue; }
                    if (from is { } a) DrawDashedLineClipped(draw, a, to, display, planColour, 3f, 5f, planThickness);
                    from = to;
                    if (!onScreen) continue;
                    draw.AddCircle(to, 5f, planColour, 0, planThickness);
                    draw.AddText(to + new Vector2(7f, -6f), planColour,
                        i == 0 ? $"{actor.Spec.Name} plan 1" : $"{i + 1}");
                    if (legs[i].HasFacing)
                        DrawWaypointOrientation(draw, legs[i].Position, legs[i].ArrivalFacing, planColour, display, planThickness);
                }
            }
        }

        // The probe trajectory as a walked path.
        if (_encounterProbe.Count > 1)
        {
            uint probeColour = ImGui.GetColorU32(new Vector4(.35f, .95f, 1f, .8f));
            Vector2? previous = null;
            foreach ((int timeMs, Vector3 position) in _encounterProbe.Waypoints)
            {
                if (!_window.Camera.TryProjectToScreen(position, display, out Vector2 pixel, out bool onScreen)) { previous = null; continue; }
                if (previous is { } from) DrawDashedLineClipped(draw, from, pixel, display, probeColour, 5f, 4f);
                previous = pixel;
                if (!onScreen) continue;
                draw.AddCircleFilled(pixel, 3f, probeColour);
                draw.AddText(pixel + new Vector2(5f, 4f), probeColour, $"{timeMs / 1000f:0.0}s");
            }
        }
    }

    private void DrawEncounterLabels(ImDrawListPtr draw, Vector2 display, EncounterSim sim)
    {
        foreach (SimEvent simEvent in ActiveEncounterFootprints(sim))
        {
            Footprint footprint = simEvent.Footprint!;
            Vector3 anchor = footprint.Kind switch
            {
                FootprintKind.PointChain when footprint.Points is { Count: > 0 } points =>
                    points[points.Count / 2],
                FootprintKind.Projectile => footprint.End,
                _ => footprint.Origin,
            };
            if (!_window.Camera.TryWorldToScreen(anchor, display, out Vector2 pixel)) continue;
            uint colour = FidelityColorU32(simEvent.Fidelity, 1f);
            draw.AddText(pixel + new Vector2(8f, -10f), colour, simEvent.Text);
        }

        // The scrub head, always legible, top-left of the viewport. The clock is the
        // COMBAT clock - it waits for the pull, not the load (see EncounterFightClock) - and
        // the ▶ glyph only shows when time is actually advancing, so a held pre-pull reads
        // as held, not running.
        bool running = _encounterPlaying && sim.EngagedAtMs >= 0;
        string head = $"{EncounterFightClock(sim)}  ·  " +
                      $"{sim.Definition.Phase(sim.PhaseKey)?.Name ?? sim.PhaseKey}" +
                      (running ? "  ▶" : "  ‖");
        draw.AddText(new Vector2(display.X * .5f - 60f, 24f),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, .85f)), head);
    }

    // ── shared helpers ───────────────────────────────────────────────────────

    // Dashed lines come from GameLoop.Control.cs's shared DrawDashedLine(draw, from,
    // to, colour, dashLength, gapLength) — the repo's "call it, do not duplicate it"
    // rule applies to six-line helpers too.

    /// <summary>A dashed line between two projected points, CLIPPED to the viewport first.
    /// Overlay paths now project points that are in front of the camera but off-screen (so
    /// a plan survives the camera flying past it); without this clip, DrawDashedLine would
    /// step its dash/gap loop across the tens of thousands of off-screen pixels such a point
    /// can land at, once per frame. Clipping bounds the walk to what is actually visible.</summary>
    private static void DrawDashedLineClipped(ImDrawListPtr draw, Vector2 a, Vector2 b,
        Vector2 display, uint colour, float dash, float gap, float thickness = 2f)
    {
        if (ClipSegmentToRect(ref a, ref b, -48f, -48f, display.X + 48f, display.Y + 48f))
            DrawDashedLine(draw, a, b, colour, dash, gap, thickness);
    }

    /// <summary>Colour and line weight for a body's plan by its role — the owner's key:
    /// tanks gold and thickest, healers green, dps cream, the boss red. A body's dots, its
    /// dashed plan and its orientation arrow all wear this so a glance reads the whole raid.</summary>
    private static (uint colour, float thickness) EncounterRoleStyle(EncounterActorRole role, RaidJob job)
    {
        if (role == EncounterActorRole.Boss)
            return (ImGui.GetColorU32(new Vector4(1f, .30f, .26f, 1f)), 3.5f);   // red
        return job switch
        {
            RaidJob.Tank => (ImGui.GetColorU32(new Vector4(1f, .82f, .22f, 1f)), 4.5f),           // gold, thickest
            RaidJob.Healer => (ImGui.GetColorU32(new Vector4(.38f, .92f, .45f, 1f)), 3.5f),        // green
            RaidJob.Melee or RaidJob.Ranged => (ImGui.GetColorU32(new Vector4(1f, .95f, .80f, 1f)), 2.5f), // cream
            _ => (ImGui.GetColorU32(new Vector4(.6f, .85f, 1f, 1f)), 2.5f),                        // neutral
        };
    }

    /// <summary>The live free-spin arrow while a waypoint is grabbed: the orientation ring +
    /// arrow drawn at the cursor-dictated angle, with a one-line hint, so the owner sees the
    /// facing sweep continuously before committing it with a click.</summary>
    private void DrawEncounterOrientPreview(ImDrawListPtr draw, Vector2 display)
    {
        if (!_encounterOrientSpinning || float.IsNaN(_encounterOrientFacing)) return;
        uint colour = ImGui.GetColorU32(new Vector4(1f, .85f, .25f, 1f));
        DrawWaypointOrientation(draw, _encounterOrientAnchor, _encounterOrientFacing, colour, display, 3.5f);
        if (_window.Camera.TryProjectToScreen(_encounterOrientAnchor, display, out Vector2 dot, out _))
            draw.AddText(dot + new Vector2(13f, 11f), colour,
                $"{EncounterFacingLabel(_encounterOrientFacing)} — move to aim, click to set");
    }

    /// <summary>A waypoint's orientation, drawn FLAT ON THE GROUND: a ring on the terrain
    /// plane with an arrow lying past it in the facing direction. Every point is a world
    /// position projected through the camera, so the whole thing sits on the floor and turns
    /// with the terrain (not a flat badge on the camera plane), and it scales with distance
    /// like every other footprint. Sizes are in YARDS so it stays legible, not a few pixels.</summary>
    private void DrawWaypointOrientation(ImDrawListPtr draw, Vector3 world, float facing,
        uint colour, Vector2 display, float thickness = 3f)
    {
        if (float.IsNaN(facing)) return;

        const float ringYd = 2.2f;    // ground ring radius
        const float arrowYd = 3.4f;   // arrow reach past the ring
        const int segments = 28;

        // The ring, sampled around the ground plane and connected in screen space.
        Vector2 prev = default;
        bool havePrev = false;
        for (int i = 0; i <= segments; i++)
        {
            float a = i / (float)segments * MathF.Tau;
            Vector3 p = world + new Vector3(MathF.Cos(a), MathF.Sin(a), 0f) * ringYd;
            if (!_window.Camera.TryProjectToScreen(p, display, out Vector2 px, out _)) { havePrev = false; continue; }
            if (havePrev) draw.AddLine(prev, px, colour, thickness);
            prev = px;
            havePrev = true;
        }

        // The arrow, in the ground plane: from the ring edge out along the facing, with a
        // head built from world offsets so it, too, lies flat and turns with the terrain.
        Vector3 fwd = EncounterGeometryLaw.Forward(facing);
        Vector3 side = new(-fwd.Y, fwd.X, 0f);
        Vector3 tailW = world + fwd * ringYd;
        Vector3 tipW = world + fwd * (ringYd + arrowYd);
        Vector3 leftW = tipW - fwd * 1.5f + side * 1.1f;
        Vector3 rightW = tipW - fwd * 1.5f - side * 1.1f;
        if (!_window.Camera.TryProjectToScreen(tailW, display, out Vector2 tail, out _) ||
            !_window.Camera.TryProjectToScreen(tipW, display, out Vector2 tip, out _))
            return;
        draw.AddLine(tail, tip, colour, thickness + 0.5f);
        if (_window.Camera.TryProjectToScreen(leftW, display, out Vector2 lp, out _) &&
            _window.Camera.TryProjectToScreen(rightW, display, out Vector2 rp, out _))
            draw.AddTriangleFilled(tip, lp, rp, colour);
    }

    /// <summary>Liang–Barsky clip of segment a→b to the axis-aligned rect. Returns false when
    /// the segment lies wholly outside; otherwise a and b are moved to the clipped endpoints.</summary>
    private static bool ClipSegmentToRect(ref Vector2 a, ref Vector2 b,
        float minX, float minY, float maxX, float maxY)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float t0 = 0f, t1 = 1f;

        bool Accept(float p, float q)
        {
            if (p == 0f) return q >= 0f;            // parallel to this edge: in iff q ≥ 0
            float r = q / p;
            if (p < 0f) { if (r > t1) return false; if (r > t0) t0 = r; }
            else        { if (r < t0) return false; if (r < t1) t1 = r; }
            return true;
        }

        if (!Accept(-dx, a.X - minX) || !Accept(dx, maxX - a.X) ||
            !Accept(-dy, a.Y - minY) || !Accept(dy, maxY - a.Y))
            return false;

        Vector2 origin = a;
        a = new Vector2(origin.X + t0 * dx, origin.Y + t0 * dy);
        b = new Vector2(origin.X + t1 * dx, origin.Y + t1 * dy);
        return true;
    }

    private static Vector3 FidelityTint(EncounterFidelity fidelity) => fidelity switch
    {
        EncounterFidelity.ExactDb => new Vector3(.45f, 1f, .55f),
        EncounterFidelity.DeclaredCppManifest => new Vector3(.5f, .8f, 1f),
        EncounterFidelity.DerivedDbc => new Vector3(.9f, .9f, .5f),
        EncounterFidelity.Heuristic => new Vector3(1f, .72f, .35f),
        _ => new Vector3(1f, .38f, .32f),
    };

    private static uint FidelityColorU32(EncounterFidelity fidelity, float alpha)
    {
        Vector3 tint = FidelityTint(fidelity);
        return ImGui.GetColorU32(new Vector4(tint.X, tint.Y, tint.Z, alpha));
    }
}
