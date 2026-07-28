#version 330 core

// MSUI Client - M2 doodad fragment shader.
//
// FORKED FROM wmo.frag ON PURPOSE - see doodad.vert. The exterior lighting and
// fog model below is character-for-character the terrain/WMO one, because a
// barrel that lights differently from the ground under it reads as wrong
// without being nameable. What this shader adds, and wmo.frag must never gain,
// is per-instance baked interior light and the M2 "unlit" material flag.
//
// ASCII ONLY - see doodad.vert.

in vec3 vWorldPos;
in vec3 vNormal;
in vec2 vUV;
in vec4 vLight;
in float vAppearStart;

uniform sampler2D uTexture;
uniform int   uHasTexture;

// Per-object appear fade (benilla model_fade.rs). Only the OUTPUT ALPHA is
// touched - never the lighting - so this is purely a reveal, not a colour
// change. Enabled sets the whole feature; uNow is the world clock in seconds;
// uAppearFadeSecs is the ramp length (2.0 in benilla). A fragment fades in as
// alpha = t^3 over uAppearFadeSecs from vAppearStart. vAppearStart <= 0 means
// "already resident" -> fully opaque, which is every instance except one just
// streamed in while the world was on screen.
uniform int   uAppearFadeEnabled;
uniform float uNow;
uniform float uAppearFadeSecs;

// Alpha below which a fragment is thrown away. Set PER BATCH, because it must
// be zero for any texture that has no alpha channel: a BLP without alpha can
// decode with every alpha byte at zero, and a fixed cut then discards geometry
// that loaded perfectly well. Leaves and fence lattices still need a real cut,
// so this cannot simply be removed.
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

// The classic render path's overbright factor, shared with the walls. Vanilla
// authored vertex light in [0, 2], not [0, 1]. This MUST be the same value the
// WMO renderer uses or props stop matching the surfaces they sit on.
uniform float uVertexColorScale;

// M2 material flag 0x01. Lantern glows, fire quads and glow planes are authored
// at full brightness and are supposed to ignore lighting completely - without
// this a lantern inside a dark room goes out, which is the one thing a lantern
// must not do.
uniform int   uUnlit;

// Beyond-portal fill light (see wmo.frag). Weighted by (1 - vLight.a) so it
// lands on interior-baked props and fades out on daylight-dominant ones;
// radius 0 disables it whenever no instance portal is near.
uniform vec3  uPortalLightPos;    // camera-relative, same space as vWorldPos
uniform vec3  uPortalLightColor;  // colour premultiplied by intensity
uniform float uPortalLightRadius; // yards; 0 = off

out vec4 FragColor;

void main()
{
    vec4 albedo = uHasTexture == 1
        ? texture(uTexture, vUV)
        : vec4(0.62, 0.60, 0.56, 1.0);

    if (uAlphaCutoff > 0.0 && albedo.a < uAlphaCutoff) discard;

    vec3 normal = normalize(vNormal);

    // Two-sided materials need the geometric side currently being rasterized,
    // not a normal forced toward the camera. Camera-facing normal flips made a
    // fixed sun appear to move when the camera crossed a tangent plane.
    if (!gl_FrontFacing) normal = -normal;

    float lambert = max(dot(normal, uSunDirection), 0.0);
    vec3 light = uAmbientColor * uAmbientIntensity
        + uSunColor * lambert * uSunIntensity;

    // Vanilla never lit WMO interiors at runtime. Walls take their light from
    // MOCV; the props inside take theirs from MODD.color, which the artist
    // baked per PLACEMENT - the same barrel model is a different colour in a
    // lit corner than in a dark one. Feeding a prop the outdoor sun is why
    // barrels used to glow inside rooms that are correctly dark.
    //
    // No MOHD ambient is added here, deliberately: the wall path does not add
    // it either, and in classic-era data it is already baked into the authored
    // values. Adding it on one side and not the other is what makes props
    // detach from the floor.
    vec3 baked = vLight.rgb * uVertexColorScale;
    vec3 lighting = mix(baked, light, vLight.a);

    // Beyond-portal fill (see the uniform block). Scaled by (1 - vLight.a) so
    // it favours interior-baked props and leaves daylight-lit ones untouched.
    if (uPortalLightRadius > 0.0)
    {
        float pd = distance(uPortalLightPos, vWorldPos);
        float atten = clamp(1.0 - pd / uPortalLightRadius, 0.0, 1.0);
        lighting += uPortalLightColor * (atten * atten) * (1.0 - vLight.a);
    }

    if (uUnlit == 1) lighting = vec3(1.0);

    vec3 lit = albedo.rgb * lighting;

    float dist = distance(uCameraPos, vWorldPos);
    float fog = clamp((dist - uFogStart) / max(uFogEnd - uFogStart, 1.0), 0.0, 1.0);

    // Appear fade: cutout stays keyed on the texture alpha (the discard above),
    // so the silhouette is stable; the surviving fragments fade together as
    // alpha = t^3. Non-fading instances (vAppearStart <= 0) resolve to alpha 1,
    // i.e. exactly the original opaque output.
    float outAlpha = albedo.a;
    if (uAppearFadeEnabled == 1)
    {
        float fade = 1.0;
        if (vAppearStart > 0.0)
        {
            float t = clamp((uNow - vAppearStart) / max(uAppearFadeSecs, 0.0001), 0.0, 1.0);
            fade = t * t * t;
        }
        if (fade <= 0.0) discard;   // invisible AND writes no depth
        outAlpha = fade;
    }

    FragColor = vec4(mix(lit, uFogColor, fog), outAlpha);
}
