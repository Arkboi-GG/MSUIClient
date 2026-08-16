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
                AddFootprintDiscs(discs, simEvent.Footprint!, FidelityTint(simEvent.Fidelity), 0.5f);

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
            draw.AddText(pixel + new Vector2(6f, -6f), colour, (++index).ToString());
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
            if (!actor.Alive) continue;
            if (!_window.Camera.TryWorldToScreen(actor.Position, display, out Vector2 pixel)) continue;

            uint colour = actor.Spec.Role switch
            {
                EncounterActorRole.Boss => ImGui.GetColorU32(new Vector4(1f, .55f, .3f, 1f)),
                EncounterActorRole.Add => ImGui.GetColorU32(new Vector4(1f, .8f, .35f, .9f)),
                _ => ImGui.GetColorU32(new Vector4(.5f, .9f, 1f, .95f)),
            };
            float size = actor.Spec.Role == EncounterActorRole.Boss ? 9f : 5f;
            draw.AddCircle(pixel, size, colour, 0, 2f);

            // Facing matters for every cone in the game; draw it.
            if (actor.Spec.Role == EncounterActorRole.Boss)
            {
                Vector3 nose = actor.Position +
                               EncounterGeometryLaw.Forward(actor.Facing) * MathF.Max(actor.Spec.BoundingRadius * 2f, 6f);
                if (_window.Camera.TryWorldToScreen(nose, display, out Vector2 tip))
                    draw.AddLine(pixel, tip, colour, 2f);
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
