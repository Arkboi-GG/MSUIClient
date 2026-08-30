#version 330 core

// MSUI Client - character fragment shader.
//
// This began as wmo.frag with character alpha preservation. Character models
// also use the legacy Model2 directional-light response below; applying the
// WMO hard-Lambert response to them makes side-facing bodies much too dark.
//
// wmo.frag ends with `FragColor = vec4(mix(lit, uFogColor, fog), 1.0)`, which
// is right for a building - every wall is opaque and anything cut out was
// already discarded. It is wrong for a character, because a character has
// genuinely BLENDED geometry: hair cards, eyelashes, eye glow. Forcing alpha to
// one means the blend equation has nothing to work with, so those surfaces
// draw solid and fight whatever is behind them.
//
// ASCII ONLY - see character.vert.

in vec3 vWorldPos;
in vec3 vNormal;
in vec2 vUV;

uniform sampler2D uTexture;
uniform int   uHasTexture;

// Alpha below which a fragment is thrown away. Set PER BATCH: zero for a
// texture with no alpha channel, a real cut for alpha-keyed geometry, and zero
// again for blended geometry, which composites rather than cutting.
uniform float uAlphaCutoff;

uniform vec3  uCameraPos;
uniform vec3  uSunDirection;   // points TOWARD the sun, normalised
uniform vec3  uSunColor;
uniform float uSunIntensity;
uniform vec3  uAmbientColor;
uniform float uAmbientIntensity;
uniform float uShadowWrap;      // nonzero only for the separately tuned glue-booth preview
uniform float uFogStart;
uniform float uFogEnd;
uniform vec3  uFogColor;
uniform float uBodyAlpha;
uniform vec3  uBodyTint;
uniform int   uUnlit;
// 0 scene fog; 1 black (additive); 2 white (modulate); 3 neutral gray (2x);
// 4 explicitly unfogged.
uniform int   uFogPolicy;

uniform int uPointLightCount;
uniform vec3 uPointLightPos[8];
uniform vec3 uPointLightColor[8];

vec3 carriedPointLight(vec3 normal, vec3 worldPos)
{
    float d0 = 1e30, d1 = 1e30, d2 = 1e30;
    vec3 v0 = vec3(0.0), v1 = vec3(0.0), v2 = vec3(0.0);
    vec3 c0 = vec3(0.0), c1 = vec3(0.0), c2 = vec3(0.0);
    for (int i = 0; i < 8; i++)
    {
        if (i >= uPointLightCount) break;
        vec3 delta = uPointLightPos[i] - worldPos;
        float ds = dot(delta, delta);
        if (ds < d0) { d2=d1; v2=v1; c2=c1; d1=d0; v1=v0; c1=c0; d0=ds; v0=delta; c0=uPointLightColor[i]; }
        else if (ds < d1) { d2=d1; v2=v1; c2=c1; d1=ds; v1=delta; c1=uPointLightColor[i]; }
        else if (ds < d2) { d2=ds; v2=delta; c2=uPointLightColor[i]; }
    }
    vec3 sum = vec3(0.0);
    if (d0 < 1e29) { float d=sqrt(d0); sum += c0 * max(dot(normal, v0/max(d,0.001)),0.0) / max(0.7*d + 0.03*d*d, 0.001); }
    if (d1 < 1e29) { float d=sqrt(d1); sum += c1 * max(dot(normal, v1/max(d,0.001)),0.0) / max(0.7*d + 0.03*d*d, 0.001); }
    if (d2 < 1e29) { float d=sqrt(d2); sum += c2 * max(dot(normal, v2/max(d,0.001)),0.0) / max(0.7*d + 0.03*d*d, 0.001); }
    return sum;
}

// WoW's Model2 light is the low-order spherical-harmonic response used by the
// 1.12 reference renderer, not max(N.L, 0). It preserves the lit-face peak but
// keeps room-coloured directional light on surfaces around the terminator.
float model2SunResponse(float mu)
{
    return (4.0 / 17.0) * (0.375 + 2.0 * mu + 1.875 * mu * mu);
}

const float WorldModelSelfFill = 0.25;

out vec4 FragColor;

void main()
{
    vec4 albedo = uHasTexture == 1
        ? texture(uTexture, vUV)
        : vec4(0.62, 0.60, 0.56, 1.0);

    if (uAlphaCutoff > 0.0 && albedo.a < uAlphaCutoff) discard;

    vec3 normal = normalize(vNormal);

    // Use the rasterized face side. Flipping toward the orbit camera makes a
    // fixed directional light change as the camera moves around the model.
    if (!gl_FrontFacing) normal = -normal;

    float mu = dot(normal, uSunDirection);
    // Keep the signed-off glue-booth wrap independent. In-world characters and
    // their attached equipment set zero and follow the Model2 response.
    float sunResponse = uShadowWrap > 0.0001
        ? clamp((mu + uShadowWrap) / (1.0 + uShadowWrap), 0.0, 1.0)
        : model2SunResponse(mu);
    vec3 light = vec3(1.0);
    if (uUnlit == 0)
    {
        light = uAmbientColor * uAmbientIntensity
            + uSunColor * sunResponse * uSunIntensity;
        // The 1.12 model path carries a base contribution independent of the
        // room surface exposure. Keep that practical separation here so a
        // readable body does not require overexposing the surrounding WMO.
        if (uShadowWrap <= 0.0001)
            light += vec3(WorldModelSelfFill);
        light += carriedPointLight(normal, vWorldPos);
        light = max(light, vec3(0.0));
    }
    vec3 lit = albedo.rgb * uBodyTint * light;

    float dist = distance(uCameraPos, vWorldPos);
    float fog = uFogPolicy == 4 ? 0.0
        : clamp((dist - uFogStart) / max(uFogEnd - uFogStart, 1.0), 0.0, 1.0);
    vec3 fogTarget = uFogColor;
    if (uFogPolicy == 1) fogTarget = vec3(0.0);
    else if (uFogPolicy == 2) fogTarget = vec3(1.0);
    else if (uFogPolicy == 3) fogTarget = vec3(0.50196078);

    // The one divergence from wmo.frag. Opaque batches carry alpha 1 anyway, so
    // this costs the opaque pass nothing and gives the blended pass something
    // to composite with.
    FragColor = vec4(mix(lit, fogTarget, fog), albedo.a * uBodyAlpha);
}
