#version 330 core

// MSUI Client - WMO (building) fragment shader.
//
// Deliberately the same lighting and fog model as terrain.frag: buildings that
// light differently from the ground they stand on look wrong in a way that is
// hard to name and easy to avoid.
//
// ASCII ONLY - see wmo.vert.

in vec3 vWorldPos;
in vec3 vNormal;
in vec2 vUV;

uniform sampler2D uTexture;
uniform int   uHasTexture;

// Alpha below which a fragment is thrown away. Set PER BATCH, because it must
// be zero for any texture that has no alpha channel: a BLP without alpha can
// decode with every alpha byte at zero, and a fixed cut then discards a wall
// that loaded perfectly well. Railings and window tracery still need a real
// cut, so this cannot simply be removed.
uniform float uAlphaCutoff;

uniform vec3  uCameraPos;
uniform vec3  uSunDirection;   // points TOWARD the sun, normalised
uniform vec3  uSunColor;
uniform float uSunIntensity;
uniform vec3  uAmbientColor;
uniform float uAmbientIntensity;
uniform float uFogStart;
uniform float uFogEnd;
uniform vec3  uFogColor;

out vec4 FragColor;

void main()
{
    vec4 albedo = uHasTexture == 1
        ? texture(uTexture, vUV)
        : vec4(0.62, 0.60, 0.56, 1.0);

    // Cut fully transparent texels rather than blending them. Vanilla WMO
    // materials lean on alpha for railings, lattices and window frames, and
    // discarding avoids needing a sorted transparent pass to look right.
    if (uAlphaCutoff > 0.0 && albedo.a < uAlphaCutoff) discard;

    vec3 normal = normalize(vNormal);

    // Two-sided materials need the geometric side currently being rasterized,
    // not a normal forced toward the orbit camera. Camera-facing normal flips
    // made a fixed sun appear to move whenever the camera crossed a surface's
    // tangent plane.
    if (!gl_FrontFacing) normal = -normal;

    float lambert = max(dot(normal, uSunDirection), 0.0);
    vec3 light = uAmbientColor * uAmbientIntensity
        + uSunColor * lambert * uSunIntensity;
    vec3 lit = albedo.rgb * light;

    float dist = distance(uCameraPos, vWorldPos);
    float fog = clamp((dist - uFogStart) / max(uFogEnd - uFogStart, 1.0), 0.0, 1.0);

    // Opaque and alpha-key batches draw with blending disabled, so retaining
    // texture alpha is harmless there and required by MOMT blend modes 2+.
    FragColor = vec4(mix(lit, uFogColor, fog), albedo.a);
}
