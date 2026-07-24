#version 330 core

// MSUI Client - water/liquid vertex shader (stage 2, "look at wowee").
//
// The old shader drew a flat sheet, so water read as a painted line with no
// dimension or motion. This one physically displaces the surface with a stack
// of Gerstner waves (the same technique WoWee uses): each vertex rises, falls,
// and slides slightly along the wave, and the surface normal is derived
// analytically from the wave slopes. That is what gives real, moving relief.
//
// Positions arrive in absolute WoW world space (X north, Y west, Z up) and are
// rendered camera-relative for float precision - identical to terrain.vert.
// There is no coordinate conversion in this client.
//
// The displacement is faded out as the water gets shallow (aDepth -> 0) so the
// waves lie flat at the shoreline instead of climbing up over the beach.
//
// ASCII ONLY. Some GLSL compilers (Intel notably) abort with a bogus
// "pre-mature EOF" on any non-ASCII byte, even inside a comment.

layout (location = 0) in vec3  aPosition;   // absolute WoW world position
layout (location = 1) in float aType;       // liquid type: 1 ocean, 3 slime, 4 river, 6 magma
layout (location = 2) in float aDepth;      // water depth here (surfaceZ - groundZ), yards

uniform mat4  uViewProjection;   // camera.RelativeViewProjection
uniform vec3  uCameraOrigin;     // camera.Position
uniform float uTime;             // seconds, for wave motion

out vec3  vRelPos;    // camera-relative position (length = view distance, for fog)
out vec2  vAbsXY;     // undisplaced absolute world XY, for stable ripple/foam noise
out float vDepth;     // water depth, passed through for shoreline fade
out float vWave;      // raw wave height at this vertex, for crest foam
out vec3  vNormal;    // analytic surface normal (Z-up)
flat out float vType;

// One Gerstner wave contributes vertical + horizontal displacement and a slope.
// Coordinate system: X,Y horizontal, Z up. Accumulate tangent/binormal so the
// normal can be taken as a cross product at the end.
struct Wave { vec3 disp; vec3 tangent; vec3 binormal; float height; };

Wave gerstner(vec2 pos, float amp, float freq, float spd, bool ocean)
{
    Wave r;
    r.disp = vec3(0.0);
    r.tangent = vec3(1.0, 0.0, 0.0);
    r.binormal = vec3(0.0, 1.0, 0.0);
    r.height = 0.0;

    // Six directions spread across many angles so no tiling pattern shows.
    vec2 dirs[6] = vec2[6](
        normalize(vec2( 0.86,  0.51)),
        normalize(vec2(-0.47,  0.88)),
        normalize(vec2( 0.32, -0.95)),
        normalize(vec2(-0.93, -0.37)),
        normalize(vec2( 0.67, -0.29)),
        normalize(vec2(-0.15,  0.74))
    );

    // Per-octave amplitude, frequency, speed and steepness. Ocean is choppier
    // and broader; inland water is gentler but still multi-scale.
    float amps[6];  float freqs[6];  float spds[6];  float steep[6];
    if (ocean) {
        amps[0]=amp*1.0;  amps[1]=amp*0.55; amps[2]=amp*0.30;
        amps[3]=amp*0.18; amps[4]=amp*0.10; amps[5]=amp*0.06;
        freqs[0]=freq*0.7; freqs[1]=freq*1.3; freqs[2]=freq*2.1;
        freqs[3]=freq*3.4; freqs[4]=freq*5.0; freqs[5]=freq*7.5;
        spds[0]=spd*0.8; spds[1]=spd*1.0; spds[2]=spd*1.3;
        spds[3]=spd*1.6; spds[4]=spd*2.0; spds[5]=spd*2.5;
        steep[0]=0.35; steep[1]=0.30; steep[2]=0.25;
        steep[3]=0.20; steep[4]=0.15; steep[5]=0.10;
    } else {
        amps[0]=amp*0.5;  amps[1]=amp*0.25; amps[2]=amp*0.15;
        amps[3]=amp*0.08; amps[4]=amp*0.05; amps[5]=amp*0.03;
        freqs[0]=freq*1.0; freqs[1]=freq*1.8; freqs[2]=freq*3.0;
        freqs[3]=freq*4.5; freqs[4]=freq*7.0; freqs[5]=freq*10.0;
        spds[0]=spd*0.6; spds[1]=spd*0.9; spds[2]=spd*1.2;
        spds[3]=spd*1.5; spds[4]=spd*1.9; spds[5]=spd*2.3;
        steep[0]=0.20; steep[1]=0.18; steep[2]=0.15;
        steep[3]=0.12; steep[4]=0.10; steep[5]=0.08;
    }

    for (int i = 0; i < 6; i++)
    {
        float w = freqs[i];
        float A = amps[i];
        float phi = spds[i] * w;
        float Q = clamp(steep[i] / max(w * A * 6.0, 1e-4), 0.0, 1.0);

        float phase = w * dot(dirs[i], pos) + phi * uTime;
        float s = sin(phase);
        float c = cos(phase);

        r.disp.x += Q * A * dirs[i].x * c;
        r.disp.y += Q * A * dirs[i].y * c;
        r.disp.z += A * s;

        float WA = w * A;
        r.tangent.x  -= Q * dirs[i].x * dirs[i].x * WA * s;
        r.tangent.y  -= Q * dirs[i].x * dirs[i].y * WA * s;
        r.tangent.z  += dirs[i].x * WA * c;

        r.binormal.x -= Q * dirs[i].x * dirs[i].y * WA * s;
        r.binormal.y -= Q * dirs[i].y * dirs[i].y * WA * s;
        r.binormal.z += dirs[i].y * WA * c;

        r.height += A * s;
    }
    return r;
}

void main()
{
    bool magmaOrSlime = aType > 2.5;
    bool ocean = (aType > 0.5 && aType < 1.5);

    // Gentle, viscous undulation for magma/slime; wind-driven waves for water.
    float amp  = magmaOrSlime ? 0.18 : (ocean ? 0.30 : 0.11);
    float freq = magmaOrSlime ? 0.20 : (ocean ? 0.20 : 0.32);
    float spd  = magmaOrSlime ? 0.55 : (ocean ? 1.20 : 1.40);

    Wave wv = gerstner(aPosition.xy, amp, freq, spd, ocean);

    // Flatten the waves as the water gets shallow so nothing rises over the
    // shoreline. Fully flat right at the waterline, full height by ~1.2 yd deep.
    float shore = smoothstep(0.0, 1.2, aDepth);

    vec3 world = aPosition;
    world.x += wv.disp.x * shore;
    world.y += wv.disp.y * shore;
    world.z += wv.disp.z * shore;

    vec3 n = normalize(cross(wv.binormal, wv.tangent));
    // Ensure it points up (Z+); cross order can flip near flat regions.
    if (n.z < 0.0) n = -n;
    vNormal = normalize(mix(vec3(0.0, 0.0, 1.0), n, shore));

    vec3 rel = world - uCameraOrigin;
    vRelPos = rel;
    vAbsXY  = aPosition.xy;
    vDepth  = aDepth;
    vWave   = wv.height * shore;
    vType   = aType;

    gl_Position = uViewProjection * vec4(rel, 1.0);
}
