using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;

namespace MSUIClient;

/// <summary>
/// Live, non-blocking spell-FX isolation surface. It deliberately is a window rather than
/// an ImGui popup modal: a popup would capture input and prevent the casts it is meant to inspect.
/// </summary>
public sealed partial class GameLoop
{
    private bool _spellFxInspectorOpen = true;
    private bool _spellFxInspectorLocked;
    private bool _spellFxInspectorKeyDown;

    private void UpdateSpellFxInspectorInput(bool typing)
    {
        bool down = _window.IsDown(Key.F7);
        if (down && !_spellFxInspectorKeyDown && !typing && _config.DevTools)
            _spellFxInspectorOpen = !_spellFxInspectorOpen;
        _spellFxInspectorKeyDown = down;
    }

    private void DrawSpellFxInspector()
    {
        if (!_spellFxInspectorOpen) return;
        ImGui.SetNextWindowPos(new Vector2(455, 12), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(560, 620), ImGuiCond.FirstUseEver);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse;
        if (_spellFxInspectorLocked) flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;
        bool open = _spellFxInspectorOpen;
        if (!ImGui.Begin("Spell FX Inspector", ref open, flags))
        {
            _spellFxInspectorOpen = open;
            ImGui.End();
            return;
        }
        _spellFxInspectorOpen = open;

        ImGui.TextWrapped("Cast while this stays open. The rows distinguish authored/live data, " +
            "generated geometry, texture resolution, and actual draw submission.");
        ImGui.Checkbox("Lock window", ref _spellFxInspectorLocked);
        ImGui.SameLine();
        ImGui.TextDisabled("F7 shows/hides");

        if (ImGui.Button("All normal")) ApplySpellFxPreset(true, true, true);
        ImGui.SameLine();
        if (ImGui.Button("Mesh only")) ApplySpellFxPreset(true, false, false);
        ImGui.SameLine();
        if (ImGui.Button("Particles only")) ApplySpellFxPreset(false, true, false);
        ImGui.SameLine();
        if (ImGui.Button("Ribbons only")) ApplySpellFxPreset(false, false, true);

        if (_spellEffectMeshes is not null)
        {
            bool enabled = _spellEffectMeshes.Enabled;
            if (ImGui.Checkbox("Effect meshes", ref enabled)) _spellEffectMeshes.Enabled = enabled;
            ImGui.SameLine();
            ImGui.Text($"submitted {_spellEffectMeshes.DrawnLastFrame}");
        }
        else ImGui.TextDisabled("Effect mesh renderer unavailable");

        ImGui.SeparatorText("Particle heads and tails");
        if (_spellParticles is null) ImGui.TextDisabled("Spell particle renderer unavailable");
        else
        {
            bool enabled = _spellParticles.Enabled;
            bool heads = _spellParticles.DrawHeads;
            bool tails = _spellParticles.DrawTails;
            bool depth = _spellParticles.DepthTest;
            if (ImGui.Checkbox("Particles", ref enabled)) _spellParticles.Enabled = enabled;
            ImGui.SameLine();
            if (ImGui.Checkbox("Heads", ref heads)) _spellParticles.DrawHeads = heads;
            ImGui.SameLine();
            if (ImGui.Checkbox("Tails", ref tails)) _spellParticles.DrawTails = tails;
            ImGui.SameLine();
            if (ImGui.Checkbox("Depth test##particles", ref depth)) _spellParticles.DepthTest = depth;

            float tail = _spellParticles.TailLengthScale;
            float size = _spellParticles.SizeScale;
            float alpha = _spellParticles.AlphaScale;
            if (ImGui.SliderFloat("Tail length", ref tail, .1f, 8f, "%.2fx"))
                _spellParticles.TailLengthScale = tail;
            if (ImGui.SliderFloat("Particle size", ref size, .25f, 4f, "%.2fx"))
                _spellParticles.SizeScale = size;
            if (ImGui.SliderFloat("Particle alpha", ref alpha, .1f, 8f, "%.2fx"))
                _spellParticles.AlphaScale = alpha;
            if (ImGui.Button("Tail stress test"))
            {
                ApplySpellFxPreset(false, true, false);
                _spellParticles.DrawHeads = false;
                _spellParticles.DrawTails = true;
                _spellParticles.DepthTest = false;
                _spellParticles.TailLengthScale = 4f;
                _spellParticles.SizeScale = 2f;
                _spellParticles.AlphaScale = 4f;
            }

            ImGui.BeginChild("particle-evidence", new Vector2(0, 150), true);
            foreach (var d in _spellParticles.Diagnostics())
            {
                string texture = d.Texture.Length == 0 ? "<none>" : d.Texture;
                ImGui.TextWrapped($"e{d.Emitter} mode={d.Mode} live={d.Live}  " +
                    $"generated H{d.GeneratedHeads}/T{d.GeneratedTails}  " +
                    $"texture={(d.TextureReady ? "READY" : "MISSING")}  " +
                    $"submitted={d.Submitted}  {texture}");
            }
            ImGui.EndChild();
        }

        ImGui.SeparatorText("Ribbons");
        if (_spellRibbons is null) ImGui.TextDisabled("Spell ribbon renderer unavailable");
        else
        {
            bool enabled = _spellRibbons.Enabled;
            bool force = _spellRibbons.ForceVisibility;
            bool depth = _spellRibbons.DepthTest;
            if (ImGui.Checkbox("Ribbons", ref enabled)) _spellRibbons.Enabled = enabled;
            ImGui.SameLine();
            if (ImGui.Checkbox("Force visibility", ref force)) _spellRibbons.ForceVisibility = force;
            ImGui.SameLine();
            if (ImGui.Checkbox("Depth test##ribbons", ref depth)) _spellRibbons.DepthTest = depth;
            float width = _spellRibbons.WidthScale;
            float alpha = _spellRibbons.AlphaScale;
            if (ImGui.SliderFloat("Ribbon width", ref width, .25f, 8f, "%.2fx"))
                _spellRibbons.WidthScale = width;
            if (ImGui.SliderFloat("Ribbon alpha", ref alpha, .1f, 8f, "%.2fx"))
                _spellRibbons.AlphaScale = alpha;
            if (ImGui.Button("Ribbon stress test"))
            {
                ApplySpellFxPreset(false, false, true);
                _spellRibbons.ForceVisibility = true;
                _spellRibbons.DepthTest = false;
                _spellRibbons.WidthScale = 4f;
                _spellRibbons.AlphaScale = 4f;
            }
            ImGui.Text($"candidates {_spellRibbons.CandidatesLastFrame}  " +
                $"visibility rejected {_spellRibbons.VisibilityRejectedLastFrame}  " +
                $"texture rejected {_spellRibbons.TextureRejectedLastFrame}  " +
                $"submitted {_spellRibbons.DrawnLastFrame}");
            ImGui.BeginChild("ribbon-evidence", new Vector2(0, 115), true);
            foreach (var d in _spellRibbons.Diagnostics())
                ImGui.TextWrapped($"{d.Model} r{d.Emitter} edges={d.Edges}  " +
                    $"texture={(d.TextureReady ? "READY" : "MISSING")}  " +
                    $"alpha={d.Alpha:0.###} submitted={(d.Submitted ? "YES" : "NO")}");
            ImGui.EndChild();
        }

        ImGui.End();
    }

    private void ApplySpellFxPreset(bool meshes, bool particles, bool ribbons)
    {
        if (_spellEffectMeshes is not null) _spellEffectMeshes.Enabled = meshes;
        if (_spellParticles is not null)
        {
            _spellParticles.Enabled = particles;
            _spellParticles.DrawHeads = true;
            _spellParticles.DrawTails = true;
            _spellParticles.DepthTest = true;
            _spellParticles.TailLengthScale = 1f;
            _spellParticles.SizeScale = 1f;
            _spellParticles.AlphaScale = 1f;
        }
        if (_spellRibbons is not null)
        {
            _spellRibbons.Enabled = ribbons;
            _spellRibbons.ForceVisibility = false;
            _spellRibbons.DepthTest = true;
            _spellRibbons.WidthScale = 1f;
            _spellRibbons.AlphaScale = 1f;
        }
    }
}
