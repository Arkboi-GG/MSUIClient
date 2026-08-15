using System.Numerics;
using MSUIClient.World;

// Validates the CloudField port against benilla's byte-exact cloud-kernel tests
// (benilla-app/src/clouds/kernel.rs + daynight.rs). Pure kernel - no MPQ, no GL.
// A green run means the noise/threshold/tone-curve/colour-pass math reproduces the
// reference structure the reference tests pin.

int checks = 0, failed = 0;
void Check(bool cond, string msg)
{
    checks++;
    if (!cond) { failed++; Console.WriteLine($"  FAIL: {msg}"); }
}

static CloudField.CloudFrame Frame() => new()
{
    Sun = new Vector3(1.0f, 0.78f, 0.54f),
    Slope = new Vector3(0.17f, 0.41f, 0.52f),
    GBase = new Vector3(0.1f, 0.1f, 0.12f),
    Bcc = 0.0f,
    GlowDir = Vector3.Normalize(new Vector3(0.6f, 0.5f, 0.1f)),
    GlowTrack = 1.0f,
};

const int Cols = CloudField.Cols;
byte Alpha(byte[] rgba, int cell) => rgba[cell * 4 + 3];

// ── 1. Clear sky (C=0 => T=255): coverage empty everywhere; texels all alpha 0. ──
{
    var k = new CloudField();
    k.Rebuild(0.0f, Frame());
    Check(k.Tile.All(b => b == 0), "clear sky: some coverage byte non-zero");
    bool allA0 = true;
    for (int i = 0; i < Cols * Cols; i++) if (Alpha(k.Rgba, i) != 0) { allA0 = false; break; }
    Check(allA0, "clear sky: some texel alpha non-zero");
    var sunDir = Vector3.Normalize(new Vector3(0.3f, 0.5f, 0.2f)) * 12.0f;
    Check(k.Coverage(sunDir) == 0.0f, "clear sky: coverage non-zero toward the sun");
}

// ── 2. Density shapes the field; regen is deterministic. ──
{
    var a = new CloudField();
    var b = new CloudField();
    a.Rebuild(1.0f, Frame());
    b.Rebuild(1.0f, Frame());
    Check(a.Tile.SequenceEqual(b.Tile), "overcast: two rebuilds gave different tiles");
    Check(a.Rgba.SequenceEqual(b.Rgba), "overcast: two rebuilds gave different texels");
    Check(a.Tile.All(v => v > 0), "overcast (C=1): a clear cell survived");
    uint mean = 0; foreach (var v in a.Tile) mean += v; mean /= (uint)a.Tile.Length;
    Check(mean > 200, $"overcast mean {mean} (expected > 200)");

    var mid = new CloudField();
    mid.Rebuild(0.6f, Frame());
    int clear = mid.Tile.Count(v => v == 0);
    int covered = mid.Tile.Count(v => v > 100);
    Check(clear > 0 && covered > 0, $"scattered (C=0.6): clear={clear} covered={covered}");
}

// ── 3. Incremental 32-row bands tile the full field (absolute-coordinate keying). ──
{
    var inc = new CloudField();
    inc.Rebuild(0.6f, Frame());          // ends phase 1, scroll 0
    for (int i = 0; i < 4; i++) inc.Tick(1.0f, 0.6f, Frame());
    var full = new CloudField();
    full.SetPhase(1);
    full.Rebuild(0.6f, Frame());
    Check(inc.Tile.SequenceEqual(full.Tile), "incremental bands != full rebuild (tile)");
    // Row 0's colour legitimately differs (the persistent prev-row scratch); compare from row 1.
    Check(inc.Rgba.Skip(Cols * 4).SequenceEqual(full.Rgba.Skip(Cols * 4)),
        "incremental bands != full rebuild (texels from row 1)");
}

// ── 4. Colour pass: glow off => pure gradient; alpha == coverage byte; glow only brightens. ──
{
    var f = Frame(); f.GlowTrack = 0.0f;   // glow off: every texel is the pure gradient
    var k = new CloudField();
    k.Rebuild(1.0f, f);
    float inv255 = BitConverter.UInt32BitsToSingle(0x3b808081u);
    byte Pack(double ch) { double c = ch < 1.0 ? ch : 1.0; return (byte)(BitConverter.SingleToUInt32Bits((float)(c * 255.0 + 512.0)) >> 14); }
    bool gradOk = true, alphaOk = true;
    for (int g = 0; g < Cols * Cols; g++)
    {
        byte t = k.Tile[g];
        if (Alpha(k.Rgba, g) != t) { alphaOk = false; break; }
        double n = ((255 - t) >> 1) + 64;
        double p = (double)inv255 * n;
        byte w0 = Pack((float)((double)f.Slope.X * p + f.GBase.X));
        byte w1 = Pack((float)((double)f.Slope.Y * p + f.GBase.Y));
        byte w2 = Pack((float)((double)f.Slope.Z * p + f.GBase.Z));
        if (k.Rgba[g * 4] != w0 || k.Rgba[g * 4 + 1] != w1 || k.Rgba[g * 4 + 2] != w2) { gradOk = false; break; }
    }
    Check(alphaOk, "colour pass: texel alpha != coverage byte");
    Check(gradOk, "colour pass: gradient bytes != slope*p + gbase");

    var kl = new CloudField();
    kl.Rebuild(1.0f, Frame());   // glow on
    int brighter = 0; bool neverDarker = true;
    for (int g = 0; g < Cols * Cols; g++)
    {
        if (kl.Rgba[g * 4] > k.Rgba[g * 4]) brighter++;
        if (kl.Rgba[g * 4] < k.Rgba[g * 4]) { neverDarker = false; }
    }
    Check(brighter > 0, "colour pass: the sun glow never fired");
    Check(neverDarker, "colour pass: the glow darkened a channel below the gradient");
}

// ── 5. Azimuthal projection: zenith -> tile centre, horizon -> rim. ──
{
    var k = new CloudField();
    Array.Clear(k.Tile);
    int mid = Cols / 2;
    k.Tile[mid * Cols + mid] = 255;
    Check(k.Coverage(new Vector3(0.0f, 12.0f, 0.0f)) == 1.0f, "projection: zenith did not hit the centre");
    k.Tile[mid * Cols] = 51;
    float r = k.Coverage(new Vector3(12.0f, 0.0f, 0.0f));
    Check(MathF.Abs(r - 0.2f) < 1e-3f, $"projection: rim read {r} (expected ~0.2)");
    Check(k.Coverage(new Vector3(12.0f, -4.0f, 0.0f)) == r, "projection: below-horizon did not clamp to the rim");
}

// ── 6. The cloud sun-glow day envelope (8-key track with the twilight notches + seam wrap). ──
{
    Check(CloudField.GlowTrack(12.0f) == 1.0f, "glow envelope: noon != 1.0");
    Check(CloudField.GlowTrack(0.20139f * 24.0f) == 0.0f, "glow envelope: 04:50 notch != 0");
    Check(CloudField.GlowTrack(0.9236f * 24.0f) < 0.01f, "glow envelope: dusk notch not near 0");
    Check(CloudField.GlowTrack(0.9237f * 24.0f) == 1.0f, "glow envelope: seam did not wrap past 22:10 to 1.0");
    Check(CloudField.GlowTrack(0.95f * 24.0f) == 1.0f, "glow envelope: deep night not wrapped to 1.0");
    Check(CloudField.GlowIsSun(12.0f) && !CloudField.GlowIsSun(0.0f), "glow body: sun/moon pick wrong");
}

Console.WriteLine($"[cloud-field-check] {checks - failed}/{checks} checks passed");
return failed == 0 ? 0 : 1;
