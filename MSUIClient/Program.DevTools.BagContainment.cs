using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using MSUIClient.Engine;
using Silk.NET.OpenGL;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private enum BagContainmentPhase { None, Visible, Hidden }
    private readonly record struct BagContainmentPixelGeometry(int Left, int Top, int Right, int Bottom,
        float ApertureLeft, float ApertureTop, float ApertureRight, float ApertureBottom);

    private BagContainmentPhase _bagContainmentPhase;
    private string _bagContainmentElement = "";
    private string _bagContainmentStamp = "";
    private string _bagContainmentError = "";
    private string _bagContainmentCompletedElement = "";
    private string _bagContainmentCompletedManifest = "";
    private Vector4 _bagContainmentCrop;
    private Vector4 _bagContainmentAperture;
    private bool _bagContainmentObserved;
    private BagContainmentPixelGeometry _bagContainmentVisibleGeometry;

    private bool ArmBagContainmentCapture(string element)
    {
        bool allowed = Enumerable.Range(0, 4).Any(i =>
                           element.Equals($"CharacterBag{i}SlotIconTexture", StringComparison.Ordinal)) ||
                       Enumerable.Range(1, 4).Any(i =>
                           element.Equals($"ContainerFrameBag{i}Portrait", StringComparison.Ordinal));
        if (!allowed || !_config.DevTools || _liveRunOptions is null) return false;
        _bagContainmentElement = element;
        _bagContainmentStamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        _bagContainmentError = "";
        _bagContainmentCompletedElement = "";
        _bagContainmentCompletedManifest = "";
        _bagContainmentObserved = false;
        _bagContainmentPhase = BagContainmentPhase.Visible;
        return true;
    }

    /// <summary>
    /// Called at the exact dynamic-icon draw. The hidden variant skips only that AddImage; its
    /// enclosing frame, ring, hit target, layout, and every other draw command remain unchanged.
    /// </summary>
    private bool BagContainmentDrawIcon(string element, Vector2 iconMin, Vector2 iconSize,
        Vector2 cropMin, Vector2 cropSize)
    {
        if (_bagContainmentPhase == BagContainmentPhase.None ||
            !_bagContainmentElement.Equals(element, StringComparison.Ordinal)) return true;

        Vector4 crop = new(cropMin.X, cropMin.Y, cropMin.X + cropSize.X, cropMin.Y + cropSize.Y);
        Vector4 aperture = new(iconMin.X, iconMin.Y, iconMin.X + iconSize.X, iconMin.Y + iconSize.Y);
        if (_bagContainmentPhase == BagContainmentPhase.Visible)
        {
            _bagContainmentCrop = crop;
            _bagContainmentAperture = aperture;
        }
        else if (Vector4.Distance(_bagContainmentCrop, crop) > .01f ||
                 Vector4.Distance(_bagContainmentAperture, aperture) > .01f)
        {
            _bagContainmentError = $"geometry changed between variants;visibleCrop={_bagContainmentCrop};hiddenCrop={crop};" +
                                   $"visibleAperture={_bagContainmentAperture};hiddenAperture={aperture}";
        }
        _bagContainmentObserved = true;
        return _bagContainmentPhase != BagContainmentPhase.Hidden;
    }

    private void FinishBagContainmentCapture()
    {
        if (_bagContainmentPhase == BagContainmentPhase.None || !_bagContainmentObserved) return;
        if (_bagContainmentError.Length > 0)
        {
            FailBagContainmentCapture(_bagContainmentError);
            return;
        }
        string dir = Path.GetFullPath(Path.IsPathRooted(_liveRunOptions!.OutputDirectory)
            ? _liveRunOptions.OutputDirectory
            : Path.Combine(_config.RepoRoot, _liveRunOptions.OutputDirectory));
        Directory.CreateDirectory(dir);
        string safeElement = string.Concat(_bagContainmentElement.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        string stem = $"ui-parity-containment-{safeElement}-{_bagContainmentStamp}";
        if (_bagContainmentPhase == BagContainmentPhase.Visible)
        {
            string visible = Path.Combine(dir, stem + "-visible.png");
            if (!TrySaveBagContainmentCrop(visible, out _bagContainmentVisibleGeometry))
            {
                FailBagContainmentCapture($"visible crop write failed: {visible}");
                return;
            }
            _bagContainmentPhase = BagContainmentPhase.Hidden;
            _bagContainmentObserved = false;
            return;
        }

        string hidden = Path.Combine(dir, stem + "-hidden.png");
        if (!TrySaveBagContainmentCrop(hidden, out BagContainmentPixelGeometry hiddenGeometry))
        {
            FailBagContainmentCapture($"hidden crop write failed: {hidden}");
            return;
        }
        if (hiddenGeometry != _bagContainmentVisibleGeometry)
        {
            FailBagContainmentCapture($"pixel geometry changed between variants;visible={_bagContainmentVisibleGeometry};hidden={hiddenGeometry}");
            return;
        }
        string visiblePath = Path.Combine(dir, stem + "-visible.png");
        string visibleSha = Sha256(visiblePath), hiddenSha = Sha256(hidden);
        if (visibleSha == hiddenSha)
        {
            FailBagContainmentCapture("visible and hidden crops are identical; target icon was absent or not drawn");
            return;
        }
        string manifest = Path.Combine(dir, stem + "-manifest.json");
        string reportName = stem + "-report.json";
        BagContainmentPixelGeometry g = hiddenGeometry;
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            kind = "ui-icon-containment-pair",
            element = _bagContainmentElement,
            variants = new
            {
                visible = new { path = Path.GetFileName(visiblePath), sha256 = visibleSha },
                hidden = new { path = Path.GetFileName(hidden), sha256 = hiddenSha },
            },
            invariant = new
            {
                identicalScreenGeometry = true,
                adjacentPresentedFrames = true,
                hiddenVariantChange = "dynamic-icon AddImage suppressed; chrome/layout/hit target unchanged",
                cropPixels = new { width = g.Right - g.Left, height = g.Bottom - g.Top },
            },
            containment = new
            {
                shape = "ellipse",
                left = g.ApertureLeft,
                top = g.ApertureTop,
                right = g.ApertureRight,
                bottom = g.ApertureBottom,
                threshold = 0,
            },
            command = $"ui-parity containment --visible {Path.GetFileName(visiblePath)} --hidden {Path.GetFileName(hidden)} " +
                      $"--out {reportName} --left {N(g.ApertureLeft)} --top {N(g.ApertureTop)} --right {N(g.ApertureRight)} " +
                      $"--bottom {N(g.ApertureBottom)} --shape ellipse --threshold 0",
        }, new JsonSerializerOptions { WriteIndented = true }));
        _bagContainmentCompletedElement = _bagContainmentElement;
        _bagContainmentCompletedManifest = manifest;
        _bagContainmentPhase = BagContainmentPhase.None;
        _bagContainmentObserved = false;
        Console.WriteLine($"[bag-containment] pair complete element={_bagContainmentElement}; manifest={manifest}");
    }

    private unsafe bool TrySaveBagContainmentCrop(string path, out BagContainmentPixelGeometry geometry)
    {
        geometry = default;
        if (_gl is null) return false;
        Vector2 framebuffer = _window.FramebufferSize;
        Vector2 display = ImGuiNET.ImGui.GetIO().DisplaySize;
        int framebufferWidth = Math.Max(1, (int)framebuffer.X);
        int framebufferHeight = Math.Max(1, (int)framebuffer.Y);
        float scaleX = framebufferWidth / MathF.Max(1f, display.X);
        float scaleY = framebufferHeight / MathF.Max(1f, display.Y);
        int left = Math.Clamp((int)MathF.Floor(_bagContainmentCrop.X * scaleX), 0, framebufferWidth - 1);
        int top = Math.Clamp((int)MathF.Floor(_bagContainmentCrop.Y * scaleY), 0, framebufferHeight - 1);
        int right = Math.Clamp((int)MathF.Ceiling(_bagContainmentCrop.Z * scaleX), left + 1, framebufferWidth);
        int bottom = Math.Clamp((int)MathF.Ceiling(_bagContainmentCrop.W * scaleY), top + 1, framebufferHeight);
        int width = right - left, height = bottom - top;
        byte[] bottomUp = new byte[checked(width * height * 4)];
        fixed (byte* pixels = bottomUp)
            _gl.ReadPixels(left, framebufferHeight - bottom, (uint)width, (uint)height,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        byte[] topDown = new byte[bottomUp.Length];
        int stride = width * 4;
        for (int y = 0; y < height; y++)
            System.Buffer.BlockCopy(bottomUp, y * stride, topDown, (height - 1 - y) * stride, stride);
        PortraitRenderTarget.SaveRgbaPng(path, width, height, topDown);
        geometry = new(left, top, right, bottom,
            _bagContainmentAperture.X * scaleX - left,
            _bagContainmentAperture.Y * scaleY - top,
            _bagContainmentAperture.Z * scaleX - left,
            _bagContainmentAperture.W * scaleY - top);
        return File.Exists(path) && new FileInfo(path).Length > 0;
    }

    private void FailBagContainmentCapture(string error)
    {
        _bagContainmentError = error;
        _bagContainmentPhase = BagContainmentPhase.None;
        _bagContainmentObserved = false;
        Console.Error.WriteLine($"[bag-containment] FAIL {error}");
    }

    private bool BagContainmentCapturePassed(string element) =>
        _bagContainmentCompletedElement.Equals(element, StringComparison.Ordinal) &&
        _bagContainmentError.Length == 0 && _bagContainmentCompletedManifest.Length > 0 &&
        File.Exists(_bagContainmentCompletedManifest) && new FileInfo(_bagContainmentCompletedManifest).Length > 0;

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string N(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
