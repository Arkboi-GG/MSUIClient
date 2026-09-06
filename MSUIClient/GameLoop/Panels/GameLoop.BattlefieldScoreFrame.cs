using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private void DrawBattlefieldScoreFrame()
    {
        if (!_battlefieldScoreOpen || _gameplayArt is null) return;
        if (CurrentBattlefieldScope() is not { } scope || _battlefieldScoreScope != scope)
        { ResetBattlefieldScores(); return; }
        IReadOnlyList<BattlefieldScoreRow> rows = _battlefieldScore?.Rows ?? Array.Empty<BattlefieldScoreRow>();
        EnsureWorldStateUiCatalog();
        var objectiveColumns = BattlefieldScoreUiLaw.ObjectiveColumns(_worldStateUiCatalog, scope.Map);
        int columns = Math.Max(Math.Max(objectiveColumns.Count, BattlefieldScoreUiLaw.ObjectiveCount(scope.Map)),
            rows.Count == 0 ? 0 : rows.Max(row => row.Objectives.Count));
        string countTemplate = InventoryGlobalString("FLAG_COUNT_TEMPLATE", "x %d");
        float width = BattlefieldScoreUiLaw.Width(columns, rows.Count);
        float s = Math.Min(GameplayUiScale(), Math.Max(.1f, (ImGui.GetIO().DisplaySize.X - 16) / (width + 110)));
        Vector2 p = BattlefieldScoreUiLaw.Origin(ImGui.GetIO().DisplaySize, width, s);
        ImGui.SetNextWindowPos(p, ImGuiCond.Always); ImGui.SetNextWindowSize(new Vector2(width,512) * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##battlefield-scores", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav)) { ImGui.End(); return; }
        var draw = ImGui.GetWindowDrawList();
        const string art = @"Interface\WorldStateFrame\WorldStateFinalScoreFrame-";
        DrawArt(draw, art + "TopBackground", p + new Vector2(8,16) * s, new(width-129,256), s);
        DrawArt(draw, art + "TopLeft", p + new Vector2(8,16) * s, new(128,256), s);
        DrawArt(draw, art + "Top", p + new Vector2(136,16) * s, new(width-392,256), s);
        DrawArt(draw, art + "TopRight", p + new Vector2(width-256,16) * s, new(256,256), s);
        DrawArt(draw, art + "BotLeft", p + new Vector2(8,272) * s, new(128,256), s);
        DrawArt(draw, art + "Bot", p + new Vector2(136,272) * s, new(width-392,256), s);
        DrawArt(draw, art + "BotRight", p + new Vector2(width-256,272) * s, new(256,256), s);
        GameText.DrawCentered(draw,"GameFontNormal", _battlefieldScore is { Ended:true } score
            ? BattlefieldScoreUiLaw.WinnerText(score.Winner) : BattlefieldName(scope.Map),p + new Vector2(width*.5f-57,21)*s,s);
        GameText.Draw(draw,"GameFontHighlightSmall","Name",p + new Vector2(20,48)*s,s);
        void Header(string text,float x)
        {
            string[] lines=WrapTooltipText(text,"GameFontHighlightSmall",s,66*s).ToArray();
            for(int i=0;i<lines.Length;i++) GameText.DrawCentered(draw,"GameFontHighlightSmall",lines[i],p + new Vector2(x,38+i*10)*s,s);
        }
        Header("Killing Blows",202); Header("Deaths",260); Header("Honorable Kills",318);
        string[] names=BattlefieldScoreUiLaw.ObjectiveNames(scope.Map);
        for (int c = 0; c < columns; c++)
        {
            var column = c < objectiveColumns.Count ? objectiveColumns[c] : null;
            float x = 386 + 77 * c;
            Header(column?.Text ?? (c < names.Length ? names[c] : $"Objective {c + 1}"), x);
            if (column is { Tooltip.Length: > 0 } &&
                ImGui.IsMouseHoveringRect(p + new Vector2(x - 30, 33) * s, p + new Vector2(x + 30, 63) * s))
                OfferOwnerAnchoredSharedGameTooltip(new("battlefield-score-column", column.Id),
                    [new(column.Tooltip, GameTooltipTextTone.White)], p + new Vector2(x + 30, 48) * s, Vector2.Zero);
        }
        float bonusX=columns==0?376:444+77*(columns-1);Header("Bonus Honor",bonusX);
        _battlefieldScoreScroll=BattlefieldScoreUiLaw.ClampScroll(_battlefieldScoreScroll,rows.Count);
        if(ImGui.IsMouseHoveringRect(p+new Vector2(17,78)*s,p+new Vector2(width-120,414)*s) && ImGui.GetIO().MouseWheel!=0)
            _battlefieldScoreScroll=BattlefieldScoreUiLaw.ClampScroll(_battlefieldScoreScroll-(int)ImGui.GetIO().MouseWheel*3,rows.Count);
        if(_battlefieldScore is null)
            GameText.DrawCentered(draw,"GameFontNormalSmall",_battlefieldScorePending?"Loading scores...":_battlefieldScoreError,p+new Vector2((width-114)*.5f,150)*s,s);
        for(int i=0;i<BattlefieldScoreUiLaw.VisibleRows && i+_battlefieldScoreScroll<rows.Count;i++)
        {
            BattlefieldScoreRow row=rows[i+_battlefieldScoreScroll];float y=80+i*15;
            string name=_playerNames.GetValueOrDefault(row.Guid, "Loading...");
            if(string.IsNullOrEmpty(name)) name="Unknown player";
            draw.PushClipRect(p+new Vector2(20,y)*s,p+new Vector2(181,y+15)*s,true);
            GameText.Draw(draw,row.Guid==ControlledGuid?"GameFontNormalSmall":"GameFontHighlightSmall",name,p+new Vector2(20,y)*s,s);
            draw.PopClipRect();
            void Value(uint value,float x)=>GameText.DrawCentered(draw,"GameFontHighlightSmall",value.ToString(),p+new Vector2(x,y)*s,s);
            Value(row.KillingBlows,202);Value(row.Deaths,260);Value(row.HonorableKills,318);Value(row.BonusHonor,bonusX);
            var rowTeam = BattlefieldRaceTeam(row.Guid);
            for (int c = 0; c < columns && c < row.Objectives.Count; c++)
            {
                var column = c < objectiveColumns.Count ? objectiveColumns[c] : null;
                var cell = BattlefieldScoreUiLaw.ObjectiveValue(column, row.Objectives[c], rowTeam, countTemplate);
                float x = 386 + 77 * c + (cell.Icon.Length > 0 ? 6 : -1);
                Vector2 textCenter = p + new Vector2(x, y) * s;
                GameText.DrawCentered(draw, "GameFontNormalSmall", cell.Text, textCenter, s);
                if (cell.Icon.Length > 0)
                {
                    float halfText = GameText.MeasureWidth("GameFontNormalSmall", cell.Text, s) * .5f;
                    Vector2 iconMin = textCenter + new Vector2(-halfText - 16 * s,
                        -7 * s);
                    DrawArt(draw, cell.Icon, iconMin, new(16), s);
                }
            }
        }
        if(rows.Count>BattlefieldScoreUiLaw.VisibleRows)
        {
            if(VanillaButton(draw,"##scores-up","Up",p+new Vector2(width-148,78)*s,new(35,20),s,enabled:_battlefieldScoreScroll>0))
                _battlefieldScoreScroll=BattlefieldScoreUiLaw.ClampScroll(_battlefieldScoreScroll-BattlefieldScoreUiLaw.VisibleRows,rows.Count);
            if(VanillaButton(draw,"##scores-down","Down",p+new Vector2(width-150,389)*s,new(40,20),s,enabled:_battlefieldScoreScroll+BattlefieldScoreUiLaw.VisibleRows<rows.Count))
                _battlefieldScoreScroll=BattlefieldScoreUiLaw.ClampScroll(_battlefieldScoreScroll+BattlefieldScoreUiLaw.VisibleRows,rows.Count);
        }
        if(_battlefieldScore is { Ended:true } && ActiveBattlefield() is { } active)
        {
            if(active.Packet.Time1>0)GameText.Draw(draw,"GameFontNormalSmall",$"Time to leave: {Math.Ceiling(active.RemainingMilliseconds(NowSeconds())/1000):0}s",p+new Vector2(40,435)*s,s);
            if(VanillaButton(draw,"##scores-return","Return to World",p+new Vector2(width*.5f-106.5f,415)*s,new(153,21),s,
                enabled:NowSeconds()>=_battlefieldScoreLeaveDeadline))ReturnFromFinishedBattlefield();
        }
        else if(VanillaButton(draw,"##scores-refresh","Refresh",p+new Vector2(width*.5f-75,415)*s,new(90,21),s,enabled:!_battlefieldScorePending))RequestBattlefieldScores();
        if (_battlefieldScore is not { Ended:true })
            GameText.Draw(draw,"GameFontNormalSmall",string.IsNullOrEmpty(_battlefieldScoreError) ? $"{rows.Count} players" : _battlefieldScoreError,p+new Vector2(30,443)*s,s);
        DrawImageButton(draw,"##scores-close",p+new Vector2(width-143,12)*s,new Vector2(32)*s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up",@"Interface\Buttons\UI-Panel-MinimizeButton-Down",@"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if(ImGui.IsItemClicked())_battlefieldScoreOpen=false;
        ImGui.End();
    }
}
