using System.Diagnostics;
using System.IO;
using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Units;

namespace MSUIClient.Engine;

// The character-select "glue booth" - the per-race 3D background plus the selected character
// standing in it. SYSTEM_CHARACTER_SELECT.md phases 1 (scene) + 2 (character).
//
// This is the SAME glue-booth mechanism the login uses (Engine/GlueScene.cs renders UI_MainMenu
// for the login half of it); char-select swaps the model per race, turns fog OFF, and stands the
// picked roster character in front of it. benilla GlueParent.lua's SetBackgroundModel HACK block
// maps each race to a scene token; portrait/glue_booth.rs loads Interface\Glues\Models\UI_<token>\
// UI_<token>.m2 - the same asset shape as UI_MainMenu - so GlueScene loads and frames it unchanged.
// The create-vs-select fog fork (benilla glue_booth.rs:471-479) is byte-verified: char-SELECT
// renders the Race scene UNFOGGED, so the booth always loads it with fog OFF.
//
// THE CHARACTER (phase 2). The dressed 3D character pipeline already exists (SYSTEM_CHARACTER.md /
// World/Units/CharacterRenderer.cs - the same skinned M2 that draws the in-world player). The booth
// owns its OWN CharacterRenderer (independent of the world one, which is disabled pre-world), builds
// the selected roster Character into it with the exact ApplyServerCharacter recipe (race/gender +
// appearance bytes + the 19 equipment display ids), and renders it over the backdrop through a
// dedicated portrait Camera.
//
// FRAMING NOTE (phase 2a, deliberately pragmatic). The scene mesh lives in glTF Y-up model space;
// the CharacterRenderer works natively in WoW Z-up world space with its own Camera. Rather than
// bridge the two spaces blind, phase 2a frames the character with an INDEPENDENT Z-up portrait
// camera (character at the origin, camera in front) and composites it over the backdrop, clearing
// depth between them so the character always draws in front. Every framing/placement/scale/light
// value is a live knob (BoothTune) so it dials in on-screen in one run. Phase 2b locks the
// character onto the scene's authored camera 0 + attachment 0 (Scene.Eye/Target/FovDiag/Ambient
// are already exposed for that) and adds drag-to-rotate; 2a proves the model builds and renders.
//
// Best-effort throughout: no MpqMount / a failed asset / a failed model build leaves the relevant
// piece null and it simply doesn't draw; the roster window still shows over whatever loaded.
public sealed class GlueBooth : IDisposable
{
    private readonly GL _gl;
    private readonly MpqMount _mpq;
    private readonly ClientConfig _config;
    private readonly AssetWorkerPool? _workers;
    private readonly GpuUploadWorker? _uploads;

    // The per-race background scene and the token it was built from. Switching race disposes the old
    // scene and loads the new one; selection changes are user-paced (a row click), so a rebuild on a
    // switch is fine. Troll shares Orc's scene and Gnome shares Dwarf's, so those switches no-op.
    private GlueScene? _scene;
    private string _token = "";

    // The booth-owned character and the roster entry it was last built from (rebuild only when the
    // pick changes - Load + Reload is not free). Race/Gender track the loaded base model so we only
    // re-Load the skeleton when they actually change (as ApplyServerCharacter does).
    // Built characters cached by guid so re-selecting one is INSTANT (WoW keeps the glue models
    // around per account). First view of a guid builds + dresses it (M2 parse / BLP decode / atlas
    // composite / GPU upload - the slow part); every later visit just re-activates the cached one.
    private readonly Dictionary<ulong, CharacterRenderer> _chars = new();
    private CharacterRenderer? _char;   // the ACTIVE (currently shown) renderer, an entry of _chars
    private ulong _charGuid;            // guid of the active character (0 = none)
    private bool _charShaderFailed;     // renderer/shader creation failed once; stop retrying every pick

    // The create-screen preview: ONE live model rebuilt in place as the create selection changes
    // (separate from the guid cache, which is for roster picks). Race/gender change re-Loads the
    // skeleton; an appearance change just Reloads. 0xFF = no skeleton loaded yet.
    private CharacterRenderer? _createChar;
    private byte _createRace = 0xFF, _createSex = 0xFF;
    private string _createLookKey = "";

    // The portrait camera the character is framed by (Z-up, independent of the world camera).
    private readonly Camera _cam = new();

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastMs;
    private float _autoAngle;        // accumulated auto-rotate yaw (radians)

    // benilla shows Orc before a roster arrives. Race id 2 (Orc) -> token "Orc".
    private const string PlaceholderToken = "Orc";

    public GlueBooth(GL gl, MpqMount mpq, ClientConfig config,
        AssetWorkerPool? workers = null, GpuUploadWorker? uploads = null)
    {
        _gl = gl;
        _mpq = mpq;
        _config = config;
        _workers = workers;
        _uploads = uploads;
    }

    /// <summary>True once a race scene has loaded and has geometry to draw.</summary>
    public bool Ok => _scene is { Ok: true };

    /// <summary>
    /// Transfer the already-built selected avatar to the world renderer. The
    /// caller owns the returned renderer; removing it from this cache prevents
    /// Dispose from deleting its GL objects with the rest of the glue booth.
    /// </summary>
    public CharacterRenderer? TakeCharacter(ulong guid)
    {
        if (!_chars.Remove(guid, out CharacterRenderer? renderer)) return null;
        if (_charGuid == guid)
        {
            _char = null;
            _charGuid = 0;
        }
        return renderer;
    }

    /// <summary>The live scene (its authored camera 0 + rig), for the phase-2b attachment lock. Null until a race is set.</summary>
    public GlueScene? Scene => _scene;

    /// <summary>ChrRaces id -> glue scene token (benilla scene_token). Troll->Orc, Gnome->Dwarf, Undead->Scourge.</summary>
    public static string SceneToken(int race) => race switch
    {
        1 => "Human",
        2 => "Orc",
        3 => "Dwarf",
        4 => "NightElf",
        5 => "Scourge",
        6 => "Tauren",
        7 => "Dwarf",     // Gnome shares the Dwarf scene
        8 => "Orc",       // Troll shares the Orc scene
        _ => PlaceholderToken,
    };

    private static string ModelPath(string token) =>
        $@"Interface\Glues\Models\UI_{token}\UI_{token}.m2";

    /// <summary>ChrRaces id -> character model folder name (Undead's folder is "Scourge").</summary>
    private static string RaceFolder(byte race) => race switch
    {
        1 => "Human", 2 => "Orc", 3 => "Dwarf", 4 => "NightElf",
        5 => "Scourge", 6 => "Tauren", 7 => "Gnome", 8 => "Troll", _ => "Human"
    };

    /// <summary>Ensure the booth is showing the scene for this race id (no character). Orc placeholder default.</summary>
    public void SetRace(int race) => SetToken(SceneToken(race));

    /// <summary>Load the Orc placeholder scene (benilla's pre-roster background), and drop any built character.</summary>
    public void ShowPlaceholder()
    {
        SetToken(PlaceholderToken);
        if (_char is not null) _char.Enabled = false;   // keep it cached, just stop drawing it
        _char = null;
        _charGuid = 0;
    }

    /// <summary>
    /// Show the given roster character: switch the background to its race scene and (re)build the
    /// dressed model. No-op if the same character is already built. Loads the model with the exact
    /// ApplyServerCharacter recipe (appearance bytes + the 19 equipment display ids).
    /// </summary>
    public void SetCharacter(Character c)
    {
        SetToken(SceneToken(c.Race));
        if (_charGuid == c.Guid) return;   // already processed this pick (built, cached, or failed - do not retry)
        _charGuid = c.Guid;

        if (_char is not null) _char.Enabled = false;           // hide whoever was shown

        // Cache hit: re-activate the already-built model. This is the whole point - no rebuild.
        if (_chars.TryGetValue(c.Guid, out var cached))
        {
            _char = cached;
            _char.Enabled = true;
            return;
        }

        // First view of this guid: build + dress once, then cache it (real guids are never 0, so a
        // model that fails to build is latched by _charGuid and not re-parsed every frame).
        _char = BuildCharacter(c);
    }

    /// <summary>
    /// Drive the booth from the CREATE screen's live selection instead of a roster pick: switch the
    /// background to the race scene and (re)build ONE preview model as race/gender/appearance change.
    /// Diffed so a per-frame call is cheap - only a real change re-Loads/Reloads. The class-driven
    /// starting outfit is a later pass, so the preview shows the undressed body for now.
    /// </summary>
    public void SetCreateLook(byte race, byte sex, byte cls, byte skin, byte face, byte hairStyle, byte hairColor, byte facialHair, CharacterEquipment equip)
    {
        SetToken(SceneToken(race));

        string key = $"{race}.{sex}.{cls}.{skin}.{face}.{hairStyle}.{hairColor}.{facialHair}";
        if (_createLookKey == key && _createChar is not null)
        {
            if (!_createChar.Enabled) _createChar.Enabled = true;
            _char = _createChar;   // ensure Render draws the preview
            _charGuid = 0;
            return;
        }
        if (_charShaderFailed) return;

        if (_createChar is null)
        {
            try
            {
                string shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
                if (!File.Exists(Path.Combine(shaderDir, "character.vert")))
                    shaderDir = Path.Combine(_config.RepoRoot, "MSUIClient", "Shaders");
                _createChar = new CharacterRenderer(_gl, _config, _workers, _uploads);
                _createChar.LoadShaders(shaderDir);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[booth] create-preview renderer unavailable: {e.Message}");
                _charShaderFailed = true;
                _createChar = null;
                return;
            }
        }

        try
        {
            if (_createRace != race || _createSex != sex)
            {
                string raceFolder = RaceFolder(race);
                string gender = sex == 1 ? "Female" : "Male";
                if (!_createChar.Load(raceFolder, gender))
                {
                    Console.WriteLine($"[booth] create preview could not load {raceFolder} {gender}");
                    _createLookKey = key;   // latch so a broken combo isn't retried every frame
                    return;
                }
                _createRace = race;
                _createSex = sex;
            }
            _createChar.SkinId = skin;
            _createChar.FaceId = face;
            _createChar.HairStyleId = hairStyle;
            _createChar.HairColorId = hairColor;
            _createChar.FacialHairId = facialHair;
            _createChar.Equipment = equip;   // the (race,class,sex) starting outfit (CharStartOutfit.dbc)
            _createChar.Reload();
            _createChar.Enabled = true;
            _createLookKey = key;
            _char = _createChar;
            _charGuid = 0;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[booth] create preview build failed: {e.Message}");
            _createLookKey = key;
        }
    }

    private void SetToken(string token)
    {
        if (string.IsNullOrEmpty(token)) token = PlaceholderToken;
        if (_scene is not null && string.Equals(token, _token, StringComparison.OrdinalIgnoreCase))
            return;

        _scene?.Dispose();
        _scene = null;
        _token = token;
        try
        {
            _scene = new GlueScene(_gl, _mpq, _config, ModelPath(token), fogEnabled: false);
            if (!_scene.Ok)
                Console.WriteLine($"[booth] UI_{token} did not load; character-select background will be blank");
            else
                Console.WriteLine($"[booth] character-select background: UI_{token}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[booth] UI_{token} failed: {e.Message}");
            _scene = null;
        }
    }

    /// <summary>
    /// Build + dress a fresh renderer for this character and cache it by guid. Returns the renderer
    /// (already Enabled), or null if shaders/model/build failed. Each cached renderer owns its own
    /// skeleton, so it Loads once - no per-instance race/gender re-Load dance.
    /// </summary>
    private CharacterRenderer? BuildCharacter(Character c)
    {
        if (_charShaderFailed) return null;

        CharacterRenderer r;
        try
        {
            string shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
            if (!File.Exists(Path.Combine(shaderDir, "character.vert")))
                shaderDir = Path.Combine(_config.RepoRoot, "MSUIClient", "Shaders");
            r = new CharacterRenderer(_gl, _config, _workers, _uploads);
            r.LoadShaders(shaderDir);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[booth] character renderer unavailable: {e.Message}");
            _charShaderFailed = true;   // shaders missing; every pick would fail the same way
            return null;
        }

        try
        {
            string race = RaceFolder(c.Race);
            string gender = c.Gender == 1 ? "Female" : "Male";
            if (!r.Load(race, gender))
            {
                Console.WriteLine($"[booth] could not load {race} {gender}");
                r.Dispose();
                return null;
            }

            r.SkinId = c.Skin;
            r.FaceId = c.Face;
            r.HairStyleId = c.HairStyle;
            r.HairColorId = c.HairColor;
            r.FacialHairId = c.FacialHair;
            r.Equipment = BuildEquipment(c);
            r.Reload();
            r.Enabled = true;

            _chars[c.Guid] = r;   // cache it: the next visit to this guid is instant
            Console.WriteLine($"[booth] character built + cached: {c.Name} - {race} {gender} " +
                              $"({c.Equipment.Count(e => e.DisplayId != 0)} equipped)");
            return r;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[booth] character build failed: {e.Message}");
            r.Dispose();
            return null;
        }
    }

    /// <summary>Turn the roster's 19 visible-item display ids into a dressable equipment set (honours hide-helm/cloak).</summary>
    private static CharacterEquipment BuildEquipment(Character c)
    {
        const uint HideHelm = 0x400, HideCloak = 0x800;   // CHARACTER_FLAG_HIDE_HELM / _HIDE_CLOAK
        var kit = new CharacterEquipment();
        for (int i = 0; i < c.Equipment.Length; i++)
        {
            var eq = c.Equipment[i];
            if (eq.DisplayId == 0) continue;
            int inv = eq.InventoryType;
            if (inv == CharacterEquipment.Slot.Head && (c.Flags & HideHelm) != 0) continue;
            if (inv == CharacterEquipment.Slot.Cloak && (c.Flags & HideCloak) != 0) continue;
            kit.Add($"slot{i}", eq.DisplayId, inv);
        }
        return kit;
    }

    /// <summary>Draw the current race scene fullscreen, then the character standing in it, framed by
    /// the SAME authored camera 0 as the scene (benilla: one camera, character on attachment 0).</summary>
    public void Render(int viewportW, int viewportH)
    {
        if (_scene is null) ShowPlaceholder();   // first draw before any pick: Orc placeholder
        if (_scene is not { Ok: true }) return;

        _scene.SceneTint = BoothTune.SceneTint();       // brighten/warm the backdrop toward the OG (login untouched)
        _scene.SceneFillDir = BoothTune.FillDir(_scene.PrimaryLightDir);  // fill rides the scene's own sun direction
        _scene.SceneFillColor = BoothTune.FillColor();
        _scene.Render(viewportW, viewportH);

        double now = _clock.Elapsed.TotalMilliseconds;
        float dt = (float)((now - _lastMs) / 1000.0);
        _lastMs = now;
        dt = Math.Clamp(dt, 0f, 0.1f);

        if (_char is null || !_char.Enabled || viewportH <= 0) return;

        // benilla renders the character through the scene's OWN authored camera 0 - the very camera
        // that framed the background - and stands it on the scene model's attachment 0. One camera,
        // one depth buffer: the character shares the scene's perspective, so it sits IN the scene
        // rather than floating in a separate portrait (which is what made the road/stage read wrong).
        //
        // The scene renders in glTF Y-up; CharacterRenderer works in WoW Z-up. The two spaces are one
        // 90-degree rotation apart, R = RotX(+90) which sends Y-up +Y (up) to Z-up +Z (up). Put the
        // character in the Z-up IMAGE of the scene (stand point + camera), and character clip == scene
        // clip, so they lock and depth-test against each other. No coordinate guessing, no depth clear.
        float aspect = viewportW / (float)viewportH;
        Vector3 eyeZ = ToZup(_scene.Eye);
        Vector3 tgtZ = ToZup(_scene.Target);
        Vector3 standZ = ToZup(_scene.StageSpot ?? _scene.Target) + new Vector3(0f, 0f, BoothTune.CharZOffset);

        // Solve the orbit-camera params so Position == eyeZ and EyeTarget == tgtZ (up = +Z = R*+Y).
        Vector3 d = eyeZ - tgtZ;
        float dist = d.Length();
        if (dist < 1e-4f) return;                       // degenerate camera; skip the character this frame
        float pitch = MathF.Asin(Math.Clamp(d.Z / dist, -1f, 1f));
        float yawCam = MathF.Atan2(-d.Y, -d.X);         // OrbitDirection = (-cos y*cp, -sin y*cp, sin p)
        float fovy = _scene.FovDiag / MathF.Sqrt(1f + aspect * aspect);

        _cam.Target = tgtZ;
        _cam.EyeHeight = 0f;
        _cam.OrbitYaw = 0f;
        _cam.Yaw = yawCam;
        _cam.Pitch = pitch;
        _cam.Distance = dist;
        _cam.EffectiveDistance = dist;
        _cam.FieldOfViewDegrees = fovy * 180f / MathF.PI;
        _cam.AspectRatio = aspect;
        _cam.NearPlane = MathF.Max(_scene.NearPlane, 0.02f);
        _cam.FarPlane = MathF.Max(_scene.FarPlane, 1000f);   // benilla floors far so unfogged geometry isn't sliced

        // Face the camera: the character's model-forward maps to world (sin h, -cos h, 0) with
        // h = Yaw + 90deg, so aim its forward along the horizontal direction toward the eye.
        Vector3 toCam = eyeZ - standZ;
        var n = new Vector2(toCam.X, toCam.Y);
        float baseYaw = n.LengthSquared() > 1e-6f
            ? MathF.Atan2(n.X, -n.Y) - MathF.PI / 2f
            : 0f;
        if (BoothTune.AutoRotate) _autoAngle += dt * (BoothTune.AutoRotateSpeed * MathF.PI / 180f);
        float yaw = baseYaw + BoothTune.CharYawDegrees * MathF.PI / 180f + _autoAngle;

        _char.ModelScale = BoothTune.CharScale;
        _char.AmbientIntensity = BoothTune.AmbientIntensity;
        _char.SunIntensity = BoothTune.SunIntensity;
        _char.SunColor = BoothTune.CharSunColor();          // key-light hue (warm/cool)
        _char.AmbientColor = BoothTune.CharAmbientColor();  // ambient/shadow hue (warm/cool)
        _char.ShadowSoftness = BoothTune.CharShadowSoftness;// terminator softness (less harsh shadow)

        // Key light is VIEWER-RELATIVE, not the fixed world vector CharacterRenderer defaults to.
        // That default is a single world-space direction, but each race scene frames the character
        // from its OWN camera orientation, so one world vector lands on a different cheek per race
        // (which reads as "the sun is on the wrong side"). Derive the to-light from the camera/stand
        // geometry so the face is lit from a FIXED SCREEN angle - upper front-left, the OG glue key -
        // on every scene. Tied to the camera and not the model, spinning the character (< / >) turns
        // him INTO the light like a real sun rather than dragging the lit side around with his body.
        _char.SunDirection = KeyLightDir(eyeZ, standZ, BoothTune.CharKeyAzimuthDeg, BoothTune.CharKeyElevationDeg);

        var state = new CharacterRenderer.UnitState
        {
            Position = standZ,
            Yaw = yaw,
            Grounded = true,
            VerticalVelocity = 0f,
            FallTimeMs = 0f,
            Walking = false,
            Flying = false,
        };

        // Share the scene's depth buffer (no clear) so the character depth-tests against the scene.
        // Depth write on for the opaque pass; back-face cull on (CharacterRenderer assumes culling
        // starts ON, and the scene draw left it OFF).
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.CullFace);

        _char.Update(dt, state);
        _char.Render(_cam, state);
    }

    // glTF Y-up -> WoW Z-up (R = RotX+90): (x, y, z) -> (x, -z, y). Sends +Y (up) to +Z (up).
    private static Vector3 ToZup(Vector3 v) => new(v.X, -v.Z, v.Y);

    // The character key light as a world-space TO-LIGHT vector (character.frag: uSunDirection points
    // toward the sun), built in the VIEWER frame from the camera (eye) and stand point so the lit
    // side is a fixed SCREEN angle on every race scene. az: degrees off dead-front toward the
    // viewer's LEFT (the character's own right, since he faces us); el: degrees above the horizon.
    // World is WoW Z-up right-handed (+X north, +Y west, +Z up); .NET CreateLookAt makes screen-right
    // = up x toCam, so the viewer's left is its negation, (toCam.Y, -toCam.X, 0).
    private static Vector3 KeyLightDir(Vector3 eye, Vector3 stand, float azDeg, float elDeg)
    {
        var toCam = new Vector3(eye.X - stand.X, eye.Y - stand.Y, 0f);   // horizontal "toward viewer" = front
        toCam = toCam.LengthSquared() > 1e-8f ? Vector3.Normalize(toCam) : new Vector3(1f, 0f, 0f);
        var viewerLeft = new Vector3(toCam.Y, -toCam.X, 0f);             // -(up x toCam): the screen's left
        float az = azDeg * MathF.PI / 180f, el = elDeg * MathF.PI / 180f;
        var horiz = toCam * MathF.Cos(az) + viewerLeft * MathF.Sin(az);
        return Vector3.Normalize(horiz * MathF.Cos(el) + new Vector3(0f, 0f, 1f) * MathF.Sin(el));
    }

    public void Dispose()
    {
        foreach (var r in _chars.Values) r.Dispose();
        _chars.Clear();
        _createChar?.Dispose();
        _createChar = null;
        _char = null;
        _scene?.Dispose();
        _scene = null;
    }
}

// Live dial-in knobs for the character-select booth (the same dev-scaffold pattern as GlueTune).
// Read every frame by GlueBooth.Render and by the char-select tuning sliders (Program.Net.cs).
// Once dialed in on-screen these get baked as defaults; phase 2b replaces the free portrait camera
// with the scene's authored camera 0 + attachment 0 and most of these collapse away.
public static class BoothTune
{
    // The camera now comes from the scene's authored camera 0, so the only knobs are fine-tune
    // adjustments on top of the benilla-faithful placement.
    public static float CharScale = 1.00f;        // model scale (benilla uses 1.0)
    public static float CharZOffset = 0.00f;      // fine vertical nudge on the stage spot (scene units)
    public static float CharYawDegrees = 0f;      // facing tweak on top of "face the camera" (flip to 180 if backwards)
    public static bool AutoRotate = false;        // slow turntable spin
    public static float AutoRotateSpeed = 30f;    // deg/sec
    public static float DragRotateDegPerPx = 0.2f;// drag-on-the-model spin rate (benilla char_select/input.rs rotate_model). Nico: 0.4 was too twitchy
    // The character-lighting block below is Nico's SIGNED-OFF preset (baked 2026-07-29 off the
    // tuning modal). It drives BOTH glue screens - character SELECT and character CREATE render the
    // same booth character through GlueBooth.Render, so there is one set of knobs, not two. Change
    // these only against a side-by-side with 1.12, and re-bake from `Log booth values`.
    public static float AmbientIntensity = 0.456f;// character ambient/shadow BRIGHTNESS (the shadow-lift)
    public static float SunIntensity = 0.555f;    // character key light BRIGHTNESS (lit side, not blown out)
    public static float CharSunWarmth = 0.318f;   // key light HUE at unit luma: + warm (red up, blue down), - cool. Brightness stays on SunIntensity
    public static float CharAmbientWarmth = -0.190f;// ambient/shadow HUE at unit luma: - cool (classic cool fill), + warm
    public static float CharShadowSoftness = 0.226f;// terminator softness: 0 = hard Lambert edge, 1 = light wraps fully
    public static float CharKeyAzimuthDeg = 29.320f; // character key light: deg off dead-front toward the viewer's LEFT (char's right)
    public static float CharKeyElevationDeg = 19.545f;// character key light: degrees above the horizon
    public static float SceneBrightness = 1.022f; // backdrop (scene mesh) brightness - login unaffected (default 1.0 there)
    public static float SceneWarmth = 0.04f;      // backdrop warm shift (red up, blue down) - small, so it doesn't go yellow
    public static float SunFillIntensity = 0.00f; // supplemental fill on the backdrop FLOOR (0 = off; dialed-in off 2026-07-28)
    public static float SunFillElevDeg = 45f;     // fill elevation above the horizon (higher = lights up-facing ground more)
    public static float SunFillAzimOffsetDeg = 0f;// nudge off the scene's own sun direction (0 = exactly the sun)
    public static float SunFillWarmth = 0.06f;    // fill warm shift (small)

    /// <summary>Character key-light colour: warm/cool HUE at unit luma - SunIntensity is the brightness, so
    /// hue and brightness stay independent knobs.</summary>
    public static Vector3 CharSunColor() => new Vector3(1f + CharSunWarmth, 1f, 1f - CharSunWarmth);

    /// <summary>Character ambient/shadow colour: warm/cool HUE at unit luma - AmbientIntensity is the brightness.</summary>
    public static Vector3 CharAmbientColor() => new Vector3(1f + CharAmbientWarmth, 1f, 1f - CharAmbientWarmth);

    /// <summary>The scene-mesh tint (brightness x warmth) fed to GlueScene.SceneTint for the booth backdrop.</summary>
    public static Vector3 SceneTint() => new Vector3(1f + SceneWarmth, 1f, 1f - SceneWarmth) * SceneBrightness;

    /// <summary>Supplemental fill to-light direction: the scene's own sun (horizontal), rotated by the
    /// azimuth offset and raised to the fill elevation so it lights up-facing surfaces from the sun's side.</summary>
    public static Vector3 FillDir(Vector3 sunHorizontal)
    {
        float aoff = SunFillAzimOffsetDeg * MathF.PI / 180f;
        float ca = MathF.Cos(aoff), sa = MathF.Sin(aoff);
        var h = new Vector3(sunHorizontal.X * ca - sunHorizontal.Z * sa, 0f, sunHorizontal.X * sa + sunHorizontal.Z * ca);
        float hlen = MathF.Sqrt(h.X * h.X + h.Z * h.Z);
        h = hlen > 1e-4f ? h / hlen : new Vector3(0f, 0f, 1f);
        float el = SunFillElevDeg * MathF.PI / 180f;
        return h * MathF.Cos(el) + new Vector3(0f, 1f, 0f) * MathF.Sin(el);
    }

    /// <summary>Supplemental fill colour (warm x intensity). (0,0,0) when intensity is 0.</summary>
    public static Vector3 FillColor() => new Vector3(1f + SunFillWarmth, 1f, 1f - SunFillWarmth) * SunFillIntensity;

    public static void Reset()
    {
        CharScale = 1.00f; CharZOffset = 0.00f; CharYawDegrees = 0f;
        AutoRotate = false; AutoRotateSpeed = 30f; DragRotateDegPerPx = 0.2f;
        AmbientIntensity = 0.456f; SunIntensity = 0.555f;
        CharSunWarmth = 0.318f; CharAmbientWarmth = -0.190f; CharShadowSoftness = 0.226f;
        CharKeyAzimuthDeg = 29.320f; CharKeyElevationDeg = 19.545f;
        SceneBrightness = 1.022f; SceneWarmth = 0.04f;
        SunFillIntensity = 0.00f; SunFillElevDeg = 45f; SunFillAzimOffsetDeg = 0f; SunFillWarmth = 0.06f;
    }

    public static void LogValues() => Console.WriteLine(
        $"[booth-tune] scale {CharScale:F2} zoff {CharZOffset:F2} yaw {CharYawDegrees:F0} " +
        $"amb {AmbientIntensity:F2} sun {SunIntensity:F2} sunwarm {CharSunWarmth:F2} ambwarm {CharAmbientWarmth:F2} soft {CharShadowSoftness:F2} keyaz {CharKeyAzimuthDeg:F0} keyel {CharKeyElevationDeg:F0} scenebright {SceneBrightness:F2} warmth {SceneWarmth:F2} " +
        $"fill i{SunFillIntensity:F2} el{SunFillElevDeg:F0} azoff{SunFillAzimOffsetDeg:F0} w{SunFillWarmth:F2}");
}
