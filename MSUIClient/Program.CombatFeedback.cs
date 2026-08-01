using System.Numerics;
using ImGuiNET;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private const float WorldTextLifetime = 1.5f;
    private const int MaxWorldTextPerUnit = 4;

    private sealed class FloatingCombatText
    {
        public ulong Target;
        public Vector3 Anchor;
        public string Text = "";
        public WorldCombatTextStyle Style;
        public bool Critical;
        public float Age;
        public int Lane;
    }

    private readonly List<FloatingCombatText> _floatingCombatText = new();
    private sealed class CenterText
    {
        public string Text = "";
        public CenterCombatTextStyle Style;
        public bool Critical;
        public float Age;
        public int Lane;
    }
    private readonly List<CenterText> _centerCombatText = new();
    private float _playerCombatFlash;
    private float _targetCombatFlash;
    private long _worldCombatTextSpawned;
    private long _worldCombatTextDropped;

    private void ResetCombatFeedback()
    {
        _floatingCombatText.Clear();
        _centerCombatText.Clear();
        _playerCombatFlash = 0f;
        _targetCombatFlash = 0f;
        _worldCombatTextSpawned = 0;
        _worldCombatTextDropped = 0;
    }

    private void ApplyCombatFeedback(CombatEvent combatEvent)
    {
        if (_net is null) return;

        foreach (ulong victim in CombatFeedbackLaw.FeedbackVictims(combatEvent))
        {
            if (victim == _net.PlayerGuid) _playerCombatFlash = 0.35f;
            if (victim == _selectionGuid) _targetCombatFlash = 0.35f;
        }

        foreach (WorldCombatTextCue cue in CombatFeedbackLaw.WorldText(combatEvent, _net.PlayerGuid))
        {
            if (!_entities.TryGet(cue.Target, out WorldEntity entity)) continue;
            int lane = _floatingCombatText.Count(t => t.Target == cue.Target);
            if (lane >= MaxWorldTextPerUnit)
            {
                _worldCombatTextDropped++;
                continue;
            }

            float scale = _creatures?.PickScale(entity) ?? MathF.Max(0.01f, entity.Scale);
            _floatingCombatText.Add(new FloatingCombatText
            {
                Target = cue.Target,
                Anchor = entity.Position + new Vector3(0, 0, MathF.Max(1.5f, 2.2f * scale)),
                Text = cue.Text,
                Style = cue.Style,
                Critical = cue.Critical,
                Lane = lane,
            });
            _worldCombatTextSpawned++;
        }

        foreach (CenterCombatTextCue cue in CombatFeedbackLaw.CenterText(combatEvent, _net.PlayerGuid))
        {
            if (_centerCombatText.Count == 20) _centerCombatText.RemoveAt(0);
            _centerCombatText.Add(new CenterText
            {
                Text = cue.Text,
                Style = cue.Style,
                Critical = cue.Critical,
                Lane = _centerCombatText.Count % 5,
            });
        }
    }

    private void UpdateCombatFeedback(float dt)
    {
        _playerCombatFlash = MathF.Max(0f, _playerCombatFlash - dt);
        _targetCombatFlash = MathF.Max(0f, _targetCombatFlash - dt);
        for (int i = _floatingCombatText.Count - 1; i >= 0; i--)
        {
            _floatingCombatText[i].Age += dt;
            if (_floatingCombatText[i].Age >= WorldTextLifetime)
                _floatingCombatText.RemoveAt(i);
        }
        for (int i = _centerCombatText.Count - 1; i >= 0; i--)
        {
            _centerCombatText[i].Age += dt;
            if (_centerCombatText[i].Age >= 1.9f) _centerCombatText.RemoveAt(i);
        }
    }

    private void DrawCombatHud()
    {
        BakeDirtyPortraits();
        DrawFloatingCombatText();
        DrawWorldUnitNames();
        DrawPlayerFrame();
        DrawTargetFrame();
        DrawPartyFrames();
        DrawPlayerAuraBar();
        DrawMinimap();
        DrawChatFrame();
        DrawCenterCombatText();
        DrawCastingBar();
        DrawActionBars();
        DrawLootFrame();
        DrawGameObjectFrame();
        DrawRestXpFrame();
        DrawDeathRezFrame();
        DrawHearthFrame();
        DrawTaxiFrame();
        DrawGossipFrame();
        DrawVendorFrame();
        DrawTrainerFrame();
        DrawQuestFrame();
        DrawBankFrame();
        DrawMailFrame();
        DrawAuctionFrame();
        DrawProfessionFrame();
        DrawGuildFrame();
        DrawTradeFrame();
        DrawKeybindingsFrame();
        DrawMacroFrame();
        DrawTooltipParityFrame();
        DrawUiErrorsParityFrame();
        DrawTabardFrame();
        DrawTalentFrame();
        DrawInventory();
        DrawCharacterPage();
        DrawSpellbook();
    }

    private void DrawPlayerFrame()
    {
        if (_net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return;
        DrawVanillaUnitFrame(player, new Vector2(-19, 4), playerFrame: true,
            _net.PlayerName, FactionReaction.Friendly,
            _playerPortraitUsable ? _playerPortrait?.TextureHandle ?? 0 : 0,
            _playerCombatFlash);
    }

    private static void DrawPowerBar(WorldEntity entity)
    {
        if (entity.Fields.ActiveMaxPower == 0) return;
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, PowerColor(entity.Fields.PowerType));
        ImGui.ProgressBar(entity.PowerFraction, new Vector2(-1, 12), "");
        ImGui.PopStyleColor();
    }

    private static Vector4 PowerColor(byte powerType) => powerType switch
    {
        1 => new Vector4(0.95f, 0.08f, 0.05f, 1f), // rage
        2 => new Vector4(1.00f, 0.50f, 0.25f, 1f), // focus
        3 => new Vector4(1.00f, 0.85f, 0.05f, 1f), // energy
        4 => new Vector4(0.00f, 0.75f, 1.00f, 1f), // happiness
        _ => new Vector4(0.05f, 0.30f, 0.95f, 1f), // mana
    };

    private static void PushUnitFrameBorder(float flash)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, flash > 0f ? 3f : 1f);
        ImGui.PushStyleColor(ImGuiCol.Border,
            flash > 0f ? new Vector4(1f, 0.12f, 0.08f, 1f) : new Vector4(0.25f, 0.25f, 0.25f, 1f));
    }

    private static void PopUnitFrameBorder()
    {
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    private void DrawFloatingCombatText()
    {
        if (SettingsModalOpen) return;
        Vector2 display = ImGui.GetIO().DisplaySize;
        float diagonal = MathF.Sqrt(display.X * display.X + display.Y * display.Y);
        ImDrawListPtr draw = ImGui.GetForegroundDrawList();
        ImFontPtr font = ImGui.GetFont();

        foreach (FloatingCombatText item in _floatingCombatText)
        {
            float t = item.Age / WorldTextLifetime;
            float alpha = item.Age < 0.15f
                ? item.Age / 0.15f
                : item.Age <= 0.76f ? 1f : 1f - (item.Age - 0.76f) / (WorldTextLifetime - 0.76f);
            alpha = Math.Clamp(alpha, 0f, 1f);

            Vector3 point = item.Anchor + new Vector3(0, 0, item.Age * 1.2f);
            if (!_window.Camera.TryWorldToScreen(point, display, out Vector2 screen)) continue;

            float size = diagonal * 0.018333f;
            if (item.Critical)
            {
                float settle = Math.Clamp(item.Age / 0.30f, 0f, 1f);
                size *= 2f + (1.5f - 2f) * settle;
            }

            Vector4 baseColor = item.Style switch
            {
                WorldCombatTextStyle.PlayerSpell => new Vector4(1f, 0.87f, 0f, alpha),
                WorldCombatTextStyle.Experience => new Vector4(0.65f, 0.25f, 1f, alpha),
                _ => new Vector4(1f, 1f, 1f, alpha),
            };
            Vector2 extent = ImGui.CalcTextSize(item.Text);
            float scaledWidth = extent.X * size / MathF.Max(ImGui.GetFontSize(), 1f);
            Vector2 pos = new(screen.X - scaledWidth * 0.5f, screen.Y - size);
            float laneDirection = item.Lane switch { 0 => -0.30f, 1 => 0.30f, 2 => -0.65f, _ => 0.65f };
            pos.X += laneDirection * size * (0.35f + t);
            Vector2 shadowOffset = new(display.X * 0.002f, display.Y * 0.002f);
            uint shadow = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, alpha * 0.55f));
            uint color = ImGui.ColorConvertFloat4ToU32(baseColor);
            draw.AddText(font, size, pos + shadowOffset, shadow, item.Text);
            draw.AddText(font, size, pos, color, item.Text);
        }
    }

    private void DrawCenterCombatText()
    {
        if (SettingsModalOpen) return;
        Vector2 display = ImGui.GetIO().DisplaySize;
        ImDrawListPtr draw = ImGui.GetForegroundDrawList();
        ImFontPtr font = ImGui.GetFont();
        float uiScale = display.Y / 768f;

        foreach (CenterText item in _centerCombatText)
        {
            float alpha = item.Age <= 1.3f ? 1f : 1f - (item.Age - 1.3f) / 0.6f;
            alpha = Math.Clamp(alpha, 0f, 1f);
            float size = 25f * uiScale;
            if (item.Critical)
            {
                float grow = Math.Clamp(item.Age / 0.05f, 0f, 1f);
                float shrink = Math.Clamp((item.Age - 0.05f) / 0.15f, 0f, 1f);
                size = (30f + 30f * grow - 30f * shrink) * uiScale;
            }

            float laneX = (item.Lane - 2) * 18f * uiScale;
            float rise = item.Critical ? 0f : item.Age / 1.9f * 225f * uiScale;
            Vector2 extent = ImGui.CalcTextSize(item.Text);
            float width = extent.X * size / MathF.Max(ImGui.GetFontSize(), 1f);
            Vector2 pos = new(display.X * 0.5f + laneX - width * 0.5f,
                              display.Y * 0.5f + 110f * uiScale - rise);
            Vector4 baseColor = item.Style switch
            {
                CenterCombatTextStyle.Heal => new Vector4(0.10f, 1f, 0.10f, alpha),
                CenterCombatTextStyle.Power => new Vector4(0.35f, 0.45f, 1f, alpha),
                _ => new Vector4(1f, 0.12f, 0.08f, alpha),
            };
            uint shadow = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, alpha * 0.6f));
            draw.AddText(font, size, pos + new Vector2(2, 2) * uiScale, shadow, item.Text);
            draw.AddText(font, size, pos, ImGui.ColorConvertFloat4ToU32(baseColor), item.Text);
        }
    }
}
