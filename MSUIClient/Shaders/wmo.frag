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
in vec4 vColor;


// Command View interior cut (Engine/WorldCut.cs): camera-relative footprint (minX, minY, maxX,
// maxY) and cut height. Fragments inside the footprint and above the height are discarded.
uniform int   uCutActive;
uniform vec4  uCutRect;
uniform float uCutZ;

// Command View line-of-sight cut (Engine/WorldCut.cs): camera-relative segments from the eye
// (origin) to each party member's chest. A fragment inside a tunnel around a segment, nearer
// than the unit, is discarded; the tunnel tapers from uSightRadius.x at the eye to .y at the unit.
uniform int   uSightCount;
uniform vec3  uSightTo[8];
uniform vec2  uSightRadius;
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

// Which MOBA run this batch came from: 1 transparent, 2 interior, 3 exterior.
// See WmoRenderer.Batch.Type. Anything other than 1 or 2 lights by daylight.
uniform int   uBatchType;
uniform int   uUnlit;   // MOMT F_UNLIT: texture brightness, no scene light
uniform vec3  uSidn;    // MOMT F_SIDN emissive × night fraction (zero by day / when clear)

// The classic render path's overbright factor. Blizzard halves MOCV at load
// and doubles it at draw, so the authored range is [0, 2], not [0, 1].
uniform float uVertexColorScale;
uniform float uInteriorBrightness; // scales baked MOCV interior light only

// Beyond-portal fill light. A soft point light dropped just past an instance
// portal so the room seen through the doorway is not pitch black. Radius 0
// disables it (set every frame no portal is near), and it is never applied to
// exterior (daylight) batches, so outdoor lighting stays exactly as it was.
uniform vec3  uPortalLightPos;    // camera-relative, same space as vWorldPos
uniform vec3  uPortalLightColor;  // colour premultiplied by intensity
uniform float uPortalLightRadius; // yards; 0 = off

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

// Per-instance appear fade (benilla model_fade.rs), computed on the CPU and set
// once per building. 1.0 = fully resident (the default and the steady state);
// less than 1 while a just-streamed building eases in. Multiplies the OUTPUT
// alpha only - lighting is untouched. The renderer enables blending for the
// instance only while this is below 1, so opaque buildings are unaffected.
uniform float uAppearAlpha;

// Painterly importance is carried in alpha for steady opaque batches. Blended
// materials and an appearing instance still need their physical source alpha,
// selected per draw by uPreserveAlpha.
uniform float uStyleWeight;
uniform int   uPreserveAlpha;

out vec4 FragColor;

void main()
{
    if (uCutActive == 1 && vWorldPos.z > uCutZ &&
        vWorldPos.x > uCutRect.x && vWorldPos.x < uCutRect.z &&
        vWorldPos.y > uCutRect.y && vWorldPos.y < uCutRect.w) discard;
    for (int i = 0; i < uSightCount; i++)
    {
        vec3 b = uSightTo[i];
        float len2 = max(dot(b, b), 1e-4);
        float t = clamp(dot(vWorldPos, b) / len2, 0.0, 1.0);
        if (t >= 0.985) continue;
        float d = length(vWorldPos - b * t);
        if (d < mix(uSightRadius.x, uSightRadius.y, t)) discard;
    }
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

    // Vanilla interiors are not lit at runtime at all. Their lighting was baked
    // per vertex by the artist into MOCV - lantern pools, the dark back of a
    // mine shaft, the blue cast of Deadmines - and the client only modulates
    // the texture by it. Feeding an interior the outdoor sun is why every room
    // used to read as a brightly lit box with a roof on.
    //
    // vColor.a is NOT opacity here. On transparent batches it is how close the
    // vertex sits to a portal, which is what fades a doorway from baked light
    // to daylight; on interior batches the CPU fixup has already collapsed it
    // to 0 (baked only) or 255 (also take the sun, for exterior-lit groups).
    vec3 baked = vColor.rgb * uVertexColorScale * uInteriorBrightness;
    vec3 lighting;
    if (uBatchType == 1)
        lighting = mix(baked, light, vColor.a);
    else if (uBatchType == 2)
        lighting = mix(baked, light + baked, vColor.a);
    else
        lighting = light;
    lighting += carriedPointLight(normal, vWorldPos);

    // Beyond-portal fill light (see the uniform block). Added into the
    // pre-albedo light term so it brightens the textured surface the way baked
    // light does. Gated off exterior batches (type 3) so daylight is untouched.
    if (uPortalLightRadius > 0.0 && uBatchType != 3)
    {
        float pd = distance(uPortalLightPos, vWorldPos);
        float atten = clamp(1.0 - pd / uPortalLightRadius, 0.0, 1.0);
        lighting += uPortalLightColor * (atten * atten);
    }

    // The window/glass law: SIDN glow is a material EMISSION added inside the lit sum
    // (tex × (lit + sidn·night)) — warm panes overnight, nothing by day; UNLIT draws the
    // texture as authored (lamp heads, glow panes) regardless of the scene light.
    lighting += uSidn;
    if (uUnlit == 1) lighting = vec3(1.0);

    vec3 lit = albedo.rgb * lighting;

    float dist = distance(uCameraPos, vWorldPos);
    float fog = clamp((dist - uFogStart) / max(uFogEnd - uFogStart, 1.0), 0.0, 1.0);

    // Opaque and alpha-key batches draw with blending disabled, so retaining
    // texture alpha is harmless there and required by MOMT blend modes 2+.
    // uAppearAlpha (default 1) scales the whole building down only while it eases
    // in; at 1.0 this is byte-for-byte the original output.
    float naturalAlpha = albedo.a * uAppearAlpha;
    float outAlpha = uPreserveAlpha == 1 ? naturalAlpha : uStyleWeight;
    FragColor = vec4(mix(lit, uFogColor, fog), outAlpha);
}
