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

out vec4 FragColor;

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

    // ---------------------------------------------------------------
    // Magma / slime: self-luminous flowing surfaces, own path.
    // ---------------------------------------------------------------
    if (vType > 2.5)
    {
        bool magma = vType > 5.5;
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
    float depthFade = 1.0 - exp(-max(vDepth, 0.0) * 0.18);  // 0 shallow -> 1 deep
    float shore     = smoothstep(0.0, 1.2, vDepth);         // 0 at waterline

    vec3 shallowCol, deepCol;
    float baseAlpha;
    if (ocean) { shallowCol=vec3(0.08,0.26,0.42); deepCol=vec3(0.02,0.09,0.22); baseAlpha=0.72; }
    else       { shallowCol=vec3(0.14,0.34,0.44); deepCol=vec3(0.05,0.16,0.30); baseAlpha=0.50; }

    vec3 body = mix(shallowCol, deepCol, depthFade);

    // Diffuse-ish lighting of the body colour.
    vec3 amb = uAmbientColor * uAmbientIntensity;
    vec3 lit = body * (amb + uSunColor * uSunIntensity * NdotL * 0.35);

    // Fresnel: transparent looking straight down, sky-tinted at grazing angles.
    float fres = clamp(pow(1.0 - NdotV, 5.0), 0.0, 1.0);
    vec3 sky = uFogColor;
    vec3 col = mix(lit, sky, fres * (ocean ? 0.55 : 0.40));

    // Moving specular sun sparkle - the clearest sign of motion.
    vec3 H = normalize(L + V);
    float spec = pow(max(dot(N, H), 0.0), 200.0);
    float sparkle = pow(max(fbm(vAbsXY * 4.0 + uTime*0.5, uTime*1.5) - 0.55, 0.0) / 0.45, 3.0);
    col += uSunColor * uSunIntensity * (spec * 1.4 + sparkle * 0.10);

    // Wave-crest brightening.
    col += vec3(smoothstep(0.5, 1.0, vWave) * 0.04);

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
    alpha = mix(alpha, min(1.0, alpha * 1.35), fres);
    alpha = clamp(alpha, 0.10, 0.95);

    // Match the world's distance fog.
    col = mix(col, uFogColor, fog);

    FragColor = vec4(col, alpha);
}
