using System.Numerics;
using ImGuiNET;
using MSUIClient.Creator;
using MSUIClient.Formats;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Spell workshop: PHASE COMPOSITION (shared_docs/SPELL_CREATOR_IDE.md §2.8)
//
// Which models play in which phase, where they hang on the caster, how big,
// and which animation the caster performs - the kit level of a spell, above
// the per-model dials. The nine kit slots are the 1.12 client's literal
// attachment order (Head, Chest, Base, Left hand, Right hand, Breath, Special
// 1..3); the missile has its own slot. Any effect M2 in the game can be put in
// any slot; a model new to the spell joins the workshop as an editable phase.
//
// Preview: PresentSpellEffect substitutes the composed kit. Export: the
// session's "composition" block; the Completer's cloner writes the kit rows
// from it (animation, slots, effect paths, scale) instead of copying the
// source verbatim.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private static readonly string[] CreatorSlotNames =
        { "Head", "Chest", "Base (feet)", "Left hand", "Right hand", "Breath", "Special 1", "Special 2", "Special 3" };

    private static readonly SpellStage[] CreatorStages =
        { SpellStage.Precast, SpellStage.Cast, SpellStage.Impact, SpellStage.State, SpellStage.Channel };

    /// <summary>One stage's kit as the workshop composes it, next to what the source authored.</summary>
    private sealed class CreatorStageComposition
    {
        public ushort? AnimationId;
        public ushort? AuthoredAnimation;
        public readonly string?[] Slots = new string?[9];
        public readonly string?[] AuthoredSlots = new string?[9];
        public readonly float[] Scales = { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f };
        /// <summary>The source spell authored this stage at all (a kit exists).</summary>
        public bool Authored;

        public bool Changed
        {
            get
            {
                if (AnimationId != AuthoredAnimation) return true;
                for (int i = 0; i < 9; i++)
                {
                    if (!string.Equals(Slots[i] ?? "", AuthoredSlots[i] ?? "", StringComparison.OrdinalIgnoreCase)) return true;
                    if (MathF.Abs(Scales[i] - 1f) > 1e-4f) return true;
                }
                return false;
            }
        }

        public void Reset()
        {
            AnimationId = AuthoredAnimation;
            for (int i = 0; i < 9; i++) { Slots[i] = AuthoredSlots[i]; Scales[i] = 1f; }
        }
    }

    private static string CreatorStageName(SpellStage stage) => stage.ToString().ToLowerInvariant();

    /// <summary>Read the source kits into the doc's composition (called once per spell select).</summary>
    private void InitCreatorComposition(CreatorSpellDoc doc)
    {
        doc.Composition.Clear();
        foreach (SpellStage stage in CreatorStages)
        {
            var composition = new CreatorStageComposition();
            uint kitId = SpellVisualCatalog.KitFor(doc.Stages, stage);
            if (kitId != 0 && _spellVisualCatalog?.TryGetKit(kitId, out SpellVisualKitInfo kit) == true)
            {
                composition.Authored = true;
                composition.AuthoredAnimation = kit.AnimationId;
                foreach (SpellVisualKitEffect effect in kit.Effects)
                {
                    int slot = Array.IndexOf(SpellVisualCatalog.KitAttachmentIds, effect.AttachmentId);
                    if (slot >= 0) composition.AuthoredSlots[slot] = effect.ModelPath;
                }
            }
            composition.Reset();
            doc.Composition[stage] = composition;
        }
        doc.MissileOverride = null;
        doc.MissileScale = 1f;
    }

    /// <summary>The kit the preview plays: the authored one, or the composed one when changed.</summary>
    private SpellVisualKitInfo CreatorComposedKit(CreatorSpellDoc doc, SpellStage stage, SpellVisualKitInfo authored)
    {
        if (!doc.Composition.TryGetValue(stage, out CreatorStageComposition? composition) || !composition.Changed)
            return authored;
        var effects = new List<SpellVisualKitEffect>(9);
        for (int i = 0; i < 9; i++)
            if (composition.Slots[i] is { Length: > 0 } path)
                effects.Add(new SpellVisualKitEffect(SpellVisualCatalog.KitAttachmentIds[i], path, composition.Scales[i]));
        return new SpellVisualKitInfo(composition.AnimationId, authored.Sound, effects, authored.CharProcs);
    }

    /// <summary>The missile the preview flies: the composed override, else the authored path.</summary>
    private static string? CreatorEffectiveMissile(CreatorSpellDoc doc) =>
        doc.MissileOverride is { Length: > 0 } m ? m : doc.MissilePath;

    private static bool CreatorMissileChanged(CreatorSpellDoc doc) =>
        (doc.MissileOverride is { Length: > 0 } && !string.Equals(doc.MissileOverride, doc.MissilePath, StringComparison.OrdinalIgnoreCase)) ||
        MathF.Abs(doc.MissileScale - 1f) > 1e-4f;

    /// <summary>A stage can play when the source authored it or the composition filled it.</summary>
    private static bool CreatorStageAvailable(CreatorSpellDoc doc, SpellStage stage) =>
        doc.Composition.TryGetValue(stage, out CreatorStageComposition? c)
            ? c.Authored || c.Slots.Any(p => p is { Length: > 0 })
            : SpellVisualCatalog.KitFor(doc.Stages, stage) != 0;

    private static bool CreatorCompositionChanged(CreatorSpellDoc doc) =>
        doc.Composition.Values.Any(c => c.Changed) || CreatorMissileChanged(doc);

    /// <summary>Every model path the composed phases reference, for the tree and the export.</summary>
    private static void RefreshCreatorPhaseModels(CreatorSpellDoc doc)
    {
        doc.PhaseModels.Clear();
        foreach (SpellStage stage in CreatorStages)
        {
            if (!doc.Composition.TryGetValue(stage, out CreatorStageComposition? composition)) continue;
            foreach (string? path in composition.Slots)
                if (path is { Length: > 0 }) doc.PhaseModels.Add((stage, path));
        }
    }

    /// <summary>A model new to the spell becomes an editable phase model (plus its geometry children).</summary>
    private bool EnsureCreatorModel(CreatorSpellDoc doc, string path)
    {
        if (path.Length == 0) return false;
        if (doc.Models.ContainsKey(path)) return true;
        byte[]? original = _spellEffects?.ReadOriginalModel(path);
        if (original is null) return false;
        var model = new CreatorModelDoc { Path = path, Original = original };
        model.Working = (byte[])original.Clone();
        model.Emitters = M2EmitterParser.ReadEmitters(model.Working);
        model.OriginalEmitterCount = model.Emitters.Count;
        model.Textures = Creator.M2TextureParser.ParseTextures(model.Original);
        model.Meshes = M2MeshParser.ReadMeshes(model.Working);
        model.OriginalLayerCount = model.Meshes.Count;
        model.Ribbons = M2RibbonParser.ReadRibbons(model.Working);
        model.OriginalRibbonCount = model.Ribbons.Count;
        model.Bones = M2BoneParser.ReadBones(model.Working);
        doc.Models[path] = model;
        AttachCreatorGeometryModels(doc);
        return true;
    }

    private void SetCreatorSlot(CreatorSpellDoc doc, SpellStage stage, int slot, string? path)
    {
        if (!doc.Composition.TryGetValue(stage, out CreatorStageComposition? composition)) return;
        if (path is { Length: > 0 } && !EnsureCreatorModel(doc, path))
        {
            _creatorExportStatus = $"Could not load {path}.";
            return;
        }
        composition.Slots[slot] = path is { Length: > 0 } ? path : null;
        RefreshCreatorPhaseModels(doc);
        ReapPresentedEffect();
        _creatorLoopNextAt = 0;
    }

    private void SetCreatorMissile(CreatorSpellDoc doc, string? path)
    {
        if (path is { Length: > 0 } && !EnsureCreatorModel(doc, path))
        {
            _creatorExportStatus = $"Could not load {path}.";
            return;
        }
        doc.MissileOverride = path is { Length: > 0 } ? path : null;
        _creatorLoopNextAt = 0;
    }

    // ── the inspector body ───────────────────────────────────────────────────

    private void DrawCreatorCompositionBody()
    {
        float cs = CreatorUiScale;
        if (_creatorSpell is not { } doc)
        {
            ImGui.TextDisabled("Pick a spell first.");
            return;
        }
        CreatorHelp("The kit level of the spell: which effect models play in each phase, WHERE they hang " +
            "on the caster (the nine 1.12 attachment slots), how big, and which animation the caster " +
            "performs. Any effect model in the game can go in any slot; a model new to this spell " +
            "joins the tree as an editable phase. Changes preview live and travel in the session.");

        foreach (SpellStage stage in CreatorStages)
        {
            if (!doc.Composition.TryGetValue(stage, out CreatorStageComposition? composition)) continue;
            ImGui.PushID(stage.ToString());
            ImGui.Spacing();
            ImGui.TextColored(SpellIdeGold, stage.ToString().ToUpperInvariant() +
                                            (composition.Authored ? "" : "  (not authored by the source)") +
                                            (composition.Changed ? "  *" : ""));
            if (composition.Changed)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("reset")) { composition.Reset(); RefreshCreatorPhaseModels(doc); ReapPresentedEffect(); _creatorLoopNextAt = 0; }
            }
            // The caster's animation, BY NAME (GameLoop.Creator.AnimationPicker.cs). This used
            // to be an InputInt over the raw AnimationData id with the common ids listed in a
            // tooltip - and the log showed it was never once changed.
            ImGui.TextDisabled("Caster does");
            ImGui.SameLine(130f * cs);
            DrawCreatorAnimationButton(stage, composition, 200f * cs);

            for (int slot = 0; slot < 9; slot++)
            {
                ImGui.PushID(slot);
                string? path = composition.Slots[slot];
                bool present = path is { Length: > 0 };
                ImGui.TextDisabled($"{CreatorSlotNames[slot],-12}");
                ImGui.SameLine(130f * cs);
                if (present)
                    ImGui.TextUnformatted(Path.GetFileName(path!));
                else
                    ImGui.TextDisabled("(empty)");
                ImGui.SameLine();
                if (ImGui.SmallButton("pick")) OpenCreatorModelPicker(stage, slot);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Put any effect model in this slot.");
                if (present)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("clear")) SetCreatorSlot(doc, stage, slot, null);
                    ImGui.SameLine();
                    float scale = composition.Scales[slot];
                    ImGui.SetNextItemWidth(110f * cs);
                    if (ImGui.SliderFloat("##scale", ref scale, 0.1f, 5f, "x%.2f"))
                    {
                        composition.Scales[slot] = scale;
                        ReapPresentedEffect();
                        _creatorLoopNextAt = 0;
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Effect scale (SpellVisualEffectName scale).");
                }
                ImGui.PopID();
            }
            ImGui.PopID();
        }

        ImGui.Spacing();
        ImGui.TextColored(SpellIdeGold, "MISSILE" + (CreatorMissileChanged(doc) ? "  *" : ""));
        if (CreatorMissileChanged(doc))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("reset##missile")) { doc.MissileOverride = null; doc.MissileScale = 1f; _creatorLoopNextAt = 0; }
        }
        string? missile = CreatorEffectiveMissile(doc);
        ImGui.TextDisabled("Model");
        ImGui.SameLine(130f * cs);
        if (missile is { Length: > 0 }) ImGui.TextUnformatted(Path.GetFileName(missile));
        else ImGui.TextDisabled("(none - the spell has no projectile)");
        ImGui.SameLine();
        if (ImGui.SmallButton("pick##missile")) OpenCreatorModelPicker(null, -1);
        if (missile is { Length: > 0 })
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("clear##missile")) SetCreatorMissile(doc, null);
            float missileScale = doc.MissileScale;
            ImGui.SetNextItemWidth(CreatorControlWidth);
            if (ImGui.SliderFloat("Missile scale", ref missileScale, 0.1f, 5f, "x%.2f")) doc.MissileScale = missileScale;
        }
        CreatorHelp("The projectile model that flies to the target (its trail and sparks are the " +
            "model's own ribbons and emitters, tuned like any phase). Flight speed is spell data, " +
            "set in the Completer.");
    }
}
