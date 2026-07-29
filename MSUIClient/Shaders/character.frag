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
    vec3 light = uAmbientColor * uAmbientIntensity
        + uSunColor * lambert * uSunIntensity;
    vec3 lit = albedo.rgb * light;

    float dist = distance(uCameraPos, vWorldPos);
    float fog = clamp((dist - uFogStart) / max(uFogEnd - uFogStart, 1.0), 0.0, 1.0);

    // The one divergence from wmo.frag. Opaque batches carry alpha 1 anyway, so
    // this costs the opaque pass nothing and gives the blended pass something
    // to composite with.
    FragColor = vec4(mix(lit, uFogColor, fog), albedo.a);
}
