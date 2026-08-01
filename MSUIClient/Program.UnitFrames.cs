using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    /// <summary>The build-5875 PlayerFrame/TargetFrame geometry and authored texture.</summary>
    private void DrawVanillaUnitFrame(WorldEntity unit, Vector2 authoredOrigin, bool playerFrame,
        string name, FactionReaction reaction, uint portraitTexture, float combatFlash)
    {
        if (_gameplayArt is null) return;
        float s = GameplayUiScale();
        Vector2 p = authoredOrigin * s;
        Vector2 size = new(232, 100);
        // The root frame is 232x100. The target's aura children deliberately
        // extend below it and ImGui needs a taller transparent host to avoid clipping.
        Vector2 windowSize = playerFrame ? size : new Vector2(232, 110);
        CollectGameplayLayout(playerFrame ? "player-frame" : "target-frame",
            authoredOrigin.X, authoredOrigin.Y, windowSize.X, windowSize.Y, p, windowSize * s);
        ImGui.SetNextWindowPos(p, ImGuiCond.Always);
        ImGui.SetNextWindowSize(windowSize * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                                 ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
                                 ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoInputs;
        if (!ImGui.Begin(playerFrame ? "##vanilla-player-frame" : "##vanilla-target-frame", flags))
        { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();

        string parityPanel = playerFrame ? "player-frame" : "target-frame";
        if (_uiParityArmed && _uiParityPanel == parityPanel)
        {
            string root = playerFrame ? "PlayerFrame" : "TargetFrame";
            BeginUiParityFrame(p, s);
            CollectUiParity(root, "Button", p, size * s, parent: "", point: "TOPLEFT",
                offsetX: playerFrame ? "-19" : "250", offsetY: "-4",
                strata: playerFrame ? "BACKGROUND" : "LOW");
            CollectUiParity(playerFrame ? "PlayerFrameBackground" : "TargetFrameBackground", "Texture",
                p + new Vector2(playerFrame ? 106f : 7f, 22f) * s, new Vector2(119f, 41f) * s,
                parent: root, point: playerFrame ? "TOPLEFT" : "TOPRIGHT",
                offsetX: playerFrame ? "106" : "-106", offsetY: "-22", layer: "BACKGROUND",
                strata: playerFrame ? "BACKGROUND" : "LOW");
            CollectUiParity(playerFrame ? "PlayerPortrait" : "TargetPortrait", "Texture",
                p + new Vector2(playerFrame ? 42f : 126f, 12f) * s, new Vector2(64f) * s,
                parent: root, point: playerFrame ? "TOPLEFT" : "TOPRIGHT",
                offsetX: playerFrame ? "42" : "-42", offsetY: "-12",
                layer: playerFrame ? "ARTWORK" : "BORDER", strata: playerFrame ? "BACKGROUND" : "LOW");
        }

        float barX = playerFrame ? 106f : 7f;
        Vector2 troughMin = p + new Vector2(barX, 22) * s;
        dl.AddRectFilled(troughMin, troughMin + new Vector2(119, 41) * s, 0x80000000);

        if (!playerFrame)
        {
            uint plate = _gameplayArt.Handle(@"Interface\TargetingFrame\UI-TargetingFrame-LevelBackground");
            if (plate != 0)
            {
                uint tint = ReactionColorU32(reaction, unit.IsPlayer, unit.IsDead);
                dl.AddImage((nint)plate, troughMin, troughMin + new Vector2(119, 19) * s,
                    Vector2.Zero, Vector2.One, tint);
            }
        }

        float portraitX = playerFrame ? 42f : 126f;
        Vector2 portraitMin = p + new Vector2(portraitX, 12) * s;
        DrawUnitPortraitImage(dl, unit, portraitMin, 64f * s, portraitTexture, playerFrame);

        DrawVanillaStatusBar(dl, p + new Vector2(barX, 41) * s, new Vector2(119, 12) * s,
            unit.HealthFraction, new Vector4(0, 1, 0, 1));
        if (unit.Fields.ActiveMaxPower > 0)
            DrawVanillaStatusBar(dl, p + new Vector2(barX, 52) * s, new Vector2(119, 12) * s,
                unit.PowerFraction, PowerColor(unit.Fields.PowerType));

        uint frame = _gameplayArt.Handle(@"Interface\TargetingFrame\UI-TargetingFrame");
        if (frame != 0)
        {
            Vector2 uv0 = playerFrame ? new Vector2(1f, 0) : new Vector2(0.09375f, 0);
            Vector2 uv1 = playerFrame ? new Vector2(0.09375f, 0.78125f) : new Vector2(1f, 0.78125f);
            dl.AddImage((nint)frame, p, p + size * s, uv0, uv1);
        }

        Vector2 nameCenter = p + new Vector2(playerFrame ? 166 : 66, 31) * s;
        DrawUnitFrameText(dl, nameCenter, name, 10f * s, UiGoldU32());
        Vector2 levelCenter = p + new Vector2(playerFrame ? 53 : 179, 66) * s;
        if (unit.Level > 0)
            DrawUnitFrameText(dl, levelCenter, unit.Level.ToString(), 10f * s,
                playerFrame ? UiGoldU32() : ReactionColorU32(reaction, unit.IsPlayer, unit.IsDead));
        else if (!playerFrame)
            DrawArt(dl, @"Interface\TargetingFrame\UI-TargetingFrame-Skull",
                levelCenter - new Vector2(8) * s, new Vector2(16), s);

        if (!playerFrame && unit.IsDead)
            DrawUnitFrameText(dl, p + new Vector2(66, 47) * s, "DEAD", 10f * s, UiGoldU32());

        if (combatFlash > 0)
            dl.AddCircle(portraitMin + new Vector2(32) * s, 29f * s,
                ImGui.ColorConvertFloat4ToU32(new Vector4(1, 0.12f, 0.08f,
                    Math.Clamp(combatFlash / 0.35f, 0, 1))), 48, 2f * s);
        if (!playerFrame) DrawTargetAuras(dl, unit, p, s);
        if (_uiParityArmed && _uiParityPanel == parityPanel) MarkUiParityFrameComplete();
        ImGui.End();
    }

    private static void DrawUnitFrameText(ImDrawListPtr dl, Vector2 center,
        string text, float size, uint color)
    {
        ImFontPtr font = ImGui.GetFont();
        Vector2 measured = ImGui.CalcTextSize(text) *
            (size / MathF.Max(1f, ImGui.GetFontSize()));
        Vector2 pos = center - measured * 0.5f;
        float shadow = MathF.Max(1f, MathF.Round(size * GlueTune.ShadowOffsetRatio));
        dl.AddText(font, size, pos + new Vector2(shadow),
            ImGui.ColorConvertFloat4ToU32(GlueTune.ShadowColor), text);
        WowSkin.OutlineText(dl, font, size, pos, text);
        dl.AddText(font, size, pos, color, text);
    }

    private void DrawTargetAuras(ImDrawListPtr dl, WorldEntity unit, Vector2 frameMin, float scale)
    {
        if (_gameplayArt is null || _spellCatalog is null) return;
        int buffs = 0, debuffs = 0;
        foreach (var aura in unit.Fields.Auras())
        {
            if (!_spellCatalog.TryGet(aura.SpellId, out SpellInfo spell) || spell.IconPath.Length == 0) continue;
            uint icon = _gameplayArt.Handle(spell.IconPath);
            if (icon == 0) continue;
            bool buff = aura.Slot < 32;
            int index = buff ? buffs++ : debuffs++;
            if (buff && index >= 5 || !buff && index >= 16) continue;
            int col = buff ? index : index % 6;
            int row = buff ? 0 : index / 6;
            float step = buff ? 24f : 20f;
            float size = buff ? 21f : 17f;
            Vector2 start = frameMin + new Vector2(5f, buff ? 87f : 68f) * scale;
            Vector2 min = start + new Vector2(col * step, row * 20f) * scale;
            Vector2 max = min + new Vector2(size) * scale;
            dl.AddImage((nint)icon, min, max);
            uint border = buff ? 0xff40d0ffu : 0xff4040ffu;
            dl.AddRect(min, max, border, 0, ImDrawFlags.None, MathF.Max(1, scale));
            if (aura.Stacks > 1)
                dl.AddText(max - new Vector2(7, 11) * scale, 0xffffffff, aura.Stacks.ToString());
        }
    }

    private void DrawPlayerAuraBar()
    {
        if (_net is null || _gameplayArt is null || _spellCatalog is null ||
            !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return;
        float s = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize;
        ImDrawListPtr dl = ImGui.GetBackgroundDrawList();
        Vector2 logicalDisplay = display / s;
        Vector2 frameMin = new(logicalDisplay.X - 255f, 13f);
        Vector2 framePhysical = frameMin * s;
        if (_uiParityArmed && _uiParityPanel == "buff-frame")
        {
            BeginUiParityFrame(framePhysical, s);
            CollectUiParity("BuffFrame", "Frame", framePhysical, new Vector2(50) * s,
                parent: "", point: "TOPRIGHT", relativeTo: "UIParent", relativePoint: "TOPRIGHT",
                offsetX: "-205", offsetY: "-13", strata: "LOW");
        }
        int shown = 0, buffShown = 0, debuffShown = 0;
        foreach (var aura in player.Fields.Auras())
        {
            if (!_spellCatalog.TryGet(aura.SpellId, out SpellInfo spell) || spell.IconPath.Length == 0) continue;
            uint icon = _gameplayArt.Handle(spell.IconPath);
            if (icon == 0) continue;
            bool harmful = aura.Slot >= 32;
            int cohort = harmful ? debuffShown++ : buffShown++;
            if (harmful ? cohort >= 8 : cohort >= 16) continue;
            int col = cohort % 8, row = harmful ? 2 : cohort / 8;
            Vector2 max = new((logicalDisplay.X - 205f - col * 35f) * s,
                (13f + row * 35f + 30f) * s);
            Vector2 min = max - new Vector2(30) * s;
            if (_uiParityArmed && _uiParityPanel == "buff-frame" && shown == 0)
            {
                CollectUiParity("BuffButton0", "Button", min, new Vector2(30) * s,
                    parent: "BuffFrame", point: "TOPRIGHT", offsetX: "0", offsetY: "0", strata: "LOW");
                CollectUiParity("BuffButton0Icon", "Texture", min, new Vector2(30) * s,
                    parent: "BuffButton0", layer: "BACKGROUND", strata: "LOW");
                CollectUiParity("BuffButton0Duration", "FontString", max, Vector2.Zero,
                    parent: "BuffFrame", point: "TOP", font: "BuffButtonDurationTemplate",
                    fontPath: @"Fonts\FRIZQT__.TTF", fontSize: "10", color: "#FFD100FF",
                    layer: "ARTWORK", strata: "LOW");
            }
            dl.AddImage((nint)icon, min, max);
            if (harmful)
            {
                uint border = _gameplayArt.Handle(@"Interface\Buttons\UI-Debuff-Overlays");
                if (border != 0)
                    dl.AddImage((nint)border, min - new Vector2(1.5f, 1f) * s,
                        max + new Vector2(1.5f, 1f) * s,
                        new Vector2(.296875f, 0), new Vector2(.5703125f, .515625f));
            }
            if (aura.Stacks > 1)
                dl.AddText(max - new Vector2(9, 13) * s, 0xffffffff, aura.Stacks.ToString());
            if (_playerAuraDurations.TryGetValue(aura.Slot, out var timer))
            {
                double remaining = Math.Max(0, timer.Expires - NowSeconds());
                string text = remaining >= 60 ? $"{Math.Ceiling(remaining / 60)}m" : $"{Math.Ceiling(remaining)}s";
                dl.AddText(new Vector2(min.X, max.Y + 1 * s), 0xffffffff, text);
            }
            if (ImGui.IsMouseHoveringRect(min, max, false) &&
                ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                CancelPlayerAura(new AuraSnapshot(aura.Slot, aura.SpellId, aura.Flags, aura.Stacks), "UI_RIGHT_CLICK");
            if (++shown >= 24) break;
        }
        if (_uiParityArmed && _uiParityPanel == "buff-frame" && shown > 0) MarkUiParityFrameComplete();
    }

    private void DrawUnitPortraitImage(ImDrawListPtr dl, WorldEntity unit, Vector2 min, float size,
        uint liveTexture, bool playerFrame)
    {
        uint texture = liveTexture;
        Vector2 uv0 = new(0, 1), uv1 = new(1, 0);
        if (texture == 0 && _gameplayArt is not null)
        {
            string fallback;
            if (unit.IsPlayer)
            {
                var b = unit.Fields.Bytes0;
                string sex = b.Gender == 1 ? "Female" : "Male";
                // The streamed stand-in art uses the asset race token, which is "Scourge"
                // for the undead (benilla portrait/mod.rs temporary_portrait race map).
                string race = b.Race == 5 ? "Scourge" : RaceName(b.Race).Replace(" ", "");
                fallback = $@"Interface\CharacterFrame\TemporaryPortrait-{sex}-{race}";
            }
            else fallback = @"Interface\CharacterFrame\TemporaryPortrait-Monster";
            texture = _gameplayArt.Handle(fallback);
            uv0 = Vector2.Zero;
            uv1 = Vector2.One;
        }
        if (texture != 0)
        {
            // UI-TargetingFrame is the authored circular chrome and is drawn after this quad.
            // Its corners are TRANSPARENT (a thin ring band), so the square bake cannot hide
            // behind it: live portrait textures are pre-masked to the inscribed circle at bake
            // time (PortraitRenderTarget.ApplyCircularMask), matching the reference client's
            // shader-side circular cut. ImGui.NET's rounded-image path emitted only one textured
            // fan triangle on this backend (the face-shaped wedge captured in-game), so it
            // cannot serve as a stencil.
            dl.AddImage((nint)texture, min, min + new Vector2(size), uv0, uv1);
        }
    }

    private void DrawVanillaStatusBar(ImDrawListPtr dl, Vector2 min, Vector2 size,
        float fraction, Vector4 color)
    {
        uint texture = _gameplayArt?.Handle(@"Interface\TargetingFrame\UI-StatusBar") ?? 0;
        fraction = Math.Clamp(fraction, 0, 1);
        if (texture == 0 || fraction <= 0) return;
        Vector2 max = new(min.X + size.X * fraction, min.Y + size.Y);
        dl.AddImage((nint)texture, min, max, Vector2.Zero, new Vector2(fraction, 1),
            ImGui.ColorConvertFloat4ToU32(color));
    }

    private static uint ReactionColorU32(FactionReaction reaction, bool player, bool dead)
    {
        Vector4 color = dead && !player ? new Vector4(.498f, .498f, .498f, 1)
            : player ? new Vector4(.376f, .376f, 1, 1)
            : reaction switch
            {
                FactionReaction.Hostile => new Vector4(1, 0, 0, 1),
                FactionReaction.Friendly => new Vector4(0, 1, 0, 1),
                _ => new Vector4(1, 1, 0, 1),
            };
        return ImGui.ColorConvertFloat4ToU32(color);
    }
}
