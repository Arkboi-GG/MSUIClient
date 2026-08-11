#version 330 core

// MSUI Client - ground-effect foliage (grass/flowers) fragment shader.
//
// Same lighting/fog model as the doodad and terrain shaders so grass sits into
// the ground it grows from. Alpha-CUTOUT (discard), not blending, so there is no
// sort order to get wrong - identical approach to wmo.frag. Distance fade thins
// the grass out toward the draw edge by multiplying coverage into the cutout.
//
// ASCII ONLY.

in vec3  vNormal;
in vec2  vUV;
in float vDist;

uniform sampler2D uTexture;
uniform float uAlphaCutoff;

uniform vec3  uSunDirection;   // toward the sun, normalised
uniform vec3  uSunColor;
uniform float uSunIntensity;
uniform vec3  uAmbientColor;
uniform float uAmbientIntensity;
uniform float uFogStart;
uniform float uFogEnd;
uniform vec3  uFogColor;

uniform float uBrightness;
uniform float uFadeStart;      // distance where grass starts thinning
uniform float uFadeEnd;        // distance where grass is gone

out vec4 FragColor;

void main()
{
    vec4 albedo = texture(uTexture, vUV);

    // Distance fade: thin the grass out by folding a fade factor into the
    // alpha-cutout, so it dissolves rather than popping at the edge.
    float fade = clamp((uFadeEnd - vDist) / max(uFadeEnd - uFadeStart, 1.0), 0.0, 1.0);
    float a = albedo.a * fade;
    if (a < uAlphaCutoff) discard;

    vec3 normal = normalize(vNormal);
    if (!gl_FrontFacing) normal = -normal;   // grass is two-sided cards

    float lambert = max(dot(normal, uSunDirection), 0.0);
    vec3  light = uAmbientColor * uAmbientIntensity + uSunColor * lambert * uSunIntensity;
    vec3  lit = albedo.rgb * light * uBrightness;

    float fog = clamp((vDist - uFogStart) / max(uFogEnd - uFogStart, 1.0), 0.0, 1.0);
    // Alpha-cutout grass is opaque after discard. Reserve alpha as a painterly
    // importance value so thousands of cards do not all become hard outlines.
    FragColor = vec4(mix(lit, uFogColor, fog), 0.08);
}
