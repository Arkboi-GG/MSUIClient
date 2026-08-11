using System.Text.Json;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly record struct PainterlyCaptureState(
        bool Enabled, float BandStrength, float Ink, float Silhouette,
        int CanvasHeight);

    private static readonly string[] PainterlyComparisonLabels =
        ["raw", "current", "flattening-zero", "generated-edges-zero", "native-canvas"];

    private bool _painterlyComparisonRequested;
    private bool _painterlyComparisonKeyDown;
    private int _painterlyComparisonIndex = -1;
    private string? _painterlyComparisonDirectory;
    private PainterlyCaptureState _painterlyComparisonRestore;

    /// <summary>Comparison frames contain only the scene, never tuning windows or HUD.</summary>
    private bool PainterlyComparisonHidesUi => _painterlyComparisonIndex >= 0;

    private void ArmPainterlyComparison()
    {
        if (!_config.DevTools || _painterly is null || _terrain is null || _worldLoading) return;
        if (_painterlyComparisonIndex >= 0 || _painterlyComparisonRequested) return;
        _painterlyComparisonRequested = true;
        Console.WriteLine("[painterly-capture] armed: raw/current/flattening/edges/canvas comparison");
    }

    /// <summary>
    /// Called before any world draw. Deferring the transition here matters for
    /// the dev-panel button: that button is clicked after the world has already
    /// rendered, and changing the pass there would mislabel the first image.
    /// </summary>
    private void BeginPainterlyComparisonFrame()
    {
        if (!_painterlyComparisonRequested || _painterly is null) return;
        _painterlyComparisonRequested = false;

        _painterlyComparisonRestore = new PainterlyCaptureState(
            _painterly.Enabled, _painterly.BandStrength, _painterly.Ink,
            _painterly.Silhouette, _painterly.CanvasHeight);

        string view = SafeCaptureName(_currentVantage ?? "unsaved-view");
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        _painterlyComparisonDirectory = Path.Combine(
            _config.RepoRoot, "dumps", $"painterly-{view}-{stamp}");
        Directory.CreateDirectory(_painterlyComparisonDirectory);

        _painterlyComparisonIndex = 0;
        ApplyPainterlyComparisonVariant();
    }

    private void ApplyPainterlyComparisonVariant()
    {
        if (_painterly is null || _painterlyComparisonIndex < 0) return;

        _painterly.Enabled = _painterlyComparisonIndex switch
        {
            0 => false,
            1 => _painterlyComparisonRestore.Enabled,
            _ => true,
        };
        _painterly.BandStrength = _painterlyComparisonIndex == 2
            ? 0f : _painterlyComparisonRestore.BandStrength;
        _painterly.Ink = _painterlyComparisonIndex == 3
            ? 0f : _painterlyComparisonRestore.Ink;
        _painterly.Silhouette = _painterlyComparisonIndex == 3
            ? 0f : _painterlyComparisonRestore.Silhouette;
        _painterly.CanvasHeight = _painterlyComparisonIndex == 4
            ? 0 : _painterlyComparisonRestore.CanvasHeight;
    }

    /// <summary>Called after the final overlay flush, while the completed framebuffer is readable.</summary>
    private void FinishPainterlyComparisonCapture()
    {
        if (_painterlyComparisonIndex < 0 || _painterly is null ||
            _painterlyComparisonDirectory is null) return;

        string label = PainterlyComparisonLabels[_painterlyComparisonIndex];
        string pngPath = Path.Combine(_painterlyComparisonDirectory, label + ".png");

        try
        {
            if (!TrySaveGameplayScreenshot(pngPath))
                throw new InvalidOperationException("framebuffer unavailable");
            Console.WriteLine($"[painterly-capture] {label}.png");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[painterly-capture] failed - {ex.Message}");
            RestorePainterlyComparison();
            return;
        }

        _painterlyComparisonIndex++;
        if (_painterlyComparisonIndex < PainterlyComparisonLabels.Length)
        {
            ApplyPainterlyComparisonVariant();
            return;
        }

        object manifest = new
        {
            takenLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            vantage = _currentVantage,
            images = PainterlyComparisonLabels.Select(label => label + ".png").ToArray(),
            comparison = new
            {
                raw = "Painterly pass disabled",
                current = "Full current profile",
                flatteningZero = "Current profile with value flattening removed",
                generatedEdgesZero = "Current profile with colour and depth ink removed",
                nativeCanvas = "Current profile at native world resolution",
            },
            profile = new
            {
                bands = _painterly.Bands,
                bandStrength = _painterly.BandStrength,
                detail = _painterly.Detail,
                ink = _painterlyComparisonRestore.Ink,
                inkThreshold = _painterly.InkThreshold,
                silhouette = _painterlyComparisonRestore.Silhouette,
                distanceCalm = _painterly.DepthFade,
                calmStart = _painterly.CalmStart,
                calmEnd = _painterly.CalmEnd,
                saturation = _painterly.Saturation,
                contrast = _painterly.Contrast,
                lift = _painterly.Lift,
                warmth = _painterly.Warmth,
                grain = _painterly.Grain,
                dither = _painterly.Dither,
                canvasHeight = _painterlyComparisonRestore.CanvasHeight,
            },
        };
        File.WriteAllText(Path.Combine(_painterlyComparisonDirectory, "manifest.json"),
            JsonSerializer.Serialize(manifest, DumpJson));

        string relative = Path.GetRelativePath(_config.RepoRoot, _painterlyComparisonDirectory)
            .Replace('\\', '/');
        RestorePainterlyComparison();
        Console.WriteLine($"[painterly-capture] wrote {relative}");
    }

    private void RestorePainterlyComparison()
    {
        if (_painterly is not null)
        {
            _painterly.Enabled = _painterlyComparisonRestore.Enabled;
            _painterly.BandStrength = _painterlyComparisonRestore.BandStrength;
            _painterly.Ink = _painterlyComparisonRestore.Ink;
            _painterly.Silhouette = _painterlyComparisonRestore.Silhouette;
            _painterly.CanvasHeight = _painterlyComparisonRestore.CanvasHeight;
        }
        _painterlyComparisonIndex = -1;
        _painterlyComparisonDirectory = null;
        InvalidatePainterlyArt();
    }
}
