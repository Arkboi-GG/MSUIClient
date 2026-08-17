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
            if (!_window.Camera.TryWorldToScreen(point, display, out Vector2 pixel)) { previous = null; continue; }
            if (previous is { } from) draw.AddLine(from, pixel, colour, 2f);
            previous = pixel;
        }
    }

    /// <summary>The boss's authored flight route: MoveTo steps from phase entries and
    /// transitions, as a dashed polyline with numbered stops.</summary>
    private void DrawEncounterRoute(ImDrawListPtr draw, Vector2 display)
    {
        if (_encounterDefinition is not { } definition) return;
        uint colour = ImGui.GetColorU32(new Vector4(.65f, .8f, 1f, .8f));
        int index = 0;
        Vector2? previous = null;

        foreach (EncounterStep step in RouteSteps(definition))
        {
            if (!_window.Camera.TryWorldToScreen(step.Point, display, out Vector2 pixel))
            {
                previous = null;
                continue;
            }
            if (previous is { } from) DrawDashedLine(draw, from, pixel, colour, 6f, 4f);
            draw.AddCircleFilled(pixel, 4f, colour);
            // The first stop names the whole polyline. Unlabelled, these points
            // hang in the AIR (they are flight waypoints) and read as noise.
            draw.AddText(pixel + new Vector2(6f, -6f), colour,
                ++index == 1 ? "1  authored route (phase moves / flight)" : index.ToString());
            previous = pixel;
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

            bool hitNow = ActorHitNow(sim, actor.Key);
            uint colour = hitNow
                ? ImGui.GetColorU32(new Vector4(1f, .3f, .25f, 1f))
                : actor.Spec.Role switch
                {
                    EncounterActorRole.Boss => ImGui.GetColorU32(new Vector4(1f, .55f, .3f, 1f)),
                    EncounterActorRole.Add => ImGui.GetColorU32(new Vector4(1f, .8f, .35f, .9f)),
                    _ => ImGui.GetColorU32(new Vector4(.5f, .9f, 1f, .95f)),
                };
            float size = actor.Spec.Role == EncounterActorRole.Boss ? 9f : 5f;
            draw.AddCircle(pixel, size, colour, 0, 2f);

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
                    draw.AddLine(pixel, tip, colour, 2f);
            }
        }

        // Each body's ordered plan as a dashed path with the order times - the
        // raid plan drawn on the floor it will be executed on.
        uint orderColour = ImGui.GetColorU32(new Vector4(.55f, .95f, .6f, .85f));
        foreach (SimActor actor in sim.Actors)
        {
            if (actor.Spec.Moves is not { Count: > 0 } moves) continue;
            Vector2? from = _window.Camera.TryWorldToScreen(actor.Spec.Position, display, out Vector2 start)
                ? start : null;
            foreach (TimedMove move in moves)
            {
                if (!_window.Camera.TryWorldToScreen(move.Position, display, out Vector2 to))
                { from = null; continue; }
                if (from is { } a) DrawDashedLine(draw, a, to, orderColour, 5f, 4f);
                draw.AddCircleFilled(to, 3f, orderColour);
                draw.AddText(to + new Vector2(5f, 4f), orderColour,
                    $"{actor.Spec.Name} @ {move.TimeMs / 1000f:0.0}s");
                from = to;
            }
        }

        // The probe trajectory as a walked path.
        if (_encounterProbe.Count > 1)
        {
            uint probeColour = ImGui.GetColorU32(new Vector4(.35f, .95f, 1f, .8f));
            Vector2? previous = null;
            foreach ((int timeMs, Vector3 position) in _encounterProbe.Waypoints)
            {
                if (!_window.Camera.TryWorldToScreen(position, display, out Vector2 pixel)) { previous = null; continue; }
                if (previous is { } from) DrawDashedLine(draw, from, pixel, probeColour, 5f, 4f);
                draw.AddCircleFilled(pixel, 3f, probeColour);
                draw.AddText(pixel + new Vector2(5f, 4f), probeColour, $"{timeMs / 1000f:0.0}s");
                previous = pixel;
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

        // The scrub head, always legible, top-left of the viewport.
        string head = $"{_encounterViewMs / 1000f:0.0}s  ·  " +
                      $"{sim.Definition.Phase(sim.PhaseKey)?.Name ?? sim.PhaseKey}" +
                      (_encounterPlaying ? "  ▶" : "  ‖");
        draw.AddText(new Vector2(display.X * .5f - 60f, 24f),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, .85f)), head);
    }

    // ── shared helpers ───────────────────────────────────────────────────────

    // Dashed lines come from GameLoop.Control.cs's shared DrawDashedLine(draw, from,
    // to, colour, dashLength, gapLength) — the repo's "call it, do not duplicate it"
    // rule applies to six-line helpers too.

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
