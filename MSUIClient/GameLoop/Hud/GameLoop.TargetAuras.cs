using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private static GameTooltipOwnerKey TargetAuraTooltipOwner(ulong guid, byte slot) =>
        new($"target-aura-{guid:X16}", slot);

    private void DrawTargetAuras(WorldEntity unit, Vector2 frameMin, float scale)
    {
        if (_gameplayArt is null) return;
        var visible = new List<(AuraSnapshot Aura, SpellInfo? Spell, uint Icon)>();
        int buffs = 0, debuffs = 0;
        foreach (AuraSnapshot aura in OrderedAuras(unit))
        {
            if (!TryVisibleAuraSpell(aura.SpellId, out SpellInfo? spell)) continue;
            uint icon = _gameplayArt.Handle(spell?.IconPath ?? @"Interface\Icons\INV_Misc_QuestionMark");
            if (icon == 0) continue;
            int index = aura.Helpful ? buffs++ : debuffs++;
            if (index >= (aura.Helpful ? TargetAuraUiLaw.HelpfulLimit : TargetAuraUiLaw.HarmfulLimit)) continue;
            visible.Add((aura, spell, icon));
        }
        bool friendly = unit.Guid == ControlledGuid || ReactionPlayerToward(unit) == FactionReaction.Friendly;
        int harmfulCount = Math.Min(debuffs, TargetAuraUiLaw.HarmfulLimit);
        float size = TargetAuraUiLaw.IconSize(harmfulCount);
        buffs = debuffs = 0;
        PreparedSharedSpellTooltip? hovered = null;
        foreach (var (aura, spell, icon) in visible)
        {
            bool harmful = !aura.Helpful;
            int index = harmful ? debuffs++ : buffs++;
            Vector2 min = frameMin + TargetAuraUiLaw.IconMin(harmful, index, harmfulCount, friendly) * scale;
            Vector2 max = min + new Vector2(size) * scale;
            // Individual transparent input hosts leave the spaces between icons clickable in world.
            ImGui.SetNextWindowPos(min - new Vector2(scale), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(size + 2) * scale, ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(0);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.Zero);
            bool begun = ImGui.Begin($"##target-aura-{(harmful ? 'd' : 'b')}-{index}",
                ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoBringToFrontOnFocus);
            ImGui.PopStyleVar(3);
            if (begun)
            {
                ImDrawListPtr dl = ImGui.GetWindowDrawList();
                dl.AddImage((nint)icon, min, max);
                if (harmful)
                {
                    uint color = ImGui.ColorConvertFloat4ToU32(BuffUiLaw.DebuffColor(spell?.DispelType ?? 0));
                    uint border = _gameplayArt.Handle(@"Interface\Buttons\UI-Debuff-Overlays");
                    if (border != 0)
                        dl.AddImage((nint)border, min - new Vector2(scale), max + new Vector2(scale),
                            new(BuffUiLaw.DebuffTexCoords.X, BuffUiLaw.DebuffTexCoords.Y),
                            new(BuffUiLaw.DebuffTexCoords.Z, BuffUiLaw.DebuffTexCoords.W), color);
                    else dl.AddRect(min, max, color, 0, ImDrawFlags.None, MathF.Max(1, scale));
                }
                if (aura.Stacks > 1)
                    GameText.DrawRightAligned(dl, "NumberFontNormalSmall", aura.Stacks.ToString(),
                        max - new Vector2(scale, GameText.EmPixels("NumberFontNormalSmall", scale)), scale);
                ImGui.SetCursorScreenPos(min);
                ImGui.InvisibleButton("##aura-hover", max - min);
                if (ImGui.IsItemHovered() && _skin is { } skin && _spellCatalog is { } catalog)
                {
                    var view = TargetAuraUiLaw.Tooltip(aura.SpellId, spell, catalog, aura.Level, harmful);
                    (string Text, uint Color)[]? extra = aura.Stacks > 1 ? [(aura.Stacks + " stacks", 0xffffffff)] : null;
                    hovered = new(TargetAuraTooltipOwner(unit.Guid, aura.Slot),
                        new(view, skin, scale, ImGui.GetIO().DisplaySize, SpellTooltipPlacement.OwnerRight,
                            min, max, SupplementalRows: extra));
                }
            }
            ImGui.End();
        }
        if (hovered is { } prepared)
            OfferPreservedSharedGameTooltipRenderer(prepared.Owner, () => DrawSpellTooltip(prepared.Snapshot));
    }
}
