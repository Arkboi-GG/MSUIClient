using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private IReadOnlyList<RealmInfo>? _realmRows;
    private int _realmPage, _realmSelected = -1;

    private void DrawRealmSelection()
    {
        if (_net is null) return;
        IReadOnlyList<RealmInfo> realms = _net.Realms;
        if (!ReferenceEquals(_realmRows, realms))
        {
            _realmRows = realms;
            _realmPage = 0;
            _realmSelected = -1;
        }
        Vector2 display = ImGui.GetIO().DisplaySize;
        float scale = MathF.Max(.1f, display.Y / GlueCanvasH);
        var host = LoginUiLaw.Host(display);
        ImGui.SetNextWindowPos(host.Min, ImGuiCond.Always);
        ImGui.SetNextWindowSize(host.Size, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        bool open = ImGui.Begin("##realm-selection", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings);
        ImGui.PopStyleVar();
        if (!open) { ImGui.End(); return; }
        var draw = ImGui.GetWindowDrawList();
        float saved = _skin?.Scale ?? 1;
        if (_skin is not null) _skin.Scale = scale;
        Vector2 size = new(640 * scale, 420 * scale), origin = (display - size) * .5f;
        if (_skin is not null) _skin.DrawBackdrop(draw, origin, origin + size, WowSkin.Dialog);
        else draw.AddRectFilled(origin, origin + size, 0xEE000000);
        GlueText(draw, "Choose a Realm", display.X * .5f, origin.Y + 18 * scale, 20 * scale, WowSkin.GlueGold, 1);
        string[] headings = ["Realm Name", "Type", "Characters", "Status"];
        float[] columns = [24, 306, 370, 466];
        for (int c = 0; c < columns.Length; c++)
            GlueText(draw, headings[c], origin.X + columns[c] * scale, origin.Y + 56 * scale, 12 * scale, WowSkin.GlueGold, 0);
        int start = _realmPage * RealmSelectUiLaw.PageSize;
        for (int row = 0; row < RealmSelectUiLaw.PageSize && start + row < realms.Count; row++)
        {
            int index = start + row;
            RealmInfo realm = realms[index];
            Vector2 min = origin + new Vector2(20, 82 + row * 25) * scale;
            Vector2 max = min + new Vector2(600, 25) * scale;
            ImGui.SetCursorScreenPos(min);
            bool clicked = ImGui.InvisibleButton($"##realm-{index}", max - min);
            if (clicked && realm.CanSelect) _realmSelected = index;
            if (_realmSelected == index) draw.AddRectFilled(min, max, 0x554080A0);
            Vector4 color = realm.CanSelect ? WowSkin.Normal : WowSkin.Muted;
            string[] values = [realm.Name, RealmSelectUiLaw.TypeName(realm.RealmType), realm.Characters.ToString(), RealmSelectUiLaw.Status(realm)];
            for (int c = 0; c < columns.Length; c++)
            {
                float right = c + 1 < columns.Length ? columns[c + 1] - 8 : 618;
                draw.PushClipRect(origin + new Vector2(columns[c], 82 + row * 25) * scale,
                    origin + new Vector2(right, 107 + row * 25) * scale, true);
                GlueText(draw, values[c], origin.X + columns[c] * scale, min.Y + 4 * scale, 12 * scale, color, 0);
                draw.PopClipRect();
            }
        }
        bool Button(string text, float x, float y, float width, bool enabled = true)
        {
            ImGui.SetCursorScreenPos(origin + new Vector2(x,y) * scale);
            if (!enabled) ImGui.BeginDisabled();
            bool pressed = _skin?.GlueButton(text, new Vector2(width,24) * scale, enabled) ??
                ImGui.Button(text, new Vector2(width,24) * scale);
            if (!enabled) ImGui.EndDisabled();
            return enabled && pressed;
        }
        if (Button("Previous",20,338,100,_realmPage>0)) _realmPage--;
        GlueText(draw, $"{_realmPage + 1} / {RealmSelectUiLaw.LastPage(realms.Count) + 1}", display.X*.5f,
            origin.Y + 342*scale,12*scale,WowSkin.GlueGold,1);
        if (Button("Next",520,338,100,_realmPage<RealmSelectUiLaw.LastPage(realms.Count))) _realmPage++;
        bool valid = _realmSelected >= 0 && _realmSelected < realms.Count && realms[_realmSelected].CanSelect;
        bool accept = Button("Okay",206,378,100,valid);
        bool cancel = Button("Cancel",334,378,100);
        if (cancel || ImGui.IsKeyPressed(ImGuiKey.Escape,false)) _net.Stop();
        else if (valid && (accept || ImGui.IsKeyPressed(ImGuiKey.Enter,false))) _net.SelectRealm(realms[_realmSelected]);
        if (_skin is not null) _skin.Scale = saved;
        ImGui.End();
    }
}
