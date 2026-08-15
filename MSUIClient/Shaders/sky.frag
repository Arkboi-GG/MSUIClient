#version 330 core

// Vanilla's sky is five authored colour bands stacked from the horizon to the
// zenith (LightIntBand 2..6). This reconstructs the ray direction per pixel and
// picks a colour by ELEVATION, so it is correct under any camera orientation and
// any field of view without a single vertex.
//
// Band order, bottom to top: smog, band2, band1, middle, top.
// The stop heights are NOT authored anywhere we can find - only the colours are.
// They are uniforms with sliders for that reason; see SYSTEM_EXTERIOR_LIGHTING.

in vec2 vNdc;
out vec4 FragColor;

uniform vec3  uForward;
uniform vec3  uRight;
uniform vec3  uUp;
uniform float uTanHalfFov;
uniform float uAspect;

uniform vec3 uSkyTop;
uniform vec3 uSkyMiddle;
uniform vec3 uSkyBand1;
uniform vec3 uSkyBand2;
uniform vec3 uSkySmog;

uniform float uStopMiddle;
uniform float uStopBand1;
uniform float uStopBand2;

// The procedural cloud layer (PLAN_18). uCloudTex is the CloudField's colored
// 128x128 tile (RGB = the reference's byte gradient+glow, A = coverage), sampled
// by the azimuthal projection of the view ray - the exact CPU projection ported
// from CloudField.ProjectCells so the drawn cloud and the CPU sun-glow co-locate.
uniform int uCloudEnabled;
uniform sampler2D uCloudTex;

// Guards a divide when two stops are dragged onto each other.
float safeSpan(float a, float b) { return max(a - b, 1e-4); }

// CloudField.SkyProject, in GLSL (the two MUST match so the sun-glow lands under
// the sun). `d` is the view ray in the tile frame (+Y up): tile.x = world.x,
// tile.y = world.z (up), tile.z = world.y. Azimuthal-equidistant: zenith -> tile
// centre, horizon -> radius 0.5. Returns the tile UV and, in .z, the radius.
vec3 cloudProject(vec3 d)
{
    const float HALF_PI = 1.57079633;
    float colat = acos(clamp(d.y, -1.0, 1.0));           // 0 zenith .. pi/2 horizon
    float r = 0.5 * min(colat, HALF_PI) / HALF_PI;
    float horiz = length(d.xz);
    vec2 n = horiz > 1e-5 ? d.xz / horiz : vec2(0.0);
    return vec3(n * r + 0.5, r);
}

void main()
{
    vec3 dir = normalize(
        uForward
      + uRight * (vNdc.x * uTanHalfFov * uAspect)
      + uUp    * (vNdc.y * uTanHalfFov));

    // World is Z-up, so elevation is simply the z component: 1 at the zenith,
    // 0 at the horizon, negative below it.
    float e = clamp(dir.z, -1.0, 1.0);

    vec3 c;
    if (e >= uStopMiddle)
        c = mix(uSkyMiddle, uSkyTop, (e - uStopMiddle) / safeSpan(1.0, uStopMiddle));
    else if (e >= uStopBand1)
        c = mix(uSkyBand1, uSkyMiddle, (e - uStopBand1) / safeSpan(uStopMiddle, uStopBand1));
    else if (e >= uStopBand2)
        c = mix(uSkyBand2, uSkyBand1, (e - uStopBand2) / safeSpan(uStopBand1, uStopBand2));
    else if (e >= 0.0)
        c = mix(uSkySmog, uSkyBand2, e / max(uStopBand2, 1e-4));
    else
        // Below the horizon the smog colour continues. Terrain covers this
        // almost everywhere; it shows through on a cliff edge and must not be
        // black, or the world ends in a hard line - the exact failure the flat
        // clear colour was originally chosen to avoid.
        c = uSkySmog;

    // Cloud layer over the gradient. The tile maps the whole visible sky into a
    // disk (centre = zenith, rim = the 45deg-shifted horizon); the rim fade
    // (benilla's per-ring dome alphas) keeps clouds from cutting a hard edge at
    // the horizon. Straight-alpha "over": the tile RGB is the reference's colour.
    if (uCloudEnabled == 1)
    {
        vec3 td = vec3(dir.x, dir.z, dir.y);            // world Z-up -> tile Y-up
        vec3 pr = cloudProject(td);
        vec4 cloud = texture(uCloudTex, pr.xy);
        float rimFade = 1.0 - smoothstep(0.44, 0.5, pr.z);   // fade the horizon edge
        c = mix(c, cloud.rgb, clamp(cloud.a * rimFade, 0.0, 1.0));
    }

    FragColor = vec4(c, 1.0);
}
