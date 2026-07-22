#version 330 core

// MSUI Client - terrain fragment shader.
//
// Reproduces vanilla 4-layer splat: a base texture with up to three overlays
// blended by per-chunk alpha masks. All of a tile's tileset lives in one array
// texture and all its masks in one atlas, so a tile draws in a single call.
//
// ASCII ONLY - see terrain.vert.

in vec3 vWorldPos;
in vec3 vNormal;
in vec2 vTileUV;
flat in vec4 vLayers;

uniform sampler2DArray uTileset;     // unit 0 - the tile's MTEX textures
uniform sampler2D      uAlphaAtlas;  // unit 1 - 16x16 chunks of 64x64 masks

uniform vec3  uCameraPos;
uniform vec3  uSunDirection;         // points TOWARD the sun, normalised
uniform float uFogStart;
uniform float uFogEnd;
uniform vec3  uFogColor;
uniform float uTextureScale;         // texture repeats per chunk; vanilla ~8
uniform int   uDebugMode;            // 0 textured, 1 normals, 2 UVs, 3 flat, 4 splat, 5 untextured

out vec4 FragColor;

const vec3 UP = vec3(0.0, 0.0, 1.0);
const float CHUNKS = 16.0;

vec4 sampleLayer(float index, vec2 uv)
{
    if (index < 0.0) return vec4(0.0);
    return texture(uTileset, vec3(uv, index));
}

// Slope/altitude palette - the untextured fallback, and what debug mode 5 shows.
vec3 proceduralAlbedo(vec3 n)
{
    float slope = clamp(dot(n, UP), 0.0, 1.0);
    vec3 grass = vec3(0.34, 0.44, 0.22);
    vec3 dirt  = vec3(0.45, 0.36, 0.24);
    vec3 rock  = vec3(0.42, 0.41, 0.39);
    vec3 c = mix(rock, dirt, smoothstep(0.55, 0.80, slope));
    return mix(c, grass, smoothstep(0.80, 0.94, slope));
}

void main()
{
    vec3 n = normalize(vNormal);

    if (uDebugMode == 1) { FragColor = vec4(n * 0.5 + 0.5, 1.0); return; }
    if (uDebugMode == 2) { FragColor = vec4(fract(vTileUV * CHUNKS), 0.0, 1.0); return; }
    if (uDebugMode == 3) { FragColor = vec4(0.62, 0.60, 0.55, 1.0); return; }

    // Tileset UVs repeat within each chunk; the alpha atlas is sampled with the
    // tile-wide UV directly, so its texels line up with chunk boundaries.
    vec2 texUV = fract(vTileUV * CHUNKS) * uTextureScale;
    vec3 splat = texture(uAlphaAtlas, vTileUV).rgb;

    if (uDebugMode == 4) { FragColor = vec4(splat, 1.0); return; }

    vec3 albedo;
    if (uDebugMode == 5 || vLayers.x < 0.0)
    {
        albedo = proceduralAlbedo(n);
    }
    else
    {
        // Base layer, then overlays in order. Each overlay covers what is under
        // it by its own alpha - the same paint-on-top order the client uses.
        albedo = sampleLayer(vLayers.x, texUV).rgb;
        albedo = mix(albedo, sampleLayer(vLayers.y, texUV).rgb, vLayers.y >= 0.0 ? splat.r : 0.0);
        albedo = mix(albedo, sampleLayer(vLayers.z, texUV).rgb, vLayers.z >= 0.0 ? splat.g : 0.0);
        albedo = mix(albedo, sampleLayer(vLayers.w, texUV).rgb, vLayers.w >= 0.0 ? splat.b : 0.0);
    }

    float ndl     = max(dot(n, normalize(uSunDirection)), 0.0);
    vec3  sun     = vec3(1.00, 0.95, 0.85) * ndl * 1.15;
    vec3  ambient = mix(vec3(0.20, 0.19, 0.16),
                        vec3(0.42, 0.50, 0.60),
                        n.z * 0.5 + 0.5) * 0.85;

    vec3 color = albedo * (sun + ambient);

    // Aerial perspective. Cheap here, and the hook the painterly mode extends.
    float dist = length(vWorldPos - uCameraPos);
    float fog  = clamp((dist - uFogStart) / max(uFogEnd - uFogStart, 1.0), 0.0, 1.0);
    color = mix(color, uFogColor, fog);

    FragColor = vec4(color, 1.0);
}
