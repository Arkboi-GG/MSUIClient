#version 330 core

// MSUI Client - character fragment shader.
//
// THIS IS wmo.frag WITH ONE CHANGE, and the change is the whole reason it
// exists: the final alpha is the texture's alpha instead of a hardcoded 1.0.
//
// wmo.frag ends with `FragColor = vec4(mix(lit, uFogColor, fog), 1.0)`, which
// is right for a building - every wall is opaque and anything cut out was
// already discarded. It is wrong for a character, because a character has
// genuinely BLENDED geometry: hair cards, eyelashes, eye glow. Forcing alpha to
// one means the blend equation has nothing to work with, so those surfaces
// draw solid and fight whatever is behind them.
//
// The lighting and fog are byte-identical to wmo.frag on purpose. A character
// that lights differently from the ground he stands on looks wrong in a way
// that is hard to name and easy to avoid, and that promise is worth more than
// having one fewer file. If wmo.frag's lighting changes, change it here too.
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
uniform float uShadowWrap;      // 0 = hard Lambert terminator; up to 1 = light wraps around (soft shadow)
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

    // Wrap lighting softens the terminator: uShadowWrap 0 keeps a hard Lambert edge; higher values
    // let the key wrap past 90 degrees so the shadow side lifts and the boundary blurs. Booth tuning
    // only - the in-world character sets 0 (uShadowWrap default), so its shading is unchanged.
    float ndl = dot(normal, uSunDirection);
    float lambert = clamp((ndl + uShadowWrap) / (1.0 + uShadowWrap), 0.0, 1.0);
    vec3 light = vec3(1.0);
    if (uUnlit == 0)
    {
        light = uAmbientColor * uAmbientIntensity
            + uSunColor * lambert * uSunIntensity;
        light += carriedPointLight(normal, vWorldPos);
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
