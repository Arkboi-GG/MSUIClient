#version 330 core

// MSUI Client - terrain fragment shader.
//
// Reproduces vanilla 4-layer splat: a base texture with up to three overlays
// blended by per-chunk alpha masks. A tile's whole tileset lives in one array
// texture and all 256 of its masks in another, so a tile draws in one call.
//
// ASCII ONLY - see terrain.vert.

in vec3 vWorldPos;
in vec3 vNormal;
in vec2 vChunkUV;                    // 0..1 across this fragment's own MCNK
flat in vec4 vLayers;
flat in float vAlphaLayer;           // this chunk's layer in the alpha array

uniform sampler2DArray uTileset;     // unit 0 - the tile's MTEX textures
uniform sampler2DArray uAlphaArray;  // unit 1 - one 64x64 mask per chunk

uniform vec3  uCameraPos;
uniform vec3  uSunDirection;         // points TOWARD the sun, normalised
uniform vec3  uSunColor;
uniform float uSunIntensity;
uniform vec3  uAmbientColor;
uniform float uAmbientIntensity;
uniform float uFogStart;
uniform float uFogEnd;
uniform vec3  uFogColor;
uniform float uTextureScale;         // texture repeats per chunk; vanilla ~8
uniform int   uDebugMode;            // 0 textured, 1 normals, 2 UVs, 3 flat, 4 splat, 5 untextured

out vec4 FragColor;

const vec3 UP = vec3(0.0, 0.0, 1.0);

// Explicit-gradient sample, so a layer can be skipped inside divergent control
// flow without the undefined implicit-LOD that plain texture() would have there.
// The gradients are computed once, outside every branch.
vec3 sampleLayerGrad(float index, vec2 uv, vec2 ddx, vec2 ddy)
{
    return textureGrad(uTileset, vec3(uv, index), ddx, ddy).rgb;
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
    if (uDebugMode == 2) { FragColor = vec4(vChunkUV, 0.0, 1.0); return; }
    if (uDebugMode == 3) { FragColor = vec4(0.62, 0.60, 0.55, 1.0); return; }

    // -- NO WRAP ANYWHERE IN THIS SHADER, AND THAT IS THE WHOLE POINT ---------
    //
    // Both of these lines used to wrap a tile-wide UV with fract(), and each
    // wrap was drawing its own artifact on every chunk boundary in the world.
    //
    // The tileset lookup was fract(vTileUV * 16) * uTextureScale. fract jumps
    // 1 -> 0 exactly where vTileUV * 16 crosses an integer, which is exactly the
    // chunk boundary set - and after the scale that is a jump of 8 whole texture
    // repeats, INSIDE a triangle. Fragment LOD comes from derivatives taken
    // across a 2x2 quad including helper lanes outside the triangle, so at each
    // chunk edge the derivative spiked to ~8 UV units per pixel, LOD came out
    // around 11, and the hardware sampled the deepest mip it had: the texture's
    // flat average, which for ground tilesets is dark mud. A one-pixel dark grid
    // over the world, at every distance, made worse by anisotropy because aniso
    // takes its footprint from the same broken derivative.
    //
    // The alpha lookup was worse. All 256 masks were packed edge to edge in one
    // 1024x1024 atlas, so a chunk boundary landed on an integer texel coordinate
    // and the bilinear tap there returned a 50/50 blend of two DIFFERENT chunks'
    // blend weights - applied to this chunk's textures, which are usually a
    // different set entirely. About a yard of wrong-texture smear per edge.
    //
    // Both are gone by construction now rather than by correction:
    //
    //   - vChunkUV is already 0..1 within this fragment's own MCNK, so tiling is
    //     a plain multiply and GL_REPEAT does the wrapping in the address unit,
    //     per tap. The UV is linear again, so the gradients below are real and
    //     the seam has nowhere to come from.
    //
    //   - the masks are an array texture with one layer per chunk, so a
    //     neighbour's texels are not addressable at any UV. CLAMP_TO_EDGE now
    //     means what it says, and no inset is needed.
    vec2 texUV = vChunkUV * uTextureScale;
    vec3 splat = texture(uAlphaArray, vec3(vChunkUV, vAlphaLayer)).rgb;

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
        //
        // THE OVERLAYS ARE SKIPPED WHERE THEY CONTRIBUTE NOTHING. This used to
        // sample all four layers unconditionally and mix three of them by a
        // weight that is very often exactly zero: a typical vanilla chunk
        // authors two or three layers, and even where four exist, most of a
        // chunk's area is one of them. Four array fetches per pixel over a
        // full-screen surface, amplified by anisotropy, is the second largest
        // per-pixel cost in the client.
        //
        // Two guards, and they are different in kind. vLayers is a flat varying,
        // so the index test is per-primitive and free. The splat weight is a
        // texture read, so that test is divergent within a quad - which is why
        // the fetches use explicit gradients.
        vec2 ddx = dFdx(texUV);
        vec2 ddy = dFdy(texUV);

        albedo = sampleLayerGrad(vLayers.x, texUV, ddx, ddy);

        if (vLayers.y >= 0.0 && splat.r > 0.0)
            albedo = mix(albedo, sampleLayerGrad(vLayers.y, texUV, ddx, ddy), splat.r);
        if (vLayers.z >= 0.0 && splat.g > 0.0)
            albedo = mix(albedo, sampleLayerGrad(vLayers.z, texUV, ddx, ddy), splat.g);
        if (vLayers.w >= 0.0 && splat.b > 0.0)
            albedo = mix(albedo, sampleLayerGrad(vLayers.w, texUV, ddx, ddy), splat.b);
    }

    // uSunDirection arrives normalised from the CPU - see TerrainRenderer.
    float ndl     = max(dot(n, uSunDirection), 0.0);
    vec3 sun = uSunColor * ndl * uSunIntensity;
    vec3 ambient = uAmbientColor * uAmbientIntensity
        * mix(0.62, 1.0, n.z * 0.5 + 0.5);

    vec3 color = albedo * (sun + ambient);

    // Aerial perspective. Cheap here, and the hook the painterly mode extends.
    float dist = length(vWorldPos - uCameraPos);
    float fog  = clamp((dist - uFogStart) / max(uFogEnd - uFogStart, 1.0), 0.0, 1.0);
    color = mix(color, uFogColor, fog);

    FragColor = vec4(color, 1.0);
}
