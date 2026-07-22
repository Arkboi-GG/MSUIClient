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

uniform vec3  uCameraPos;
uniform vec3  uSunDirection;   // points TOWARD the sun, normalised
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
    if (albedo.a < 0.35) discard;

    vec3 normal = normalize(vNormal);

    // Two-sided lighting: WMO interiors and thin panels are often wound away
    // from the viewer, and an unlit black wall reads as a hole in the world.
    if (dot(normal, uCameraPos - vWorldPos) < 0.0) normal = -normal;

    float lambert = max(dot(normal, uSunDirection), 0.0);
    float ambient = 0.45;
    vec3 lit = albedo.rgb * (ambient + 0.65 * lambert);

    float dist = distance(uCameraPos, vWorldPos);
    float fog = clamp((dist - uFogStart) / max(uFogEnd - uFogStart, 1.0), 0.0, 1.0);

    FragColor = vec4(mix(lit, uFogColor, fog), 1.0);
}
