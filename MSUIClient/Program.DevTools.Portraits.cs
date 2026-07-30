using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Units;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private enum PortraitLabSubject { Player, Target, Specimen }

    private PortraitRenderTarget? _labPortrait;
    private PortraitLabSubject _labSubject;
    private PortraitTuning _labTuning = PortraitTuning.Default;
    private string _labTuningKey = "";
    private bool _labPanelOpen;
    private bool _labShowUnmasked;
    private bool _labPortraitDirty = true;
    private double _labPortraitRetryAt;
    private string _labSpecimenFilter = "";
    private int _labSpecimenIndex;
    private bool _labPreviousSpecimenKeyDown;
    private bool _labNextSpecimenKeyDown;

    private void DrawPortraitLabPanel()
    {
        bool open = ImGui.CollapsingHeader("Portrait Lab");
        if (_labPanelOpen && !open) MarkPortraitDirtyForKey(_labTuningKey);
        _labPanelOpen = open;
        if (!open) return;

        DrawPortraitLabSubjectRadio("Player", PortraitLabSubject.Player);
        ImGui.SameLine();
        DrawPortraitLabSubjectRadio("Target", PortraitLabSubject.Target);
        ImGui.SameLine();
        DrawPortraitLabSubjectRadio("Specimen", PortraitLabSubject.Specimen);

        if (_labSubject == PortraitLabSubject.Specimen) DrawPortraitLabSpecimenChooser();
        SyncPortraitLabSubject();
        if (_labShowUnmasked || _labSubject == PortraitLabSubject.Specimen)
            BakeLabPortraitIfDirty();

        PortraitRenderTarget? evidenceTarget = string.IsNullOrEmpty(_labTuningKey)
            ? null
            : _labSubject == PortraitLabSubject.Specimen || _labShowUnmasked
                ? _labPortrait
                : _labSubject == PortraitLabSubject.Player ? _playerPortrait : _targetPortrait;
        uint evidenceTexture = evidenceTarget?.TextureHandle ?? 0;
        if (ImGui.BeginChild("##portrait-lab-evidence", new Vector2(0f, 532f), true,
                ImGuiWindowFlags.HorizontalScrollbar))
        {
            DrawPortrait(evidenceTexture, 512f);
            ImGui.SameLine();
            ImGui.BeginGroup();
            DrawPortraitLabEvidence();
            ImGui.EndGroup();
        }
        ImGui.EndChild();

        if (ImGui.Button("Save PNG##portrait-lab") && evidenceTarget is not null)
            DumpPortrait(evidenceTarget, $"lab-{_labSubject.ToString().ToLowerInvariant()}", "capture");

        bool tuningChanged = false;
        tuningChanged |= LabSlider("HeadFraction", _labTuning.HeadFraction, 0.5f, 1.2f,
            value => _labTuning = _labTuning with { HeadFraction = value });
        tuningChanged |= LabSlider("WindowFraction", _labTuning.WindowFraction, 0.1f, 1.0f,
            value => _labTuning = _labTuning with { WindowFraction = value });
        tuningChanged |= LabSlider("WindowMin", _labTuning.WindowMin, 0.1f, 1.5f,
            value => _labTuning = _labTuning with { WindowMin = value });
        tuningChanged |= LabSlider("WindowMax", _labTuning.WindowMax, 0.5f, 2.5f,
            value => _labTuning = _labTuning with { WindowMax = value });
        tuningChanged |= LabSlider("FovyDegrees", _labTuning.FovyDegrees, 10f, 70f,
            value => _labTuning = _labTuning with { FovyDegrees = value });
        tuningChanged |= LabSlider("YawOffset", _labTuning.YawOffset, -MathF.PI, MathF.PI,
            value => _labTuning = _labTuning with { YawOffset = value });
        tuningChanged |= LabSlider("Pitch", _labTuning.Pitch, -0.5f, 0.5f,
            value => _labTuning = _labTuning with { Pitch = value });
        tuningChanged |= LabSlider("NearFloor", _labTuning.NearFloor, 0.005f, 0.5f,
            value => _labTuning = _labTuning with { NearFloor = value });

        ImGui.TextUnformatted("Camera source");
        ImGui.SameLine();
        if (ImGui.RadioButton("auto##portrait-source", _labTuning.ForceSource is null))
        {
            _labTuning = _labTuning with { ForceSource = null };
            tuningChanged = true;
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("authored##portrait-source",
                _labTuning.ForceSource == PortraitCameraSource.Authored))
        {
            _labTuning = _labTuning with { ForceSource = PortraitCameraSource.Authored };
            tuningChanged = true;
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("bounds##portrait-source",
                _labTuning.ForceSource == PortraitCameraSource.Bounds))
        {
            _labTuning = _labTuning with { ForceSource = PortraitCameraSource.Bounds };
            tuningChanged = true;
        }

        if (tuningChanged) MarkPortraitLabDirty();

        bool keyValid = !string.IsNullOrEmpty(_labTuningKey);
        bool stored = keyValid && _portraitOverrides?.Find(_labTuningKey) is not null;
        ImGui.TextDisabled(keyValid
            ? $"{_labTuningKey} ({(stored ? "stored" : "not stored")})"
            : "No live subject key");
        ImGui.BeginDisabled(!keyValid);
        if (ImGui.Button("Save override##portrait-lab"))
            _portraitOverrides?.Set(_labTuningKey, _labTuning);
        ImGui.SameLine();
        if (ImGui.Button("Clear override##portrait-lab"))
            _portraitOverrides?.Remove(_labTuningKey);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Reset to defaults##portrait-lab"))
        {
            _labTuning = PortraitTuning.Default;
            MarkPortraitLabDirty();
        }

        bool unmasked = _labShowUnmasked;
        if (ImGui.Checkbox("Show unmasked bake", ref unmasked))
        {
            _labShowUnmasked = unmasked;
            _labPortraitDirty = true;
            _labPortraitRetryAt = 0;
        }
    }

    private void DrawPortraitLabSubjectRadio(string label, PortraitLabSubject subject)
    {
        if (!ImGui.RadioButton(label, _labSubject == subject)) return;
        MarkPortraitDirtyForKey(_labTuningKey);
        _labSubject = subject;
        _labTuningKey = "";
        _labPortraitDirty = true;
        _labPortraitRetryAt = 0;
    }

    private void DrawPortraitLabSpecimenChooser()
    {
        string filter = _labSpecimenFilter;
        ImGui.SetNextItemWidth(240f);
        if (ImGui.InputText("Filter##portrait-specimen", ref filter, 128u))
        {
            _labSpecimenFilter = filter;
            SelectPortraitLabSpecimen(0);
        }

        CreatureRenderer.PortraitSpecimen[] filtered = FilteredPortraitSpecimens();
        if (filtered.Length == 0)
        {
            ImGui.TextDisabled("No matching CreatureDisplayInfo rows");
            return;
        }
        _labSpecimenIndex = Math.Clamp(_labSpecimenIndex, 0, filtered.Length - 1);
        CreatureRenderer.PortraitSpecimen specimen = filtered[_labSpecimenIndex];
        ImGui.TextUnformatted(
            $"{_labSpecimenIndex + 1}/{filtered.Length}  displayId={specimen.DisplayId}");
        ImGui.TextWrapped(specimen.ModelPath);
        if (ImGui.Button("[ Previous##portrait-specimen")) CyclePortraitLabSpecimen(-1);
        ImGui.SameLine();
        if (ImGui.Button("] Next##portrait-specimen")) CyclePortraitLabSpecimen(1);
    }

    private CreatureRenderer.PortraitSpecimen[] FilteredPortraitSpecimens()
    {
        if (_creatures is null) return Array.Empty<CreatureRenderer.PortraitSpecimen>();
        IEnumerable<CreatureRenderer.PortraitSpecimen> specimens = _creatures.PortraitSpecimens;
        if (!string.IsNullOrWhiteSpace(_labSpecimenFilter))
        {
            string filter = _labSpecimenFilter.Trim();
            specimens = specimens.Where(specimen =>
                specimen.DisplayId.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                specimen.ModelPath.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }
        return specimens.ToArray();
    }

    private CreatureRenderer.PortraitSpecimen? CurrentPortraitLabSpecimen()
    {
        CreatureRenderer.PortraitSpecimen[] filtered = FilteredPortraitSpecimens();
        if (filtered.Length == 0) return null;
        _labSpecimenIndex = Math.Clamp(_labSpecimenIndex, 0, filtered.Length - 1);
        return filtered[_labSpecimenIndex];
    }

    private void SelectPortraitLabSpecimen(int index)
    {
        MarkPortraitDirtyForKey(_labTuningKey);
        _labSpecimenIndex = index;
        _labTuningKey = "";
        _labPortraitDirty = true;
        _labPortraitRetryAt = 0;
    }

    private void CyclePortraitLabSpecimen(int delta)
    {
        CreatureRenderer.PortraitSpecimen[] filtered = FilteredPortraitSpecimens();
        if (filtered.Length == 0) return;
        int next = (_labSpecimenIndex + delta) % filtered.Length;
        if (next < 0) next += filtered.Length;
        SelectPortraitLabSpecimen(next);
    }

    private void UpdatePortraitLabInput(bool typing)
    {
        bool previous = _window.IsDown(Key.LeftBracket);
        bool next = _window.IsDown(Key.RightBracket);
        if (_config.DevTools && _labPanelOpen &&
            _labSubject == PortraitLabSubject.Specimen && !typing)
        {
            if (previous && !_labPreviousSpecimenKeyDown) CyclePortraitLabSpecimen(-1);
            if (next && !_labNextSpecimenKeyDown) CyclePortraitLabSpecimen(1);
        }
        _labPreviousSpecimenKeyDown = previous;
        _labNextSpecimenKeyDown = next;
    }

    private void SyncPortraitLabSubject()
    {
        string key = _labSubject switch
        {
            PortraitLabSubject.Player when _character is not null => PlayerPortraitKey(_character),
            PortraitLabSubject.Target when _selectionGuid != 0 &&
                _entities.TryGet(_selectionGuid, out WorldEntity target) && target.IsCreature =>
                CreaturePortraitKey(target.DisplayId),
            PortraitLabSubject.Specimen when CurrentPortraitLabSpecimen() is { } specimen =>
                CreaturePortraitKey(specimen.DisplayId),
            _ => "",
        };
        if (key == _labTuningKey) return;
        MarkPortraitDirtyForKey(_labTuningKey);
        _labTuningKey = key;
        _labTuning = string.IsNullOrEmpty(key)
            ? PortraitTuning.Default
            : _portraitOverrides?.Find(key) ?? PortraitTuning.Default;
        MarkPortraitLabDirty();
    }

    private bool TryResolveLabTuning(string key, out PortraitTuning tuning)
    {
        if (_config.DevTools && _labPanelOpen &&
            string.Equals(key, _labTuningKey, StringComparison.OrdinalIgnoreCase))
        {
            tuning = _labTuning;
            return true;
        }
        tuning = PortraitTuning.Default;
        return false;
    }

    private void MarkPortraitLabDirty()
    {
        MarkPortraitDirtyForKey(_labTuningKey);
        _labPortraitDirty = true;
        _labPortraitRetryAt = 0;
    }

    private void MarkPortraitDirtyForKey(string key)
    {
        if (key.StartsWith("player:", StringComparison.OrdinalIgnoreCase))
        {
            _playerPortraitDirty = true;
            _playerPortraitRetryAt = 0;
            return;
        }
        if (key.StartsWith("creature:", StringComparison.OrdinalIgnoreCase))
        {
            _targetPortraitUsable = false;
            _targetPortraitRetryAt = 0;
        }
    }

    private static bool LabSlider(
        string label, float current, float min, float max, Action<float> assign)
    {
        float value = current;
        if (!ImGui.SliderFloat($"{label}##portrait-lab", ref value, min, max)) return false;
        assign(value);
        return true;
    }

    private void DrawPortraitLabEvidence()
    {
        PortraitSubject wanted = _labSubject switch
        {
            PortraitLabSubject.Player => PortraitSubject.Player,
            PortraitLabSubject.Target => PortraitSubject.Target,
            _ => PortraitSubject.Lab,
        };
        PortraitVerdict[] matches = _verdicts.Recent<PortraitVerdict>(256)
            .Where(v => v.Subject == wanted).ToArray();
        if (matches.Length == 0)
        {
            ImGui.TextDisabled("No portrait verdict yet");
            return;
        }
        PortraitVerdict v = matches[^1];
        ImGui.TextUnformatted($"Time: {v.Time:F3}");
        ImGui.TextUnformatted($"Subject: {v.Subject}");
        ImGui.TextUnformatted($"Outcome: {v.Outcome}");
        ImGui.TextUnformatted($"CameraSource: {v.CameraSource}");
        ImGui.TextUnformatted($"AuthoredRetriedAsBounds: {v.AuthoredRetriedAsBounds}");
        ImGui.TextUnformatted($"SubjectPixels: {v.SubjectPixels}");
        ImGui.TextUnformatted($"RGB: {v.RgbLo}..{v.RgbHi}");
        ImGui.TextUnformatted($"Alpha: {v.AlphaLo}..{v.AlphaHi}");
        ImGui.TextUnformatted($"Pieces / VisiblePieces: {v.Pieces}");
        ImGui.TextUnformatted($"DisplayId: {v.DisplayId}");
        ImGui.TextUnformatted($"BindPoseHeight: {v.BindPoseHeight:F4}");
        ImGui.TextUnformatted($"EyeHeight: {v.EyeHeight:F4}");
        ImGui.TextUnformatted($"Distance: {v.Distance:F4}");
        ImGui.TextUnformatted($"FovyDegrees: {v.FovyDegrees:F4}");
        ImGui.TextUnformatted($"NearPlane: {v.NearPlane:F4}");
    }

    private void BakeLabPortraitIfDirty()
    {
        if (!_labPortraitDirty || _labPortrait is null || NowSeconds() < _labPortraitRetryAt) return;
        bool ready = _labSubject switch
        {
            PortraitLabSubject.Player => BakeLabPlayer(),
            PortraitLabSubject.Target => BakeLabTarget(),
            PortraitLabSubject.Specimen => BakeLabSpecimen(),
            _ => false,
        };
        _labPortraitDirty = !ready;
        _labPortraitRetryAt = ready ? 0 : NowSeconds() + 1.0;
    }

    private bool BakeLabPlayer()
    {
        if (_labPortrait is null || _character is not { Loaded: true, Enabled: true }) return false;
        CharacterRenderer.UnitState state = BuildUnitState();
        state.Position = Vector3.Zero;
        state.Yaw = -_character.HeadingOffsetDegrees * MathF.PI / 180f;
        state.Forward = 0f;
        state.Strafe = 0f;
        float savedScale = _character.ModelScale;
        bool savedBind = _character.BindPose;
        bool savedFrozen = _character.FrozenStandPose;
        _character.ModelScale = 1f;
        _character.BindPose = false;
        _character.FrozenStandPose = true;
        try
        {
            M2PortraitCamera authoredData = default;
            Matrix4x4 transform = default;
            bool authored = _labTuning.ForceSource != PortraitCameraSource.Bounds &&
                _character.TryGetAuthoredPortrait(state, out authoredData, out transform) &&
                Vector3.DistanceSquared(authoredData.Position, authoredData.Target) > 1e-8f;
            bool forcedMissing = _labTuning.ForceSource == PortraitCameraSource.Authored && !authored;
            Camera camera = authored
                ? AuthoredPortraitCamera(authoredData, transform)
                : BoundsPortraitCamera(Vector3.Zero, state.Yaw, _character.BindPoseHeight(), _labTuning);
            WithPortraitLighting(() => _labPortrait.Bake(() =>
            {
                if (!forcedMissing) _character.Render(camera, state);
            }));
            PortraitRenderTarget.ReadbackStats stats = _labPortrait.Analyze();
            if (!stats.HasSubject && authored &&
                _labTuning.ForceSource != PortraitCameraSource.Authored)
            {
                Camera fallback = BoundsPortraitCamera(
                    Vector3.Zero, state.Yaw, _character.BindPoseHeight(), _labTuning);
                WithPortraitLighting(() =>
                    _labPortrait.Bake(() => _character.Render(fallback, state)));
                stats = _labPortrait.Analyze();
            }
            return stats.HasSubject;
        }
        finally
        {
            _character.ModelScale = savedScale;
            _character.BindPose = savedBind;
            _character.FrozenStandPose = savedFrozen;
        }
    }

    private bool BakeLabTarget()
    {
        if (_labPortrait is null || _creatures is null || _selectionGuid == 0 ||
            !_entities.TryGet(_selectionGuid, out WorldEntity target) || !target.IsCreature)
            return false;
        return TryBakeCreaturePortrait(
                   _labPortrait, target, _labTuning, overrideHit: true,
                   out CreaturePortraitBake bake) &&
               bake.Drawn && bake.Stats.HasSubject;
    }

    private bool BakeLabSpecimen()
    {
        if (_labPortrait is null || CurrentPortraitLabSpecimen() is not { } specimen)
            return false;
        WorldEntity entity = new()
        {
            Guid = 0xDEAD_0000_0000UL + (uint)specimen.DisplayId,
            Type = ObjectTypeId.Unit,
            Fields = ObjectFields.ForSyntheticUnit(specimen.DisplayId, 1f),
            Position = Vector3.Zero,
            Orientation = 0f,
        };
        if (!TryBakeCreaturePortrait(
                _labPortrait, entity, _labTuning, overrideHit: true,
                out CreaturePortraitBake bake))
        {
            _verdicts.Add(new PortraitVerdict(
                NowSeconds(), PortraitSubject.Lab, PortraitOutcome.NotDrawn,
                PortraitCameraSource.Override, false, 0, 0, 0, 0, 0,
                -1, specimen.DisplayId, 0f, 0f, 0f, 0f, 0f));
            return false;
        }

        bool ready = bake.Drawn && bake.Stats.HasSubject;
        _verdicts.Add(new PortraitVerdict(
            NowSeconds(),
            PortraitSubject.Lab,
            !bake.Drawn
                ? PortraitOutcome.NotDrawn
                : bake.Stats.HasSubject ? PortraitOutcome.Ready : PortraitOutcome.Blank,
            EffectivePortraitCameraSource(true, _labTuning, bake.UsedBounds),
            bake.AuthoredRetriedAsBounds,
            bake.Stats.SubjectPixels,
            bake.Stats.MinRgb,
            bake.Stats.MaxRgb,
            bake.Stats.MinAlpha,
            bake.Stats.MaxAlpha,
            -1,
            specimen.DisplayId,
            bake.Framing.Height,
            bake.UsedBounds ? bake.Camera.EyeHeight : 0f,
            bake.UsedBounds ? bake.Camera.Distance : 0f,
            bake.Camera.AuthoredVerticalFieldOfViewRadians is float authoredFovy
                ? authoredFovy * 180f / MathF.PI
                : bake.Camera.FieldOfViewDegrees,
            bake.Camera.NearPlane));
        if (ready && !_labShowUnmasked) _labPortrait.ApplyCircularMask();
        return ready;
    }
}
