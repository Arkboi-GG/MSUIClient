using System.Numerics;
using ImGuiNET;
using MSUIClient.Creator;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Spell workshop: MESH LAYERS and RIBBONS (shared_docs/SPELL_CREATOR_IDE.md §2.5)
//
// Owner, 2026-09-10, with every Cleave emitter off and the crescent still
// there: "we need to be able to edit all". An effect M2 draws three kinds of
// thing; the workshop edited one. This partial gives the other two the same
// treatment emitters have: a snapshot per layer / ribbon read from the bytes,
// absolute edits patched into the bytes on every rebuild (M2MeshParser /
// M2RibbonParser), an on/off switch, rows in the tree and the classic editor,
// and a body for the inspector. Everything travels in the patched M2, so the
// Completer builds the same picture without knowing any of this exists.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private static readonly (string Label, ushort Bit, string Help)[] CreatorMaterialFlagBits =
    {
        ("Unlit", M2MeshParser.FlagUnlit, "Ignore the scene light: the texture draws at its own brightness."),
        ("Unfogged", M2MeshParser.FlagUnfogged, "Never fogged by distance."),
        ("Two-sided", M2MeshParser.FlagTwoSided, "Draw both faces of every plane (most effect sheets need this)."),
        ("No depth test", M2MeshParser.FlagNoDepthTest, "Draw through the world instead of behind it."),
        ("No depth write", M2MeshParser.FlagNoDepthWrite, "Do not occlude what draws after it (normal for glows)."),
    };

    /// <summary>The texture name for a table slot, or a placeholder.</summary>
    private static string CreatorTextureName(CreatorModelDoc model, int slot)
    {
        var entry = model.Textures.FirstOrDefault(t => t.Index == slot);
        return entry is { Filename.Length: > 0 } ? Path.GetFileName(entry.Filename) : $"(slot {slot})";
    }

    private static string CreatorMeshLabel(CreatorModelDoc model, MeshSnapshot mesh, bool prefixState)
    {
        bool off = model.HiddenSubmeshes.Contains(mesh.SubmeshIndex);
        bool added = mesh.BatchIndex >= model.OriginalLayerCount;
        string state = (off ? "[OFF] " : "") + (added ? "[added] " : "");
        string name = CreatorTextureName(model, mesh.TextureSlot);
        return prefixState
            ? $"m{mesh.BatchIndex}  {state}{name}"
            : $"Mesh layer {mesh.BatchIndex}  ({name}, blend {mesh.Blend}, submesh {mesh.SubmeshIndex})" +
              (added ? "  [added]" : "") + (off ? "  [OFF]" : "");
    }

    private static string CreatorRibbonLabel(CreatorModelDoc model, RibbonSnapshot ribbon, bool prefixState)
    {
        bool off = model.DisabledRibbons.Contains(ribbon.Index);
        bool added = ribbon.Index >= model.OriginalRibbonCount;
        string state = (off ? "[OFF] " : "") + (added ? "[added] " : "");
        string name = CreatorTextureName(model, ribbon.TextureSlot);
        return prefixState
            ? $"r{ribbon.Index}  {state}{name}"
            : $"Ribbon {ribbon.Index}  ({name}, blend {ribbon.Blend}, bone {ribbon.Bone})" +
              (added ? "  [added]" : "") + (off ? "  [OFF]" : "");
    }

    /// <summary>Rebuild step: every mesh-layer and ribbon edit patched into the working bytes.
    /// Runs after the emitter patches (fixed offsets, EOF appends) and before the texture
    /// swaps (another EOF resize) - appends never move anything already written.</summary>
    private static byte[] ApplyCreatorMeshAndRibbonEdits(CreatorModelDoc model, byte[] working)
    {
        foreach (MeshPatch edit in model.MeshEdits.Values)
            working = M2MeshParser.ApplyMeshPatch(working, edit);
        foreach (int submesh in model.HiddenSubmeshes)
            M2MeshParser.HideSubmesh(working, submesh);
        foreach (RibbonPatch edit in model.RibbonEdits.Values)
            working = M2RibbonParser.ApplyRibbonPatch(working, edit);
        foreach (int ribbon in model.DisabledRibbons)
            M2RibbonParser.DisableRibbon(working, ribbon);
        foreach (BonePatch edit in model.BoneEdits.Values)
            M2BoneParser.ApplyBonePatch(working, edit);
        return working;
    }

    private static bool CreatorMeshOrRibbonEditsActive(CreatorModelDoc model) =>
        model.HiddenSubmeshes.Count > 0 || model.MeshEdits.Count > 0 ||
        model.DisabledRibbons.Count > 0 || model.RibbonEdits.Count > 0 || model.BoneEdits.Count > 0;

    /// <summary>Drop one added layer and re-key everything addressed by batch index above it.</summary>
    private static void RemoveCreatorAddedLayer(CreatorModelDoc model, int batchIndex)
    {
        int added = batchIndex - model.OriginalLayerCount;
        if (added < 0 || added >= model.AddedLayers.Count) return;
        model.AddedLayers.RemoveAt(added);
        var edits = model.MeshEdits.Where(kv => kv.Key != batchIndex)
            .ToDictionary(kv => kv.Key > batchIndex ? kv.Key - 1 : kv.Key, kv => kv.Value);
        model.MeshEdits.Clear();
        foreach (var (key, edit) in edits) { edit.BatchIndex = key; model.MeshEdits[key] = edit; }
    }

    /// <summary>Drop one added ribbon and re-key everything addressed by ribbon index above it.</summary>
    private static void RemoveCreatorAddedRibbon(CreatorModelDoc model, int ribbonIndex)
    {
        int added = ribbonIndex - model.OriginalRibbonCount;
        if (added < 0 || added >= model.AddedRibbons.Count) return;
        model.AddedRibbons.RemoveAt(added);
        var edits = model.RibbonEdits.Where(kv => kv.Key != ribbonIndex)
            .ToDictionary(kv => kv.Key > ribbonIndex ? kv.Key - 1 : kv.Key, kv => kv.Value);
        model.RibbonEdits.Clear();
        foreach (var (key, edit) in edits) { edit.RibbonIndex = key; model.RibbonEdits[key] = edit; }
        var disabled = model.DisabledRibbons.Where(i => i != ribbonIndex).Select(i => i > ribbonIndex ? i - 1 : i).ToList();
        model.DisabledRibbons.Clear();
        foreach (int i in disabled) model.DisabledRibbons.Add(i);
    }

    // ── shared widgets ───────────────────────────────────────────────────────

    private bool CreatorBlendCombo(string label, ushort authored, ref ushort? edited, string resetId)
    {
        bool changed = false;
        int blend = edited ?? authored;
        ImGui.SetNextItemWidth(130f * CreatorUiScale);
        if (ImGui.Combo(label, ref blend, CreatorBlendModes, CreatorBlendModes.Length))
        {
            edited = (ushort)blend;
            changed = true;
        }
        if (CreatorResetKnob(resetId) && edited is not null)
        {
            edited = null;
            changed = true;
        }
        CreatorHelp("How this layer composites with the world (its own private material - " +
            "other layers sharing the authored material are untouched):\n" +
            "0 Opaque, 1 Alpha key, 2 Alpha blend, 3 Add (no alpha), 4 Additive (pure light, " +
            $"most glows), 5 Mod, 6 Mod x2.\n\nAuthored: {authored}.");
        return changed;
    }

    private bool CreatorMaterialFlagSwitches(ushort authored, ref ushort? edited, string resetId)
    {
        bool changed = false;
        ushort flags = edited ?? authored;
        ImGui.TextDisabled("MATERIAL FLAGS");
        foreach (var (label, bit, help) in CreatorMaterialFlagBits)
        {
            bool on = (flags & bit) != 0;
            if (ImGui.Checkbox(label, ref on))
            {
                flags = on ? (ushort)(flags | bit) : (ushort)(flags & ~bit);
                edited = flags;
                changed = true;
            }
            CreatorHelp(help);
        }
        if (CreatorResetKnob(resetId) && edited is not null)
        {
            edited = null;
            changed = true;
        }
        CreatorHelp($"Reset restores the authored flag word (0x{authored:X}).");
        return changed;
    }

    private bool CreatorTextureSlotCombo(CreatorModelDoc model, string label, int authored,
        ref int? edited, string resetId)
    {
        bool changed = false;
        int current = edited ?? authored;
        var names = model.Textures.Select(t => $"{t.Index}: {CreatorTextureName(model, t.Index)}").ToArray();
        int pick = model.Textures.FindIndex(t => t.Index == current);
        if (pick < 0) pick = 0;
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (names.Length > 0 && ImGui.Combo(label, ref pick, names, names.Length))
        {
            edited = model.Textures[pick].Index;
            changed = true;
        }
        if (CreatorResetKnob(resetId) && edited is not null)
        {
            edited = null;
            changed = true;
        }
        CreatorHelp("Draw with a different image from this model's texture table (a private " +
            "lookup entry is appended, so nothing else changes). To bring in art from another " +
            "spell, Swap a slot in the TEXTURES rows of the phase first, then pick it here.");
        if (current >= 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Swap image"))
                _texSwapTarget = (model.Path, current);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Replace the image in this slot with any other BLP.");
        }
        return changed;
    }

    // ── mesh layer body ──────────────────────────────────────────────────────

    /// <summary>One mesh layer's editor. Returns true when a rebuild is due.</summary>
    private bool DrawCreatorMeshBody(CreatorModelDoc model, MeshSnapshot mesh)
    {
        float cs = CreatorUiScale;
        bool dirty = false;
        MeshPatch edit = model.MeshEdits.TryGetValue(mesh.BatchIndex, out var found)
            ? found : new MeshPatch { BatchIndex = mesh.BatchIndex };
        void Keep()
        {
            if (edit.IsEmpty) model.MeshEdits.Remove(mesh.BatchIndex);
            else model.MeshEdits[mesh.BatchIndex] = edit;
        }

        ImGui.TextDisabled($"submesh {mesh.SubmeshIndex} (geoset id {mesh.SubmeshId}), " +
                           $"{mesh.TriangleIndexCount / 3} triangles" +
                           (mesh.LayersOnSubmesh > 1 ? $", one of {mesh.LayersOnSubmesh} layers on this geometry" : ""));
        if (mesh.BatchIndex >= model.OriginalLayerCount)
        {
            if (ImGui.SmallButton("Remove layer"))
            {
                RemoveCreatorAddedLayer(model, mesh.BatchIndex);
                return true;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Delete this added layer (authored layers can only be hidden).");
        }
        else if (model.OriginalLayerCount + model.AddedLayers.Count < 4000 && ImGui.SmallButton("Duplicate layer"))
        {
            model.AddedLayers.Add(mesh.BatchIndex);
            dirty = true;
        }
        if (mesh.BatchIndex < model.OriginalLayerCount && ImGui.IsItemHovered())
            ImGui.SetTooltip("ADD a copy of this layer on the same geometry, with its own material: " +
                             "a second image or blend over the same planes.");

        bool visible = !model.HiddenSubmeshes.Contains(mesh.SubmeshIndex);
        if (ImGui.Checkbox("Visible", ref visible))
        {
            if (visible) model.HiddenSubmeshes.Remove(mesh.SubmeshIndex);
            else model.HiddenSubmeshes.Add(mesh.SubmeshIndex);
            dirty = true;
        }
        CreatorHelp("Switch this piece of geometry off: its triangle count is zeroed in the M2, " +
            "so nothing draws it - here or in the finished patch. Every layer stacked on the " +
            "same submesh goes with it.");
        if (!visible)
            ImGui.TextDisabled("Hidden. The dials below still edit this layer; they show once it is visible again.");

        ImGui.Spacing();
        int? slot = edit.TextureSlot;
        if (CreatorTextureSlotCombo(model, "Image", mesh.TextureSlot, ref slot, "meshtex"))
        {
            edit.TextureSlot = slot;
            dirty = true;
        }

        ushort? blend = edit.Blend;
        if (CreatorBlendCombo("Blend", mesh.Blend, ref blend, "meshblend"))
        {
            edit.Blend = blend;
            dirty = true;
        }

        ushort? flags = edit.MaterialFlags;
        if (CreatorMaterialFlagSwitches(mesh.MaterialFlags, ref flags, "meshflags"))
        {
            edit.MaterialFlags = flags;
            dirty = true;
        }

        ImGui.Spacing();
        ImGui.TextDisabled("COLOUR");
        if (mesh.ColorIndex >= 0 && mesh.ColorFirst is { } authoredColor)
        {
            Vector3 color = edit.Color ?? authoredColor;
            ImGui.SetNextItemWidth(160f * cs);
            if (ImGui.ColorEdit3(mesh.ColorKeys > 1 ? "Colour *" : "Colour", ref color, ImGuiColorEditFlags.NoInputs))
            {
                edit.Color = color;
                dirty = true;
            }
            if (CreatorResetKnob("meshcolor") && edit.Color is not null)
            {
                edit.Color = null;
                dirty = true;
            }
            CreatorHelp("The layer's colour track multiplies the texture. " +
                (mesh.ColorKeys > 1 ? $"Animated ({mesh.ColorKeys} keys) - this sets the FIRST key. " : "") +
                "The whole-model Hue dial hue-maps every colour track at draw time on top of this.");
            if (mesh.AlphaFirst is { } authoredAlpha)
            {
                float alpha = edit.Alpha ?? authoredAlpha;
                ImGui.SetNextItemWidth(CreatorControlWidth);
                if (ImGui.SliderFloat(mesh.AlphaKeys > 1 ? "Alpha *" : "Alpha", ref alpha, 0f, 1f, "%.2f"))
                {
                    edit.Alpha = alpha;
                    dirty = true;
                }
                if (CreatorResetKnob("meshalpha") && edit.Alpha is not null)
                {
                    edit.Alpha = null;
                    dirty = true;
                }
                CreatorHelp("The colour record's alpha (first key). 0 makes the layer invisible " +
                    "without removing it.");
            }
        }
        else ImGui.TextDisabled("(no colour track on this layer)");

        if (mesh.TransparencyIndex >= 0 && mesh.TransparencyFirst is { } authoredWeight)
        {
            float weight = edit.Transparency ?? authoredWeight;
            ImGui.SetNextItemWidth(CreatorControlWidth);
            if (ImGui.SliderFloat(mesh.TransparencyKeys > 1 ? "Transparency *" : "Transparency", ref weight, 0f, 1f, "%.2f"))
            {
                edit.Transparency = weight;
                dirty = true;
            }
            if (CreatorResetKnob("meshweight") && edit.Transparency is not null)
            {
                edit.Transparency = null;
                dirty = true;
            }
            CreatorHelp("The texture-weight track (first key): 1 = fully drawn, 0 = gone. " +
                (mesh.TransparencyKeys > 1 ? $"Animated ({mesh.TransparencyKeys} keys) - the fade-in/out envelope lives here." : ""));
        }

        if (ImGui.SmallButton("Reset layer") && model.MeshEdits.Remove(mesh.BatchIndex)) dirty = true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Clear every override on this layer (visibility stays).");
        Keep();
        return dirty;
    }

    // ── ribbon body ──────────────────────────────────────────────────────────

    /// <summary>One ribbon's editor. Returns true when a rebuild is due.</summary>
    private bool DrawCreatorRibbonBody(CreatorModelDoc model, RibbonSnapshot ribbon)
    {
        float cs = CreatorUiScale;
        bool dirty = false;
        RibbonPatch edit = model.RibbonEdits.TryGetValue(ribbon.Index, out var found)
            ? found : new RibbonPatch { RibbonIndex = ribbon.Index };
        void Keep()
        {
            if (edit.IsEmpty) model.RibbonEdits.Remove(ribbon.Index);
            else model.RibbonEdits[ribbon.Index] = edit;
        }

        if (ribbon.Index >= model.OriginalRibbonCount)
        {
            if (ImGui.SmallButton("Remove ribbon"))
            {
                RemoveCreatorAddedRibbon(model, ribbon.Index);
                return true;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Delete this added ribbon (authored ribbons can only be disabled).");
        }
        else if (model.OriginalRibbonCount + model.AddedRibbons.Count < 250 && ImGui.SmallButton("Duplicate ribbon"))
        {
            model.AddedRibbons.Add(ribbon.Index);
            dirty = true;
        }
        if (ribbon.Index < model.OriginalRibbonCount && ImGui.IsItemHovered())
            ImGui.SetTooltip("ADD a copy of this trail with private tracks and material - re-parent or " +
                             "recolour the copy independently.");
        bool on = !model.DisabledRibbons.Contains(ribbon.Index);
        if (ImGui.Checkbox("Enabled", ref on))
        {
            if (on) model.DisabledRibbons.Remove(ribbon.Index);
            else model.DisabledRibbons.Add(ribbon.Index);
            dirty = true;
        }
        CreatorHelp("Switch the trail off wholesale: it commits no edges and every visibility " +
            "and alpha key is zeroed in the M2, so nothing draws it here or in the finished patch.");
        if (!on)
            ImGui.TextDisabled("Off. The dials below still edit this ribbon; they show once it is on again.");
        if (ribbon.Silenced)
            ImGui.TextDisabled("Authored with 0 edges per second: it never draws unless you raise that.");

        ImGui.Spacing();
        int? slot = edit.TextureSlot;
        if (CreatorTextureSlotCombo(model, "Image", ribbon.TextureSlot, ref slot, "ribtex"))
        {
            edit.TextureSlot = slot;
            dirty = true;
        }
        ushort? blend = edit.Blend;
        if (CreatorBlendCombo("Blend", ribbon.Blend, ref blend, "ribblend"))
        {
            edit.Blend = blend;
            dirty = true;
        }

        bool Track(string label, float? authored, int keys, float min, float max, Func<float?> get,
            Action<float?> set, string help, string id)
        {
            if (authored is not { } a) return false;
            float value = get() ?? a;
            ImGui.SetNextItemWidth(CreatorControlWidth);
            bool moved = ImGui.SliderFloat(keys > 1 ? $"{label} *" : label, ref value, min, max, "%.3f");
            if (moved) set(value);
            if (CreatorResetKnob(id) && get() is not null) { set(null); moved = true; }
            CreatorHelp(help + $"\n\nAuthored: {a:0.###}" +
                (keys > 1 ? $" (animated, {keys} keys - the slider sets the first)" : ""));
            return moved;
        }
        bool Plain(string label, float authored, float min, float max, Func<float?> get,
            Action<float?> set, string help, string id)
        {
            float value = get() ?? authored;
            ImGui.SetNextItemWidth(CreatorControlWidth);
            bool moved = ImGui.SliderFloat(label, ref value, min, max, "%.3f");
            if (moved) set(value);
            if (CreatorResetKnob(id) && get() is not null) { set(null); moved = true; }
            CreatorHelp(help + $"\n\nAuthored: {authored:0.###}");
            return moved;
        }

        ImGui.Spacing();
        ImGui.TextDisabled("SHAPE");
        dirty |= Track("Height above", ribbon.HeightAboveFirst, ribbon.HeightAboveKeys, 0f, 10f,
            () => edit.HeightAbove, v => edit.HeightAbove = v,
            "How far the strip reaches ABOVE the bone, in yards.", "ribabove");
        dirty |= Track("Height below", ribbon.HeightBelowFirst, ribbon.HeightBelowKeys, 0f, 10f,
            () => edit.HeightBelow, v => edit.HeightBelow = v,
            "How far the strip reaches BELOW the bone, in yards.", "ribbelow");
        dirty |= Plain("Edges / second", ribbon.EdgesPerSecond, 0f, 120f,
            () => edit.EdgesPerSecond, v => edit.EdgesPerSecond = v,
            "How often a new edge is committed behind the bone. More = smoother trail; 0 = none.", "ribeps");
        dirty |= Plain("Edge lifetime", ribbon.EdgeLifetime, 0.05f, 5f,
            () => edit.EdgeLifetime, v => edit.EdgeLifetime = v,
            "Seconds each edge lives: the trail's LENGTH in time.", "riblife");
        dirty |= Plain("Gravity", ribbon.Gravity, -20f, 20f,
            () => edit.Gravity, v => edit.Gravity = v,
            "Pulls old edges down (positive) or up (negative).", "ribgrav");

        var position = new Vector3(edit.PositionX ?? ribbon.PositionX,
            edit.PositionY ?? ribbon.PositionY, edit.PositionZ ?? ribbon.PositionZ);
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.DragFloat3("Local position", ref position, 0.05f, -50f, 50f, "%.3f"))
        {
            edit.PositionX = position.X; edit.PositionY = position.Y; edit.PositionZ = position.Z;
            dirty = true;
        }
        if (CreatorResetKnob("ribpos") &&
            (edit.PositionX is not null || edit.PositionY is not null || edit.PositionZ is not null))
        {
            edit.PositionX = edit.PositionY = edit.PositionZ = null;
            dirty = true;
        }
        CreatorHelp("Where the strip's head sits, in WoW's Z-up local frame of the effect, offset " +
            $"from bone {ribbon.Bone} (the bone's sweep is what draws the crescent).");

        int bone = edit.Bone ?? ribbon.Bone;
        ImGui.SetNextItemWidth(110f * cs);
        if (ImGui.InputInt("Bone", ref bone))
        {
            edit.Bone = (ushort)Math.Clamp(bone, 0, 65535);
            dirty = true;
        }
        if (CreatorResetKnob("ribbone") && edit.Bone is not null)
        {
            edit.Bone = null;
            dirty = true;
        }
        CreatorHelp("The bone the strip rides. Re-parenting to another animated bone is the " +
            "way to change WHICH sweep draws the trail.");

        ImGui.Spacing();
        ImGui.TextDisabled("COLOUR");
        if (ribbon.ColorFirst is { } authoredColor)
        {
            Vector3 color = edit.Color ?? authoredColor;
            ImGui.SetNextItemWidth(160f * cs);
            if (ImGui.ColorEdit3(ribbon.ColorKeys > 1 ? "Colour *" : "Colour", ref color, ImGuiColorEditFlags.NoInputs))
            {
                edit.Color = color;
                dirty = true;
            }
            if (CreatorResetKnob("ribcolor") && edit.Color is not null)
            {
                edit.Color = null;
                dirty = true;
            }
            CreatorHelp("Multiplies the trail texture. " +
                (ribbon.ColorKeys > 1 ? $"Animated ({ribbon.ColorKeys} keys) - this sets the first key." : ""));
        }
        dirty |= Track("Alpha", ribbon.AlphaFirst, ribbon.AlphaKeys, 0f, 1f,
            () => edit.Alpha, v => edit.Alpha = v, "The trail's opacity (first key).", "ribalpha");

        int rows = edit.TextureRows ?? ribbon.TextureRows, cols = edit.TextureColumns ?? ribbon.TextureColumns;
        ImGui.SetNextItemWidth(90f * cs);
        if (ImGui.InputInt("Rows", ref rows)) { edit.TextureRows = (ushort)Math.Clamp(rows, 1, 64); dirty = true; }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90f * cs);
        if (ImGui.InputInt("Cols", ref cols)) { edit.TextureColumns = (ushort)Math.Clamp(cols, 1, 64); dirty = true; }
        if (CreatorResetKnob("ribcells") && (edit.TextureRows is not null || edit.TextureColumns is not null))
        {
            edit.TextureRows = edit.TextureColumns = null;
            dirty = true;
        }
        CreatorHelp("Sprite-sheet cells of the trail texture (1 x 1 = the whole image).");

        if (ImGui.SmallButton("Reset ribbon") && model.RibbonEdits.Remove(ribbon.Index)) dirty = true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Clear every override on this ribbon (enabled stays).");
        Keep();
        return dirty;
    }

    // ── the classic editor's categories ──────────────────────────────────────

    /// <summary>MESH LAYERS and RIBBONS as drill-down categories, for the classic floating
    /// panel and the deck. Returns true when a rebuild is due.</summary>
    private bool DrawCreatorMeshRibbonCategories(CreatorModelDoc model, float cs)
    {
        bool dirty = false;
        if (model.Meshes.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("MESH LAYERS");
            CreatorHelp("The model's own textured geometry - the planes a crescent, a glow " +
                "sheet or a ring is made of, drawn by the mesh renderer regardless of any " +
                "emitter. One row per layer (batch): its image, blend, material flags, " +
                "colour and transparency, and an on/off switch for its geometry.");
            foreach (MeshSnapshot mesh in model.Meshes)
            {
                Vector4? marker = mesh.TextureSlot >= 0 ? CreatorSlotColor(mesh.TextureSlot) : null;
                if (!CreatorCategory($"ws-{model.Path}-mesh{mesh.BatchIndex}",
                        CreatorMeshLabel(model, mesh, prefixState: false), marker: marker))
                    continue;
                ImGui.PushID(10000 + mesh.BatchIndex);
                ImGui.Indent(10f * cs);
                dirty |= DrawCreatorMeshBody(model, mesh);
                ImGui.Unindent(10f * cs);
                ImGui.PopID();
            }
        }
        if (model.Ribbons.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("RIBBONS");
            CreatorHelp("Trail strips committed behind a bone over time - weapon trails and " +
                "slash streaks. Shape, timing, image, blend, colour, and an on/off switch.");
            foreach (RibbonSnapshot ribbon in model.Ribbons)
            {
                Vector4? marker = ribbon.TextureSlot >= 0 ? CreatorSlotColor(ribbon.TextureSlot) : null;
                if (!CreatorCategory($"ws-{model.Path}-ribbon{ribbon.Index}",
                        CreatorRibbonLabel(model, ribbon, prefixState: false), marker: marker))
                    continue;
                ImGui.PushID(20000 + ribbon.Index);
                ImGui.Indent(10f * cs);
                dirty |= DrawCreatorRibbonBody(model, ribbon);
                ImGui.Unindent(10f * cs);
                ImGui.PopID();
            }
        }
        if (model.Bones.Count > 0)
        {
            ImGui.Spacing();
            if (CreatorCategory($"ws-{model.Path}-bones",
                    $"Bones ({model.Bones.Count})" + (model.BoneEdits.Count > 0 ? " *" : "")))
            {
                ImGui.PushID(30000);
                ImGui.Indent(10f * cs);
                dirty |= DrawCreatorBonesBody(model);
                ImGui.Unindent(10f * cs);
                ImGui.PopID();
            }
        }
        return dirty;
    }

    // ── the IDE phase inspector's lists ──────────────────────────────────────

    /// <summary>On/off + select rows for the phase inspector. Returns true when a rebuild is due.</summary>
    private bool DrawCreatorMeshRibbonLists(CreatorModelDoc model)
    {
        float cs = CreatorUiScale;
        bool dirty = false;
        if (model.Meshes.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled($"MESH LAYERS ({model.Meshes.Count})");
            CreatorHelp("The model's own geometry, drawn regardless of any emitter. Tick to " +
                "show/hide a layer's geometry; pick one in the tree (or here) to edit it.");
            foreach (MeshSnapshot mesh in model.Meshes)
            {
                ImGui.PushID(10000 + mesh.BatchIndex);
                bool visible = !model.HiddenSubmeshes.Contains(mesh.SubmeshIndex);
                if (ImGui.Checkbox("##on", ref visible))
                {
                    if (visible) model.HiddenSubmeshes.Remove(mesh.SubmeshIndex);
                    else model.HiddenSubmeshes.Add(mesh.SubmeshIndex);
                    dirty = true;
                }
                ImGui.SameLine();
                if (mesh.TextureSlot >= 0)
                {
                    ImGui.ColorButton("##swatch", CreatorSlotColor(mesh.TextureSlot),
                        ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop,
                        new Vector2(10f * cs, 10f * cs));
                    ImGui.SameLine();
                }
                if (ImGui.Selectable($"m{mesh.BatchIndex}  {CreatorTextureName(model, mesh.TextureSlot)}, " +
                                     $"blend {mesh.Blend}, submesh {mesh.SubmeshIndex}", false))
                    SpellIdeSelect(new SpellIdeSelection(SpellIdeKind.Mesh, model.Path, mesh.BatchIndex));
                ImGui.PopID();
            }
        }
        if (model.Ribbons.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled($"RIBBONS ({model.Ribbons.Count})");
            CreatorHelp("Trail strips behind a bone. Tick to enable/disable; pick one to edit it.");
            foreach (RibbonSnapshot ribbon in model.Ribbons)
            {
                ImGui.PushID(20000 + ribbon.Index);
                bool on = !model.DisabledRibbons.Contains(ribbon.Index);
                if (ImGui.Checkbox("##on", ref on))
                {
                    if (on) model.DisabledRibbons.Remove(ribbon.Index);
                    else model.DisabledRibbons.Add(ribbon.Index);
                    dirty = true;
                }
                ImGui.SameLine();
                if (ribbon.TextureSlot >= 0)
                {
                    ImGui.ColorButton("##swatch", CreatorSlotColor(ribbon.TextureSlot),
                        ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop,
                        new Vector2(10f * cs, 10f * cs));
                    ImGui.SameLine();
                }
                if (ImGui.Selectable($"r{ribbon.Index}  {CreatorTextureName(model, ribbon.TextureSlot)}, " +
                                     $"blend {ribbon.Blend}, bone {ribbon.Bone}", false))
                    SpellIdeSelect(new SpellIdeSelection(SpellIdeKind.Ribbon, model.Path, ribbon.Index));
                ImGui.PopID();
            }
        }
        return dirty;
    }

    // ── the IDE tree's rows ──────────────────────────────────────────────────

    private void DrawSpellIdeMeshRibbonRows(CreatorModelDoc model, float cs)
    {
        foreach (MeshSnapshot mesh in model.Meshes)
        {
            bool selected = _spellIdeSelection.Kind == SpellIdeKind.Mesh &&
                            _spellIdeSelection.IsPhaseOf(model.Path) && _spellIdeSelection.Emitter == mesh.BatchIndex;
            ImGui.PushID(10000 + mesh.BatchIndex);
            if (mesh.TextureSlot >= 0)
            {
                ImGui.ColorButton("##swatch", CreatorSlotColor(mesh.TextureSlot),
                    ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop,
                    new Vector2(10f * cs, 10f * cs));
                ImGui.SameLine();
            }
            if (ImGui.Selectable(CreatorMeshLabel(model, mesh, prefixState: true), selected))
                SpellIdeSelect(new SpellIdeSelection(SpellIdeKind.Mesh, model.Path, mesh.BatchIndex));
            ImGui.PopID();
        }
        foreach (RibbonSnapshot ribbon in model.Ribbons)
        {
            bool selected = _spellIdeSelection.Kind == SpellIdeKind.Ribbon &&
                            _spellIdeSelection.IsPhaseOf(model.Path) && _spellIdeSelection.Emitter == ribbon.Index;
            ImGui.PushID(20000 + ribbon.Index);
            if (ribbon.TextureSlot >= 0)
            {
                ImGui.ColorButton("##swatch", CreatorSlotColor(ribbon.TextureSlot),
                    ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop,
                    new Vector2(10f * cs, 10f * cs));
                ImGui.SameLine();
            }
            if (ImGui.Selectable(CreatorRibbonLabel(model, ribbon, prefixState: true), selected))
                SpellIdeSelect(new SpellIdeSelection(SpellIdeKind.Ribbon, model.Path, ribbon.Index));
            ImGui.PopID();
        }
    }

    /// <summary>The IDE inspector body for a mesh layer or a ribbon selection.</summary>
    private void DrawSpellIdeMeshRibbonBody(CreatorModelDoc model, SpellIdeSelection selection)
    {
        if (selection.Kind == SpellIdeKind.Mesh)
        {
            MeshSnapshot? mesh = model.Meshes.FirstOrDefault(m => m.BatchIndex == selection.Emitter);
            if (mesh is null)
            {
                ImGui.TextDisabled("This layer is gone - pick another in the tree.");
                return;
            }
            ImGui.PushID(10000 + mesh.BatchIndex);
            bool dirty = DrawCreatorMeshBody(model, mesh);
            ImGui.PopID();
            if (dirty) RebuildCreatorModel(model);
            return;
        }
        RibbonSnapshot? ribbon = model.Ribbons.FirstOrDefault(r => r.Index == selection.Emitter);
        if (ribbon is null)
        {
            ImGui.TextDisabled("This ribbon is gone - pick another in the tree.");
            return;
        }
        ImGui.PushID(20000 + ribbon.Index);
        bool ribbonDirty = DrawCreatorRibbonBody(model, ribbon);
        ImGui.PopID();
        if (ribbonDirty) RebuildCreatorModel(model);
    }
}
