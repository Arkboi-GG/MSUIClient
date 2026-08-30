using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

/// <summary>
/// Stablemaster window (spec P3). Shows the active pet and the stabled pets, and
/// drives stable / unstable / swap / buy-slot. Opened by the server's pet list
/// (gossip stable option or /stable).
///
/// Rendered the SuperUI way — DrawVanillaPanelChrome + GameText on the draw list +
/// VanillaButton, with row selection via InvisibleButton. No ImGui widgets, so it
/// enrolls in GameplayImguiPolicyLaw's clean list.
/// </summary>
public sealed partial class GameLoop
{
    private const float StableRowHeight = 20f;

    private void DrawStablePanel()
    {
        if (!_stableOpen || _net is null || _gameplayArt is null || _stableList is null) return;
        float scale = GameplayUiScale();

        ImGui.SetNextWindowSize(new Vector2(320f, 360f) * scale, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Vector2(290f, 240f) * scale, new Vector2(float.MaxValue, float.MaxValue));
        ImGui.SetNextWindowBgAlpha(0f);
        if (!ImGui.Begin("###stable", ref _stableOpen,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground |
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }
        DrawVanillaPanelChrome("Stable", scale, ref _stableOpen);

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 wMin = ImGui.GetWindowPos();
        Vector2 wMax = wMin + ImGui.GetWindowSize();
        float edge = 11f * scale;
        float plaqueBottom = wMin.Y + 52f * scale;
        dl.AddRectFilled(
            new Vector2(wMin.X + edge, plaqueBottom + 2f * scale),
            new Vector2(wMax.X - edge, wMax.Y - edge), 0xc00b0e12);

        Vector2 c0 = new(wMin.X + edge + 8f * scale, plaqueBottom + 10f * scale);
        float innerWidth = (wMax.X - edge - 8f * scale) - c0.X;
        float y = DrawStableContent(dl, c0, y: 0f, scale, innerWidth);

        DrawStableFooter(dl, wMin, wMax, edge, scale);
        ImGui.End();
    }

    private float DrawStableContent(ImDrawListPtr dl, Vector2 c0, float y, float scale, float innerWidth)
    {
        StableList list = _stableList!;
        StabledPet? active = list.Active;
        int stabledCount = list.Stabled.Count();
        bool hasFreeSlot = stabledCount < list.StableSlots;

        // --- active pet + Stable button ---
        GameText.Draw(dl, "GameFontNormal", "Current Pet",
            c0 + new Vector2(0, y) * scale, scale, VanillaGold);
        y += 20f;
        if (active is { } cur)
        {
            GameText.Draw(dl, "GameFontNormalSmall", $"{cur.Name}  (Level {cur.Level})",
                c0 + new Vector2(6f, y) * scale, scale, 0xffd8e0e6);
            var stableBtn = new Vector2(78f, 18f);
            if (VanillaButton(dl, "##stable-current", "Stable",
                    c0 + new Vector2(innerWidth - stableBtn.X, y - 2f) * scale,
                    stableBtn, scale, hasFreeSlot))
                StableActivePet();
        }
        else
        {
            GameText.Draw(dl, "GameFontNormalSmall", "No active pet.",
                c0 + new Vector2(6f, y) * scale, scale, 0xff9aa4ab);
        }
        y += 24f;
        dl.AddLine(c0 + new Vector2(0, y) * scale,
            new Vector2(c0.X + innerWidth, c0.Y + y * scale), 0xff2a343d, MathF.Max(1f, scale));
        y += 6f;

        // --- stabled pets ---
        GameText.Draw(dl, "GameFontNormal", "Stabled Pets",
            c0 + new Vector2(0, y) * scale, scale, VanillaGold);
        y += 20f;
        if (stabledCount == 0)
        {
            GameText.Draw(dl, "GameFontNormalSmall", "The stable is empty.",
                c0 + new Vector2(6f, y) * scale, scale, 0xff9aa4ab);
            y += StableRowHeight;
        }
        else
        {
            foreach (StabledPet pet in list.Stabled)
            {
                var rowMin = new Vector2(c0.X, c0.Y + (y - 2f) * scale);
                var rowSize = new Vector2(innerWidth * scale, StableRowHeight * scale);
                ImGui.SetCursorScreenPos(rowMin);
                if (ImGui.InvisibleButton($"##stable-row-{pet.PetNumber}", rowSize))
                    _stableSelected = pet.PetNumber;
                bool hovered = ImGui.IsItemHovered();
                if (_stableSelected == pet.PetNumber)
                    dl.AddRectFilled(rowMin, rowMin + rowSize, 0x33d0b060);
                else if (hovered)
                    dl.AddRectFilled(rowMin, rowMin + rowSize, 0x22ffffff);

                GameText.Draw(dl, "GameFontNormalSmall", $"{pet.Name}  (Level {pet.Level})",
                    c0 + new Vector2(6f, y) * scale, scale, 0xffd8e0e6);
                y += StableRowHeight;
            }
        }

        // --- actions for the selected stabled pet ---
        if (_stableSelected != 0 && list.Stabled.Any(p => p.PetNumber == _stableSelected))
        {
            y += 4f;
            var btn = new Vector2(88f, 18f);
            if (VanillaButton(dl, "##stable-unstable", "Unstable",
                    c0 + new Vector2(0, y) * scale, btn, scale))
                UnstableSelectedPet(_stableSelected);
            if (VanillaButton(dl, "##stable-swap", "Swap with active",
                    c0 + new Vector2(btn.X + 8f, y) * scale, new Vector2(118f, 18f), scale,
                    active is not null))
                SwapSelectedPet(_stableSelected);
            y += 24f;
        }
        return y;
    }

    private void DrawStableFooter(ImDrawListPtr dl, Vector2 wMin, Vector2 wMax, float edge, float scale)
    {
        StableList list = _stableList!;
        int used = list.Stabled.Count();
        var rowY = wMax.Y - edge - 26f * scale;
        GameText.Draw(dl, "GameFontNormalSmall", $"Stable slots: {used}/{list.StableSlots}",
            new Vector2(wMin.X + edge + 8f * scale, rowY + 3f * scale), scale, 0xff9aa4ab);

        var buyBtn = new Vector2(96f, 20f);
        if (VanillaButton(dl, "##stable-buy", "Buy Slot",
                new Vector2(wMax.X - edge - 8f * scale - buyBtn.X * scale, rowY), buyBtn, scale,
                _net is { IsInWorld: true }))
            BuyStableSlot();
    }
}
