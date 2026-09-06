using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private void DrawBattlefieldFrame()
    {
        if (_battlefieldList is not { } list || _gameplayArt is null) return;
        if (!BattlefieldListContextCurrent()) { CloseBattlefieldList(); return; }
        float s = GameplayUiScale(); Vector2 p = UiPanelFrameOrigin(UiPanelOwnershipRegistry[22], s);
        ImGui.SetNextWindowPos(p, ImGuiCond.Always); ImGui.SetNextWindowSize(new Vector2(384,512) * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##battlefield-frame", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav)) { ImGui.End(); return; }
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        DrawArt(draw, @"Interface\BattlefieldFrame\UI-Battlefield-Icon", p + new Vector2(7,6) * s, new(60,60), s);
        DrawArt(draw, @"Interface\BattlefieldFrame\UI-Battlefield-TopLeft", p, new(256,256), s);
        DrawArt(draw, @"Interface\BattlefieldFrame\UI-Battlefield-TopRight", p + new Vector2(256,0) * s, new(128,256), s);
        DrawArt(draw, @"Interface\BattlefieldFrame\UI-Battlefield-BotLeft", p + new Vector2(0,256) * s, new(256,256), s);
        DrawArt(draw, @"Interface\BattlefieldFrame\UI-Battlefield-BotRight", p + new Vector2(256,256) * s, new(128,256), s);
        GameText.DrawCentered(draw, "GameFontNormal", BattlefieldName(list.Map), p + new Vector2(192,17) * s, s);
        GameText.Draw(draw, "GameFontHighlight", "Battleground", p + new Vector2(75,55) * s, s);
        int total = list.Instances.Count + 1;
        Vector2 rowsMin = p + new Vector2(23,79) * s, rowsMax = rowsMin + new Vector2(293,192) * s;
        if (ImGui.IsMouseHoveringRect(rowsMin, rowsMax) && ImGui.GetIO().MouseWheel != 0)
            _battlefieldScroll = Math.Clamp(_battlefieldScroll - (int)ImGui.GetIO().MouseWheel, 0, Math.Max(0,total - 12));
        for (int row = 0; row < 12 && row + _battlefieldScroll < total; row++)
        {
            int index = row + _battlefieldScroll; uint instance = index == 0 ? 0 : list.Instances[index-1];
            Vector2 min = rowsMin + new Vector2(0,row * 16) * s;
            ImGui.SetCursorScreenPos(min);
            if (ImGui.InvisibleButton($"##battlefield-instance-{index}",new Vector2(293,16) * s)) _battlefieldSelected = index;
            if (_battlefieldSelected == index || ImGui.IsItemHovered())
                DrawArt(draw, @"Interface\Buttons\UI-Listbox-Highlight2", min, new(293,16), s);
            GameText.Draw(draw, "GameFontNormalSmall", index == 0 ? InventoryGlobalString("FIRST_AVAILABLE", "First Available") : $"{BattlefieldName(list.Map)} {instance}", min, s);
        }
        // Scroll buttons retain access when the server advertises more than twelve instances.
        if (VanillaButton(draw,"##battlefield-up","Up",p + new Vector2(317,79) * s,new(32,20),s,enabled:_battlefieldScroll > 0)) _battlefieldScroll--;
        if (VanillaButton(draw,"##battlefield-down","Down",p + new Vector2(310,250) * s,new(40,20),s,enabled:_battlefieldScroll + 12 < total)) _battlefieldScroll++;
        bool close = VanillaButton(draw,"##battlefield-cancel","Cancel",p + new Vector2(263.5f,412) * s,new(83,22),s);
        if (VanillaButton(draw,"##battlefield-join","Join Battle",p + new Vector2(152.5f,412) * s,new(109,22),s)) JoinSelectedBattlefield(false);
        if (list.Map != 30 && VanillaButton(draw,"##battlefield-group","Join as Group",p + new Vector2(16.5f,412) * s,new(136,22),s,
            enabled:_partyMembers.Count > 0 && _partyLeaderGuid == ControlledGuid)) JoinSelectedBattlefield(true);
        DrawImageButton(draw,"##battlefield-close",p + new Vector2(323,8) * s,new Vector2(32,32) * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up",@"Interface\Buttons\UI-Panel-MinimizeButton-Down",@"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        close |= ImGui.IsItemClicked(); ImGui.End();
        if (close) CloseBattlefieldList();
    }

    private void DrawBattlefieldQueueControl(Vector2 mapMin, Vector2 mapMax, float s)
    {
        if (!CanAuthorBattlefield || !Enumerable.Range(0,3).Any(slot => _battlefieldQueues[slot] is not null)) return;
        Vector2 p = new(mapMin.X + 13 * s, mapMax.Y - 20 * s);
        ImGui.SetNextWindowPos(p,ImGuiCond.Always); ImGui.SetNextWindowSize(new Vector2(33) * s,ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        bool begun = ImGui.Begin("##battlefield-minimap",ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav);
        if (begun)
        {
            var draw = ImGui.GetWindowDrawList();
            DrawImageButton(draw,"##battlefield-status-button",p,new Vector2(33) * s,
                @"Interface\BattlefieldFrame\UI-Battlefield-Icon",@"Interface\BattlefieldFrame\UI-Battlefield-Icon",@"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
            if (ImGui.IsItemClicked() || ImGui.IsItemClicked(ImGuiMouseButton.Right)) _battlefieldQueueMenu = !_battlefieldQueueMenu;
        }
        ImGui.End();
        if (!_battlefieldQueueMenu || _skin is null) return;
        int count = Enumerable.Range(0,3).Count(slot => _battlefieldQueues[slot] is not null);
        Vector2 size = new Vector2(310,42 + count * 82) * s;
        Vector2 origin = Vector2.Clamp(p + new Vector2(-280,35) * s,Vector2.Zero,Vector2.Max(Vector2.Zero,ImGui.GetIO().DisplaySize-size));
        ImGui.SetNextWindowPos(origin,ImGuiCond.Always); ImGui.SetNextWindowSize(size,ImGuiCond.Always); ImGui.SetNextWindowBgAlpha(0);
        begun = ImGui.Begin("##battlefield-queues",ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav);
        if (begun)
        {
            var draw = ImGui.GetWindowDrawList(); _skin.DrawBackdrop(draw,origin,origin+size,WowSkin.Dialog);
            int row = 0;
            for (int slot = 0; slot < 3; slot++)
            {
                if (_battlefieldQueues[slot] is not { } entry) continue;
                Vector2 top = origin + new Vector2(12,12 + row++ * 82) * s;
                GameText.Draw(draw,"GameFontNormal",BattlefieldName(entry.Packet.Map),top,s);
                string status = entry.Packet.Status switch
                {
                    BattlefieldStatus.Queued => $"Queued: {TimeSpan.FromSeconds(Math.Floor(entry.ElapsedMilliseconds(NowSeconds()) / 1000)):g} (estimated {(entry.Packet.Time1 == 0 ? "unavailable" : TimeSpan.FromSeconds(Math.Floor(entry.Packet.Time1 / 1000d)).ToString("g"))})",
                    BattlefieldStatus.Invited => $"Invitation: {Math.Ceiling(entry.RemainingMilliseconds(NowSeconds()) / 1000):0} seconds remaining",
                    _ => "In battleground",
                };
                GameText.Draw(draw,"GameFontHighlightSmall",status,top + new Vector2(0,20) * s,s);
                if (entry.Packet.Status == BattlefieldStatus.Active && VanillaButton(draw,$"##battlefield-scores-{slot}","Show Scores",top + new Vector2(0,42)*s,new(120,22),s)) RequestBattlefieldScores();
                if (entry.Packet.Status is BattlefieldStatus.Queued or BattlefieldStatus.Invited)
                {
                    if (VanillaButton(draw,$"##battlefield-leave-{slot}","Leave Queue",top + new Vector2(0,42) * s,new(120,22),s)) SubmitBattlefieldPort(slot,false);
                    if (entry.Packet.Status == BattlefieldStatus.Invited && VanillaButton(draw,$"##battlefield-enter-{slot}","Enter Battle",top + new Vector2(138,42) * s,new(120,22),s,enabled:entry.CanEnter(NowSeconds()))) SubmitBattlefieldPort(slot,true);
                }
            }
            if (VanillaButton(draw,"##battlefield-queues-close","Close",origin + new Vector2(115,16 + count * 82) * s,new(80,22),s)) _battlefieldQueueMenu = false;
        }
        ImGui.End();
    }
}
