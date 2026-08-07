using System.Globalization;
using System.Numerics;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed record UiParityDrawTrace(string TexturePath, uint Color, string DrawLayer,
        string Point, string RelativeTo, string RelativePoint, float OffsetX, float OffsetY,
        string FontPath = "", float FontSize = 0);
    private sealed record UiParityActualRow(string[] Values);
    private readonly List<UiParityActualRow> _uiParityRows = [];
    private bool _uiParityArmed;
    private bool _uiParityFrameSeen;
    private int _uiParityPresentedFrames;
    private string _uiParityPanel = "";
    private string _uiParityStamp = "";
    private Vector2 _uiParityOrigin;
    private float _uiParityLogicalScale = 1f;

    private void ArmUiParityCapture(string panel)
    {
        // Dev-only capture affordance: "character-frame:N" opens the character frame at tab N
        // (0 Character, 2 Reputation, 3 Skills, 4 Honor) so each sub-page can be captured.
        int characterTab = 0;
        if (panel.StartsWith("character-frame:", StringComparison.Ordinal))
        {
            int.TryParse(panel["character-frame:".Length..], out characterTab);
            panel = "character-frame";
        }
        if (!_config.DevTools || panel is not ("game-menu" or "options" or "keybindings" or "macro" or "tooltip" or "ui-errors" or "static-popup" or "player-frame" or "target-frame" or "party-frame" or "party-invite" or
            "action-bar" or "action-button" or "multi-action-bar" or "pet-action-bar" or "cast-bar" or "buff-frame" or "minimap" or "chat-frame" or "reputation-bar" or "backpack" or "character-frame" or "spellbook" or "talent-frame" or "quest-log" or "quest-frame" or "merchant" or "trainer" or "bank" or "mail" or "auction" or "loot" or "guild" or "gossip" or "taxi" or "trade")) return;
        _uiParityPanel = panel;
        // Captures isolate the requested gameplay panel from persistent wire-opened utility
        // windows; this changes capture presentation only, never the panel draw path.
        _hearthOpen = false;
        _uiParityStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        _uiParityRows.Clear(); _uiParityArmed = true; _uiParityFrameSeen = false; _uiParityPresentedFrames = 0;
        if (panel == "game-menu") OpenSettings();
        if (panel == "options") { OpenSettings(); _menuPage=MenuPage.Video; }
        if (panel == "backpack") _backpackOpen = true;
        if (panel == "character-frame") { _characterOpen = true; _characterTab = characterTab; _paperDollDirty = true; }
        if (panel == "spellbook") { _spellbookOpen = true; _characterOpen = false; }
        if (panel == "talent-frame") _talentOpen = true;
        if (panel == "quest-log") _questLogOpen = true;
        if (panel == "trade") _tradeOpen = true;
        if (panel == "keybindings") _keybindingsOpen = true;
        if (panel == "macro") _macroOpen = true;
        if (panel == "tooltip") _tooltipParityOpen = true;
        if (panel == "ui-errors") _uiErrorsParityOpen = true;
        if (panel == "static-popup") _staticPopupParityOpen = true;
        if (panel == "pet-action-bar") StagePetActionBarProof();
        if (panel == "cast-bar")
        {
            _castBarPhase = CastBarPhase.Casting;
            _castBarText = "Fireball";
            _castBarStarted = NowSeconds() - 2;
            _castBarEnds = NowSeconds() + 2;
        }
    }

    private void BeginUiParityFrame(Vector2 origin, float logicalScale = 0f)
    {
        if (!_uiParityArmed || _uiParityFrameSeen || _uiParityPanel == "game-menu" && _menuPage != MenuPage.GameMenu) return;
        _uiParityOrigin = origin;
        _uiParityLogicalScale = logicalScale > 0 ? logicalScale : S;
        _uiParityRows.Clear();
    }

    private void CollectUiParity(string element, string type, Vector2 min, Vector2 size,
        string parent = "GameMenuFrame", string point = "", string relativeTo = "",
        string relativePoint = "", string offsetX = "", string offsetY = "", string texture = "",
        string font = "", string fontPath = "", string fontSize = "", string color = "",
        string layer = "", string strata = "DIALOG", string bgFile = "", string edgeFile = "",
        string tileSize = "", string edgeSize = "", string insets = "", string texCoords = "")
    {
        if (!_uiParityArmed || _uiParityFrameSeen) return;
        static string N(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        static string Norm(string path) => path.Length == 0 || path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) ? path : path + ".blp";
        texture = Norm(texture); bgFile = Norm(bgFile); edgeFile = Norm(edgeFile);
        string assets = string.Join('|', new[] { texture, bgFile, edgeFile }.Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(path =>
                _mpq?.ReadFileWithSupplier(path) is { } hit ? $"{hit.Supplier}:{path}" : $"MISSING:{path}"));
        string fontSource = fontPath.Length > 0 && _mpq?.ReadFileWithSupplier(fontPath) is { } fontHit
            ? $"{fontHit.Supplier}:{fontPath}" : "";
        float logicalScale = MathF.Max(_uiParityLogicalScale, 0.001f);
        Vector2 relative = element is "GameMenuFrame" or "PlayerFrame" or "TargetFrame" or "PartyMemberFrame1" or
            "MainMenuBar" or "ActionButton1" or "MultiBarBottomLeft" or "CastingBarFrame" or "BuffFrame" or "MinimapCluster" or "ChatFrame1" or "ReputationWatchBar" or "ContainerFrame1"
            ? Vector2.Zero : (min - _uiParityOrigin) / logicalScale;
        bool unsized = size == Vector2.Zero;
        string[] values = [_uiParityPanel, element, type, parent, unsized ? "" : N(relative.X), unsized ? "" : N(relative.Y),
            unsized ? "" : N(size.X / logicalScale), unsized ? "" : N(size.Y / logicalScale), point, relativeTo, relativePoint, offsetX, offsetY,
            texture, font, fontPath, fontSize, color, layer, strata, bgFile, edgeFile, tileSize,
            edgeSize, insets, texCoords, "MSUI:actual-draw-path", assets, fontSource];
        // Legacy declarations contain call-site copies of reference metadata. Preserve their
        // measured geometry, but never accept those declarations as evidence.
        _uiParityRows.Add(new([.. values.Take(8), "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "",
            "MSUI:legacy-geometry-only", "", "", "DRAWN-NOT-INSTRUMENTED"]));
    }

    private void CollectUiParityDraw(string element, string type, Vector2 min, Vector2 size,
        string parent, UiParityDrawTrace trace)
    {
        if (!_uiParityArmed || _uiParityFrameSeen) return;
        static string N(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        static string Norm(string path) => path.Length == 0 || path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) ? path : path + ".blp";
        float logicalScale = MathF.Max(_uiParityLogicalScale, 0.001f);
        Vector2 relative = element is "GameMenuFrame" or "OptionsFrame" or "KeyBindingFrame" or "MacroFrame" or "GameTooltip" or "UIErrorsFrame" or "StaticPopup1" or "PlayerFrame" or "TargetFrame" or "CharacterFrame" or "PaperDollFrame" or "SpellBookFrame" or "TalentFrame" or "QuestLogFrame" or "MerchantFrame" or "ClassTrainerFrame" or "BankFrame" or "MailFrame" or "AuctionFrame" or "LootFrame" or "GuildFrame" or "GossipFrame" or "TaxiFrame" or "TradeFrame" ? Vector2.Zero : (min - _uiParityOrigin) / logicalScale;
        string texture = Norm(trace.TexturePath);
        string asset = texture.Length == 0 ? "" : _mpq?.ReadFileWithSupplier(texture) is { } hit
            ? $"{hit.Supplier}:{texture}" : $"MISSING:{texture}";
        string fontSource = trace.FontPath.Length == 0 ? "" : _mpq?.ReadFileWithSupplier(trace.FontPath) is { } fontHit
            ? $"{fontHit.Supplier}:{trace.FontPath}" : $"MISSING:{trace.FontPath}";
        uint c = trace.Color;
        string color = c == 0 ? "" : $"#{c & 0xff:X2}{(c >> 8) & 0xff:X2}{(c >> 16) & 0xff:X2}{(c >> 24) & 0xff:X2}";
        string[] values = [_uiParityPanel, element, type, parent, N(relative.X), N(relative.Y), N(size.X/logicalScale), N(size.Y/logicalScale),
            trace.Point, trace.RelativeTo, trace.RelativePoint, N(trace.OffsetX), N(trace.OffsetY), texture, "", trace.FontPath,
            trace.FontSize > 0 ? N(trace.FontSize) : "", color, trace.DrawLayer, "IMGUI_WINDOW", "", "", "", "", "", "",
            "MSUI:derived-from-draw-variables", asset, fontSource, "DRAWN-INSTRUMENTED"];
        _uiParityRows.Add(new(values));
    }

    private void ClassifyUiParity(string element, string type, string parent, string coverage)
    {
        if (!_uiParityArmed || _uiParityFrameSeen || coverage is not ("DRAWN-NOT-INSTRUMENTED" or "NOT-DRAWN")) return;
        string[] values = [_uiParityPanel, element, type, parent, "", "", "", "", "", "", "", "", "",
            "", "", "", "", "", "", "", "", "", "", "", "", "", "MSUI:panel-draw-walk", "", "", coverage];
        _uiParityRows.Add(new(values));
    }

    private void MarkUiParityFrameComplete()
    {
        if (_uiParityArmed && !_uiParityFrameSeen && _uiParityRows.Count > 0) _uiParityFrameSeen = true;
    }

    private void FinishUiParityCapture()
    {
        if (!_uiParityArmed || !_uiParityFrameSeen || _liveRunOptions is null) return;
        // The modal is opened during the same ImGui frame that records the draw rows.
        // Read back only after two presented frames so the screenshot contains that modal,
        // rather than the framebuffer that preceded OpenPopup.
        if (++_uiParityPresentedFrames < 2) return;
        string dir = Path.GetFullPath(Path.IsPathRooted(_liveRunOptions.OutputDirectory)
            ? _liveRunOptions.OutputDirectory : Path.Combine(_config.RepoRoot, _liveRunOptions.OutputDirectory));
        Directory.CreateDirectory(dir);
        string stem = $"ui-parity-{_uiParityPanel}-{_uiParityStamp}";
        string csv = Path.Combine(dir, stem + "-actual.csv"), png = Path.Combine(dir, stem + "-actual.png");
        const string header = "panel,element,type,parent,x,y,width,height,point,relativeTo,relativePoint,offsetX,offsetY,texture,font,fontPath,fontSize,color,layer,strata,bgFile,edgeFile,tileSize,edgeSize,insets,texCoords,source,assetSource,fontSource,coverage";
        static string Csv(IEnumerable<string> values) => string.Join(',', values.Select(v => '"' + v.Replace("\"", "\"\"") + '"'));
        File.WriteAllLines(csv, new[] { header }.Concat(_uiParityRows.Select(r => Csv(r.Values))));
        TrySaveGameplayScreenshot(png);
        Console.WriteLine($"[ui-parity] actual draw capture {csv} (+ .png)");
        _uiParityArmed = false; _uiParityFrameSeen = false; _uiParityPresentedFrames = 0; _uiParityRows.Clear();
    }
}
