using System.Globalization;
using System.Numerics;

namespace MSUIClient;

public sealed partial class GameLoop
{
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
        if (!_config.DevTools || panel is not ("game-menu" or "player-frame" or "target-frame" or "party-frame" or
            "action-bar" or "action-button" or "multi-action-bar" or "cast-bar" or "buff-frame" or "minimap" or "chat-frame" or "reputation-bar")) return;
        _uiParityPanel = panel;
        _uiParityStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        _uiParityRows.Clear(); _uiParityArmed = true; _uiParityFrameSeen = false; _uiParityPresentedFrames = 0;
        if (panel == "game-menu") OpenSettings();
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
            "MainMenuBar" or "ActionButton1" or "MultiBarBottomLeft" or "CastingBarFrame" or "BuffFrame" or "MinimapCluster" or "ChatFrame1" or "ReputationWatchBar"
            ? Vector2.Zero : (min - _uiParityOrigin) / logicalScale;
        bool unsized = size == Vector2.Zero;
        string[] values = [_uiParityPanel, element, type, parent, unsized ? "" : N(relative.X), unsized ? "" : N(relative.Y),
            unsized ? "" : N(size.X / logicalScale), unsized ? "" : N(size.Y / logicalScale), point, relativeTo, relativePoint, offsetX, offsetY,
            texture, font, fontPath, fontSize, color, layer, strata, bgFile, edgeFile, tileSize,
            edgeSize, insets, texCoords, "MSUI:actual-draw-path", assets, fontSource];
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
        const string header = "panel,element,type,parent,x,y,width,height,point,relativeTo,relativePoint,offsetX,offsetY,texture,font,fontPath,fontSize,color,layer,strata,bgFile,edgeFile,tileSize,edgeSize,insets,texCoords,source,assetSource,fontSource";
        static string Csv(IEnumerable<string> values) => string.Join(',', values.Select(v => '"' + v.Replace("\"", "\"\"") + '"'));
        File.WriteAllLines(csv, new[] { header }.Concat(_uiParityRows.Select(r => Csv(r.Values))));
        TrySaveGameplayScreenshot(png);
        Console.WriteLine($"[ui-parity] actual draw capture {csv} (+ .png)");
        _uiParityArmed = false; _uiParityFrameSeen = false; _uiParityPresentedFrames = 0; _uiParityRows.Clear();
    }
}
