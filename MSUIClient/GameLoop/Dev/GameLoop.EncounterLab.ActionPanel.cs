using System.Numerics;
using ImGuiNET;
using MSUIClient.World.Encounters;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Encounter Lab — live "action step-through" pop-out.
//
// A second chrome window that FOLLOWS the selected body (a clicked raid puppet in
// the free view, or the boss by default) and shows exactly what it is stepping
// through as the sim plays: Onyxia's cleave / tail sweep / flame breath / spells,
// her phase turns, and what a raid body is hit by — timestamped relative to the
// scrub head, the current step marked ▶, upcoming actions listed ahead. Every row
// is a sim.Event; the fight is pre-simulated, so "what she is about to do" is real,
// not a guess. No new sim instrumentation — this is pure presentation.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class GameLoop
{
    private bool _encounterActionPanelOpen;

    /// <summary>The sim actor the action panel follows: an RTS/freecam selection first, then
    /// the world-inspect selection, else the boss. Mapped back through the puppet guid table.</summary>
    private string? EncounterFollowedActorKey(EncounterSim sim)
    {
        foreach (ulong guid in _freecamSelection)
            foreach ((string key, ulong g) in _encounterPuppets)
                if (g == guid) return key;
        if (_selectionGuid != 0)
            foreach ((string key, ulong g) in _encounterPuppets)
                if (g == _selectionGuid) return key;
        return sim.Boss?.Key;
    }

    /// <summary>Drawn every frame from the HUD, right after the Lab window. Independent chrome
    /// so it can sit beside the Lab (or anywhere) while the fight plays.</summary>
    private void DrawEncounterActionPanel()
    {
        if (!_encounterActionPanelOpen) return;

        float cs = CreatorUiScale;
        float s = MathF.Max(ImGui.GetIO().DisplaySize.Y / GlueCanvasH, 0.5f) * cs;
        ImGui.SetNextWindowPos(new Vector2(510f * s, 64f * s), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(360f * s, 470f * s), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(260f * cs, 200f * cs),
            new Vector2(float.MaxValue, float.MaxValue));

        PushCreatorStyle();
        if (!ImGui.Begin("###encounter-actions", CreatorChromeFlags))
        {
            ImGui.End();
            PopCreatorStyle();
            return;
        }
        ClampCreatorWindowOnScreen();
        if (DrawCreatorPanelChrome("Action Timeline", "encounter-actions"))
            _encounterActionPanelOpen = false;
        ImGui.SetWindowFontScale(CreatorTextScale);
        BeginCreatorContent();

        DrawEncounterActionPanelBody();

        EndCreatorContent();
        ImGui.SetWindowFontScale(1f);
        ImGui.End();
        PopCreatorStyle();
    }

    private void DrawEncounterActionPanelBody()
    {
        if (_encounterSim is not { } sim)
        {
            ImGui.TextDisabled("open an encounter in the Lab (Ctrl+E) and play it");
            return;
        }

        string? key = EncounterFollowedActorKey(sim);
        SimActor? actor = key is null ? null : sim.Actors.FirstOrDefault(a => a.Key == key);
        if (actor is null || key is null)
        {
            ImGui.TextDisabled("no target — click a body in the free view (Ctrl+F)");
            return;
        }

        // Who we follow, and how to change it.
        ImGui.TextColored(RoleColourVec4(actor.Spec.Role, actor.Spec.Job), actor.Spec.Name);
        ImGui.SameLine();
        ImGui.TextDisabled(actor.Spec.Job == RaidJob.None ? $"({actor.Spec.Role})" : $"({actor.Spec.Job})");
        ImGui.TextDisabled("follows your selection — click a body in Ctrl+F to switch");

        // Clock, phase, and (boss) a health bar.
        ImGui.Text($"{EncounterFightClock(sim)}  ·  {sim.Definition.Phase(sim.PhaseKey)?.Name ?? sim.PhaseKey}");
        if (actor.Spec.Role == EncounterActorRole.Boss)
        {
            float hp = Math.Clamp(actor.HealthFraction, 0f, 1f);
            Vector4 bar = hp > .5f ? new Vector4(.85f, .30f, .28f, 1f)
                        : hp > .2f ? new Vector4(.90f, .55f, .20f, 1f)
                                   : new Vector4(.95f, .80f, .20f, 1f);
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, bar);
            ImGui.ProgressBar(hp, new Vector2(-1f, 14f * CreatorUiScale), $"{hp * 100f:0}%");
            ImGui.PopStyleColor();
        }

        // "NOW": the latest thing this body was doing at/just-before the view instant.
        SimEvent? now = null;
        foreach (SimEvent e in sim.Events)
        {
            if (e.TimeMs > _encounterViewMs) break;
            if (e.ActorKey == key && e.Kind is SimEventKind.CastStart or SimEventKind.PhaseEnter
                    or SimEventKind.Move or SimEventKind.Say or SimEventKind.Summon or SimEventKind.Aggro)
                now = e;
        }
        ImGui.Separator();
        if (now is not null)
            ImGui.TextColored(new Vector4(1f, .85f, .35f, 1f), $"NOW   {now.Text}");
        else
            ImGui.TextDisabled(sim.EngagedAtMs < 0 || _encounterViewMs < sim.EngagedAtMs
                ? "NOW   waiting for the pull" : "NOW   —");

        // The stepped stream: everything this body DOES (and is hit BY), windowed around the
        // scrub head, the current step marked ▶, upcoming actions below with a lead time.
        const int windowMs = 20_000;
        List<SimEvent> stream = sim.Events
            .Where(e => (e.ActorKey == key || e.TargetKey == key) &&
                        Math.Abs(e.TimeMs - _encounterViewMs) <= windowMs)
            .OrderBy(e => e.TimeMs)
            .ToList();

        ImGui.Separator();
        if (stream.Count == 0)
        {
            ImGui.TextDisabled(sim.EngagedAtMs < 0
                ? "nothing yet — order a body into her ring to start the fight"
                : "nothing near this instant");
            return;
        }

        if (ImGui.BeginChild("##enc-actionstream", new Vector2(0f, 0f), true))
        {
            foreach (SimEvent e in stream)
            {
                bool isNow = Math.Abs(e.TimeMs - _encounterViewMs) <= sim.Options.StepMs;
                bool future = e.TimeMs > _encounterViewMs;
                Vector4 color = e.Kind switch
                {
                    SimEventKind.CastStart => new Vector4(1f, .80f, .35f, 1f),
                    SimEventKind.CastLand => FidelityColor(e.Fidelity),
                    SimEventKind.ActorHit => new Vector4(1f, .40f, .35f, 1f),
                    SimEventKind.PhaseEnter => new Vector4(.6f, .85f, 1f, 1f),
                    SimEventKind.Aggro => new Vector4(1f, .50f, .40f, 1f),
                    SimEventKind.Say => new Vector4(.85f, .80f, .55f, 1f),
                    SimEventKind.Death => new Vector4(.80f, .80f, .80f, 1f),
                    _ => new Vector4(.72f, .72f, .72f, 1f),
                };
                if (!isNow) color.W = future ? .85f : .5f;

                string marker = isNow ? "▶" : future ? "·" : " ";
                string rel = e.TimeMs >= _encounterViewMs
                    ? $"+{(e.TimeMs - _encounterViewMs) / 1000f:0.0}"
                    : $"-{(_encounterViewMs - e.TimeMs) / 1000f:0.0}";
                ImGui.TextColored(color, $"{marker} {rel,6}s  {e.Text}");
                if (e.Kind == SimEventKind.CastLand && e.Fidelity != EncounterFidelity.ExactDb &&
                    ImGui.IsItemHovered())
                    ImGui.SetTooltip(EncounterSchema.Describe(e.Fidelity));
            }
        }
        ImGui.EndChild();
    }

    /// <summary>The role/job plan colour as a Vector4 (the overlay's EncounterRoleStyle returns
    /// a packed uint; this mirrors its palette for ImGui text).</summary>
    private static Vector4 RoleColourVec4(EncounterActorRole role, RaidJob job)
    {
        if (role == EncounterActorRole.Boss) return new Vector4(1f, .30f, .26f, 1f);
        return job switch
        {
            RaidJob.Tank => new Vector4(1f, .82f, .22f, 1f),
            RaidJob.Healer => new Vector4(.38f, .92f, .45f, 1f),
            RaidJob.Melee or RaidJob.Ranged => new Vector4(1f, .95f, .80f, 1f),
            _ => new Vector4(.6f, .85f, 1f, 1f),
        };
    }
}
