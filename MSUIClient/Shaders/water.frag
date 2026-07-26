#version 330 core

// MSUI Client - water/liquid fragment shader (stage 2, "look at wowee").
//
// This is a cut-down port of WoWee's water shader. WoWee samples a captured
// scene-depth texture to know how deep the water is at each pixel; this client
// does not have that pass yet, so instead the DEPTH IS BAKED PER VERTEX
// (aDepth = surfaceZ - groundZ, computed when the mesh is built) and carried in
// here as vDepth. That is enough to drive the three things that make water read
// as water rather than a flat sheet:
//
//   * shoreline fade  - shallow water is nearly transparent (you see the bed),
//                        deep water turns opaque. The soft waterline is the
//                        single biggest depth cue.
//   * depth darkening  - a shallow-to-deep colour ramp (Beer-Lambert-ish).
//   * shoreline foam   - scattered foam particles where the water is shallow.
//
// On top of that: dual-scroll detail-normal ripples, a moving specular sun
// sparkle, a fresnel sky tint at grazing angles, and per-type palettes for
// ocean / river / slime / magma.
//
// ASCII ONLY.

in vec3  vRelPos;    // camera-relative fragment position (length = view distance)
in vec2  vAbsXY;     // absolute world XY, for world-locked ripples and foam
in float vDepth;     // water depth in yards
in float vWave;      // raw Gerstner wave height, for crest foam
in vec3  vNormal;    // analytic surface normal from the vertex stage
flat in float vType;

uniform vec3  uSunDirection;
uniform vec3  uSunColor;
uniform float uSunIntensity;
uniform vec3  uAmbientColor;
uniform float uAmbientIntensity;
uniform float uFogStart;
uniform float uFogEnd;
uniform vec3  uFogColor;
uniform float uTime;

// Real vanilla animated liquid textures: one array of frames per basic type.
// uFrames* is 0 when a type has no texture loaded (then we fall back to the
// procedural surface below). uWaterFps drives frame cross-fade; uTexScale is
// world yards -> UV.
uniform sampler2DArray uTexWater;
uniform sampler2DArray uTexOcean;
uniform sampler2DArray uTexSlime;
uniform sampler2DArray uTexMagma;
uniform int   uFramesWater;
uniform int   uFramesOcean;
uniform int   uFramesSlime;
uniform int   uFramesMagma;
uniform float uWaterFps;
uniform float uTexScale;

// Live tuning knobs (Water Tuning HUD). All default to the current look.
uniform float uFrameBlend;   // 0 = discrete frame swap, 1 = full cross-fade
uniform float uTexBright;    // texture brightness multiply
uniform float uTexContrast;  // texture contrast around mid
uniform vec3  uTexTint;      // texture per-channel tint
uniform float uOpacity;      // deep-water alpha
uniform float uShoreFade;    // alpha fraction at the waterline
uniform float uShoreWidth;   // yards the shoreline softens over
uniform float uDepthDarken;  // deep-water brightness multiplier
uniform float uDepthRate;    // depth darkening rate
uniform float uBrightness;   // base surface brightness
uniform float uAmbientAmt;   // ambient contribution
uniform float uSunAmt;       // sun contribution
uniform float uSkySheen;     // grazing sky tint

// Authored water colours, LightIntBand 13-16 + LightParams alphas (PLAN_12).
// uAuthoredWater is the whole switch: at 0 every mix() below picks the left
// operand and this file computes exactly what it computed before PLAN_12.
uniform float uAuthoredWater;
uniform vec3  uOceanClose;
uniform vec3  uOceanFar;
uniform vec3  uRiverClose;
uniform vec3  uRiverFar;
uniform float uOceanAlphaShallow;
uniform float uOceanAlphaDeep;
uniform float uRiverAlphaShallow;
uniform float uRiverAlphaDeep;

// How hard the animated liquid texture is ADDED on top of the body colour.
//
// MEASURED 2026-07-26, and it is the whole reason this uniform exists: the
// vanilla water and ocean BLPs are near-black GREYSCALE highlight masks, not
// coloured surfaces. lake_a.1.blp has mean RGB (0.014, 0.014, 0.014) and a peak
// luminance of 0.158; ocean_h.1.blp is the same. Compare lava.1.blp
// (0.688, 0.089, 0.000) and slime.1.blp (0.268, 0.517, 0.074), which ARE real
// coloured textures - which is exactly why magma and slime look right today and
// take the early-return branch below, while water and ocean did not.
//
// So the texture supplies the animated sparkle and NOTHING ELSE. The colour has
// to come from the body uniforms above. Gain lifts the 0..0.158 mask into a
// visible highlight range.
uniform float uHighlightGain;

// -- The walking wake (PLAN_16) ----------------------------------------------
//
// The V-shaped bow wave you push through shallow water.
//
// XTextures\splash\wake.blp IS THAT V. Decoded, its alpha channel is a narrow
// wedge at the top that splits into two diverging arms toward the bottom -
// dense churn right behind you, spreading into a trailing V. Blizzard authored
// the shape; this just has to place it.
//
// FIRST ATTEMPT GOT THIS WRONG and it is worth recording: the mask was stamped
// eight times along a trail of recent positions, which drew eight overlapping
// Vs and read as a chain of blobs. ONE stamp, anchored at the player and
// extending backward, is the whole effect. The texture is the trail.
//
// Only the ALPHA is sampled - wake.blp is near-black greyscale (mean RGB 0.024),
// a mask like every other water texture here. Colour comes from uWakeColor.
uniform vec2      uWakePos;      // world XY of the apex
uniform vec2      uWakeDir;      // unit direction of travel
uniform float     uWakeAmount;   // 0 = off; ramps with speed, eases out when you stop
uniform float     uWakeLength;   // yards the V extends BEHIND
uniform float     uWakeWidth;    // yards across at the widest
uniform float     uWakeAhead;    // yards the apex sits ahead of the origin
uniform float     uWakeRepeat;   // how many wavefronts fit along the length
uniform float     uWakeScroll;   // world-locking phase, driven by distance travelled
uniform vec3      uWakeColor;
uniform float     uWakeOpacity;  // how much the wake also lifts ALPHA
uniform sampler2D uTexWake;
uniform int       uHasWakeTex;   // 0 = mask missing, fall back to an analytic V

// Wake coverage at this fragment, 0..1.
float wakeAt(vec2 worldXY)
{
    if (uWakeAmount <= 0.001) return 0.0;

    vec2 d    = worldXY - uWakePos;
    vec2 perp = vec2(-uWakeDir.y, uWakeDir.x);

    float along = dot(d, uWakeDir);   // + is ahead of the player
    float lat   = dot(d, perp);

    // v runs 0 at the apex (just ahead of the feet) to 1 at the far tail.
    float v = (uWakeAhead - along) / max(uWakeLength, 0.01);
    float u = lat / max(uWakeWidth, 0.01) + 0.5;
    if (u < 0.0 || u > 1.0 || v < 0.0 || v > 1.0) return 0.0;

    // PROPAGATION. The V must not ride along rigidly stuck to the character -
    // in the real client the wavefronts are laid down in the water and the
    // player moves THROUGH them, so they appear to stream backward and new ones
    // keep being born at the feet.
    //
    // uWakeScroll is advanced by DISTANCE TRAVELLED, not by time, which is what
    // makes the pattern world-locked: move a yard forward and the phase shifts by
    // exactly the yard, so a given crest stays put in the river. Tiling by
    // uWakeRepeat is what gives a train of crests instead of one frozen chevron.
    float vt = fract(v * uWakeRepeat + uWakeScroll);

    float m;
    if (uHasWakeTex != 0)
    {
        m = texture(uTexWake, vec2(u, vt)).a;
    }
    else
    {
        // Analytic V, so a missing mask still reads as the right shape rather
        // than as nothing: two thin arms diverging from the apex.
        float arm = abs(u - 0.5) * 2.0;      // 0 centre .. 1 edge
        m = smoothstep(0.10, 0.0, abs(arm - vt)) * step(0.04, vt);
    }

    // The arms spread with distance behind, so narrow the sampled band near the
    // apex - without this every tiled crest is the same width and the whole
    // thing reads as stripes rather than a widening wake.
    m *= smoothstep(0.0, 0.15, v);

    // Thin the tail out so the V dissolves instead of ending on a hard edge.
    // Uses the TRUE v, not the tiled coordinate, so distance fade is monotonic.
    m *= 1.0 - v * v;
    return clamp(m * uWakeAmount, 0.0, 1.0);
}

out vec4 FragColor;

// Sample an animated liquid array. uFrameBlend controls the cross-fade: 0 swaps
// frames in place (the light twinkles/boils - vanilla), 1 blends fully (the light
// glides/"swims"). Returns vec4(-1) as a sentinel when the type has no texture.
vec4 sampleLiquid(sampler2DArray tex, int frames, vec2 uv)
{
    if (frames <= 0) return vec4(-1.0);
    float ff = uTime * uWaterFps;
    float f  = mod(ff, float(frames));
    float f0 = floor(f);
    vec4 a = texture(tex, vec3(uv, f0));
    if (uFrameBlend <= 0.001) return a;
    float f1 = mod(f0 + 1.0, float(frames));
    vec4 b = texture(tex, vec3(uv, f1));
    return mix(a, b, (f - f0) * clamp(uFrameBlend, 0.0, 1.0));
}

// ---- small hash / value noise, for foam and sparkle ----
float hash21(vec2 p){ return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float hash22(vec2 p){ return fract(sin(dot(p, vec2(269.5, 183.3))) * 43758.5453); }
float vnoise(vec2 p)
{
    vec2 i = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash21(i), b = hash21(i + vec2(1,0));
    float c = hash21(i + vec2(0,1)), d = hash21(i + vec2(1,1));
    return mix(mix(a,b,f.x), mix(c,d,f.x), f.y);
}
float fbm(vec2 p, float t)
{
    float v = 0.0;
    v += vnoise(p * 3.0 + t * 0.3) * 0.5;
    v += vnoise(p * 6.0 - t * 0.5) * 0.25;
    v += vnoise(p * 12.0 + t * 0.7) * 0.125;
    return v;
}
// Voronoi-ish cellular distance, for scattered foam particles.
float cellular(vec2 p)
{
    vec2 i = floor(p), f = fract(p);
    float md = 1.0;
    for (int y = -1; y <= 1; y++)
    for (int x = -1; x <= 1; x++)
    {
        vec2 g = vec2(float(x), float(y));
        vec2 o = vec2(hash21(i + g), hash22(i + g));
        md = min(md, length(g + o - f));
    }
    return md;
}

// Detail ripple normal: three scrolling octaves, gradient gives the slope.
vec3 detailNormal(vec2 p, float t)
{
    vec2 d1 = normalize(vec2( 0.86, 0.51));
    vec2 d2 = normalize(vec2(-0.47, 0.88));
    vec2 d3 = normalize(vec2( 0.32,-0.95));
    float f1 = 0.19, f2 = 0.43, f3 = 0.72;
    float s1 = 0.95, s2 = 1.73, s3 = 2.40;
    float a1 = 0.22, a2 = 0.10, a3 = 0.05;
    float c1 = cos(dot(p + d1 * (t*s1*4.0), d1) * f1);
    float c2 = cos(dot(p + d2 * (t*s2*4.0), d2) * f2);
    float c3 = cos(dot(p + d3 * (t*s3*4.0), d3) * f3);
    float gx = c1*d1.x*f1*a1 + c2*d2.x*f2*a2 + c3*d3.x*f3*a3;
    float gy = c1*d1.y*f1*a1 + c2*d2.y*f2*a2 + c3*d3.y*f3*a3;
    return normalize(vec3(-gx, -gy, 1.0));
}

void main()
{
    float dist = length(vRelPos);
    float fog  = clamp((dist - uFogStart) / max(uFogEnd - uFogStart, 1.0), 0.0, 1.0);

    // ===============================================================
    // AUTHENTIC PATH: vanilla's real animated liquid textures.
    // If the matching type has frames loaded, the surface IS the scrolling
    // animated BLP (the true 1.12 shimmer). Scoped block + early return so it
    // never clashes with, and takes precedence over, the procedural fallback.
    // ===============================================================
    {
        vec2  tuv    = vAbsXY * uTexScale;
        bool  tmagma = (vType > 5.5);
        bool  tslime = (vType > 2.5 && vType < 3.5);
        bool  tocean = (vType > 0.5 && vType < 1.5);

        vec4 liq;
        if      (tmagma) liq = sampleLiquid(uTexMagma, uFramesMagma, tuv);
        else if (tslime) liq = sampleLiquid(uTexSlime, uFramesSlime, tuv);
        else
        {
            // River/lake and ocean. Prefer the type's own texture; if it did not
            // load (e.g. the river frames didn't resolve), borrow the other water
            // texture so inland water still gets a REAL animated surface - and so
            // the tuning knobs, which only affect this textured path, stay live.
            bool useOcean = tocean ? (uFramesOcean > 0)
                                   : (uFramesWater <= 0 && uFramesOcean > 0);
            if (useOcean) liq = sampleLiquid(uTexOcean, uFramesOcean, tuv);
            else          liq = sampleLiquid(uTexWater, uFramesWater, tuv);
        }

        if (liq.r >= 0.0)   // sentinel check: >= 0 means a real texel
        {
            vec3  col   = liq.rgb;
            float tfog  = fog;

            if (tmagma || tslime)
            {
                // Self-luminous: show the texture bright, just fade to fog.
                col = mix(uFogColor, col, 1.0 - tfog);
                FragColor = vec4(col, 0.97);
                return;
            }

            // Depth ramp first. The authored tint is a close->far blend, so it
            // has to exist before the tint is applied. Moving these two lines up
            // changes nothing for the old path: both depend only on vDepth and
            // the two uniforms, and neither reads col.
            float tdepthFade = 1.0 - exp(-max(vDepth, 0.0) * uDepthRate);
            float tshore     = smoothstep(0.0, uShoreWidth, vDepth);

            // BODY COLOUR + ADDITIVE HIGHLIGHTS.  (Corrected 2026-07-26.)
            //
            // This used to be `col *= aBody`, a MULTIPLY, on the theory that the
            // texture is the surface and the light data tints it. That theory is
            // dead: the water/ocean BLPs are near-black greyscale masks (see
            // uHighlightGain above, with the measurements). Multiplying a
            // near-black mask by a near-black authored colour - Azeroth's
            // river-close is (0.000, 0.114, 0.161), red exactly zero - is what
            // turned the river into a flat dark sheet with the animation gone.
            //
            // The texture is a HIGHLIGHT OVERLAY. It gets added, not multiplied.
            // The body uniforms carry the colour; LiquidRenderer decides whether
            // those hold the tuned constants or the authored Light.dbc bands, so
            // this shader does not need to know which and uAuthoredWater no
            // longer gates the colour path at all.
            vec3  aBody  = mix(tocean ? uOceanClose : uRiverClose,
                               tocean ? uOceanFar   : uRiverFar,  tdepthFade);
            float aAlpha = mix(tocean ? uOceanAlphaShallow : uRiverAlphaShallow,
                               tocean ? uOceanAlphaDeep    : uRiverAlphaDeep, tdepthFade);

            // The animated sparkle, lifted out of the mask's 0..0.158 range.
            vec3 highlight = col * uHighlightGain;

            // uTexTint / uTexBright stay MULTIPLIERS on the body so a by-eye
            // session still works and Adopt live still captures it.
            col = aBody * uTexTint * uTexBright + highlight;
            col = (col - 0.5) * uTexContrast + 0.5;
            col = max(col, vec3(0.0));

            // FLAT uniform lighting - the texture carries every ripple, so we do
            // NOT relight with the wave normal (that painted the drifting bands).
            float tsun       = max(uSunDirection.z, 0.0);
            vec3  tamb       = uAmbientColor * uAmbientIntensity;

            col *= mix(1.0, uDepthDarken, tdepthFade);
            col *= (uBrightness + tamb * uAmbientAmt + uSunColor * uSunIntensity * tsun * uSunAmt);

            // THE WAKE (PLAN_16). Added after the lighting multiply so churned
            // water reads bright rather than being dimmed with the body, and
            // before fog so a distant wake fades with everything else.
            //
            // With uWakeAmount 0 this is exactly `col += 0`, which is why the
            // feature can be switched off to a bit-identical image.
            float twake = wakeAt(vAbsXY);
            col += twake * uWakeColor;

            // Grazing sky sheen from the VIEW angle only (flat surface) - a smooth
            // static gradient, never a moving band.
            vec3  tV    = normalize(-vRelPos);
            float tfres = clamp(pow(1.0 - max(tV.z, 0.0), 5.0), 0.0, 1.0);
            col = mix(col, uFogColor, tfres * uSkySheen);

            col = mix(col, uFogColor, tfog);

            // uOpacity likewise stays a multiplier over the authored depth ramp.
            // The shoreline softening is a separate, finer effect (uShoreWidth is
            // about a yard) and survives either way.
            float tbodyA = mix(uOpacity, uOpacity * aAlpha, uAuthoredWater);
            float alpha  = clamp(tbodyA * mix(uShoreFade, 1.0, tshore), 0.0, 1.0);

            // The wake also lifts ALPHA, and this is not decoration: a wake
            // happens in SHALLOW water, which is exactly where uShoreFade has
            // already pulled alpha down. Colour alone would be nearly invisible
            // in the one place the effect exists.
            alpha = clamp(alpha + twake * uWakeOpacity, 0.0, 1.0);

            FragColor = vec4(col, alpha);
            return;
        }
    }

    // ===============================================================
    // FALLBACK (no texture found): procedural surface, kept intact below.
    // ===============================================================

    // ---------------------------------------------------------------
    // Magma / slime: self-luminous flowing surfaces, own path.
    // Route by exact MCLQ type code (SYSTEM_WATER.md 1.6): 3 = slime,
    // 6 = magma. River/lake water is type 4 and must fall through to the
    // water path below - the old "> 2.5" test wrongly caught type 4 and
    // painted the river as green slime.
    // ---------------------------------------------------------------
    bool magma = (vType > 5.5);                   // 6
    bool slime = (vType > 2.5 && vType < 3.5);    // 3
    if (magma || slime)
    {
        vec2 uv = vAbsXY;
        float n1 = fbm(uv * 0.06 + vec2(uTime*0.02, uTime*0.03), uTime*0.4);
        float n2 = fbm(uv * 0.10 + vec2(-uTime*0.015, uTime*0.025), uTime*0.3);
        float n3 = vnoise(uv * 0.25 + vec2(uTime*0.04, -uTime*0.02));
        float flow = n1*0.45 + n2*0.35 + n3*0.20;

        vec3 crust, hot, core;
        if (magma) { crust=vec3(0.15,0.04,0.01); hot=vec3(1.0,0.45,0.05); core=vec3(1.0,0.85,0.30); }
        else       { crust=vec3(0.05,0.15,0.02); hot=vec3(0.30,0.80,0.15); core=vec3(0.50,1.0,0.30); }

        float crustMask = smoothstep(0.25, 0.50, flow);
        float coreMask  = smoothstep(0.60, 0.80, flow);
        vec3 col = mix(crust, hot, crustMask);
        col = mix(col, core, coreMask);
        col *= 1.0 + 0.15 * sin(uTime*1.5 + flow*6.0);   // slow pulse
        col *= 1.0 + coreMask * 0.6;                     // emissive core
        col = mix(uFogColor, col, 1.0 - fog);
        FragColor = vec4(col, 0.97);
        return;
    }

    // ---------------------------------------------------------------
    // Water (ocean / river / lake).
    // ---------------------------------------------------------------
    bool ocean = (vType > 0.5 && vType < 1.5);

    vec3 meshN = normalize(vNormal);
    vec3 detN  = detailNormal(vAbsXY, uTime);
    vec3 N     = normalize(mix(meshN, detN, 0.55));

    vec3 V = normalize(-vRelPos);                 // toward camera
    vec3 L = normalize(uSunDirection);
    float NdotV = max(dot(N, V), 0.001);
    float NdotL = max(dot(N, L), 0.0);

    // Depth cues from the baked per-vertex water depth.
    float depthFade = 1.0 - exp(-max(vDepth, 0.0) * 0.22);  // 0 shallow -> 1 deep
    float shore     = smoothstep(0.0, 1.2, vDepth);         // 0 at waterline

    // Opaque, teal-green vanilla water (you do NOT see through it). Kept a touch
    // brighter than a flat dark sheet so the animated shimmer below reads clearly.
    vec3 shallowCol, deepCol;
    float baseAlpha;
    if (ocean) { shallowCol=vec3(0.06,0.20,0.28); deepCol=vec3(0.02,0.09,0.16); baseAlpha=0.90; }
    else       { shallowCol=vec3(0.10,0.26,0.26); deepCol=vec3(0.05,0.15,0.16); baseAlpha=0.85; }

    // PLAN_12: the six numbers above are precisely what LightIntBand 13-16 and
    // the LightParams alphas author. This path only runs when a liquid texture
    // failed to load, but the constants were invented here too and the data is
    // already in the uniforms.
    shallowCol = mix(shallowCol, ocean ? uOceanClose : uRiverClose, uAuthoredWater);
    deepCol    = mix(deepCol,    ocean ? uOceanFar   : uRiverFar,   uAuthoredWater);
    baseAlpha  = mix(baseAlpha,  ocean ? uOceanAlphaDeep : uRiverAlphaDeep, uAuthoredWater);

    vec3 body = mix(shallowCol, deepCol, depthFade);

    // Diffuse-ish lighting of the body colour.
    vec3 amb = uAmbientColor * uAmbientIntensity;
    vec3 lit = body * (amb + uSunColor * uSunIntensity * NdotL * 0.35);

    // Fresnel: dark looking straight down, a modest sky sheen at grazing angles.
    // Vanilla water is reflective, not see-through, so it stays dark-with-sheen.
    float fres = clamp(pow(1.0 - NdotV, 5.0), 0.0, 1.0);
    vec3 sky = uFogColor;
    vec3 col = mix(lit, sky, fres * (ocean ? 0.40 : 0.28));

    // Moving specular sun sparkle - the clearest sign of motion.
    vec3 H = normalize(L + V);
    float spec = pow(max(dot(N, H), 0.0), 200.0);
    float sparkle = pow(max(fbm(vAbsXY * 4.0 + uTime*0.5, uTime*1.5) - 0.55, 0.0) / 0.45, 3.0);
    col += uSunColor * uSunIntensity * (spec * 1.4 + sparkle * 0.10);

    // Wave-crest brightening.
    col += vec3(smoothstep(0.5, 1.0, vWave) * 0.04);

    // ---- Animated surface shimmer: the signature vanilla 1.12 water look ----
    // Dense soft caustic highlights that DRIFT steadily sideways (the constant
    // left-to-right movement), a second cross-scrolling layer to break up tiling,
    // bright speckles on the peaks, and a slow oscillation so the colour breathes.
    // This is the procedural stand-in for vanilla's scrolling animated water BLPs.
    {
        vec2 flow = vAbsXY;
        float t = uTime;
        float g1 = fbm(flow * 0.35 + vec2( t * 0.90, t * 0.20), t);
        float g2 = fbm(flow * 0.75 + vec2(-t * 0.50, t * 0.55), t * 1.3);
        float caustic = g1 * 0.6 + g2 * 0.4;

        float shimmer = smoothstep(0.50, 0.90, caustic);
        col += (ocean ? vec3(0.10, 0.14, 0.18) : vec3(0.11, 0.17, 0.15)) * shimmer;

        // Fine bright speckles riding the caustic peaks.
        float speck = smoothstep(0.82, 0.99, caustic);
        col += vec3(0.20, 0.24, 0.22) * speck;

        // Slow light/dark bands drifting sideways -> "oscillating colours".
        float osc = 0.5 + 0.5 * sin(dot(vAbsXY, vec2(0.12, 0.07)) - t * 1.4);
        col *= 0.92 + 0.16 * osc;
    }

    // Shoreline foam: scattered particles where the water is shallow.
    if (vDepth > 0.02 && vDepth < 2.2)
    {
        float foamDepth = 1.0 - smoothstep(0.0, 1.8, vDepth);
        vec2 warp = vec2(vnoise(vAbsXY*2.5 + uTime*0.08) - 0.5,
                         vnoise(vAbsXY*2.5 + vec2(37.0) + uTime*0.06) - 0.5) * 1.6;
        vec2 fuv = vAbsXY + warp;
        float f1 = (1.0 - smoothstep(0.0, 0.12, cellular(fuv*14.0 + uTime*vec2(0.15,0.08)))) * 0.45;
        float f2 = (1.0 - smoothstep(0.0, 0.07, cellular(fuv*28.0 + uTime*vec2(-0.12,0.22)))) * 0.30;
        float clump = smoothstep(0.30, 0.60, vnoise(vAbsXY*3.0 + uTime*0.15));
        float foam = (f1 + f2) * foamDepth * clump * smoothstep(0.0, 0.10, vDepth);
        col = mix(col, vec3(0.72, 0.80, 0.88), clamp(foam, 0.0, 0.40));
    }

    // Alpha: see-through at the shore, opaque in deep water, more opaque at
    // grazing angles. This soft waterline is what sells the depth.
    float alpha = baseAlpha * mix(0.12, 1.0, shore);
    alpha = mix(alpha, min(1.0, alpha * 1.25), fres);
    alpha = clamp(alpha, 0.12, 0.96);   // near-opaque body; only the very shoreline stays soft

    // Match the world's distance fog.
    col = mix(col, uFogColor, fog);

    FragColor = vec4(col, alpha);
}
