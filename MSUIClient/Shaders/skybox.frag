#version 330 core

// Zone skybox fragment (PLAN_18 Phase 2). Emissive: the texel * the batch's
// authored colour tint, no lighting. Blend mode is set by GL state per batch
// (opaque / alpha-key / additive), matching the M2's render flags.

in vec2 vUv;

uniform sampler2D uTex;
uniform vec4 uColor;      // rgb tint, a = alpha
uniform float uAlphaCut;  // >0 for alpha-key batches

out vec4 frag;

void main()
{
    vec4 t = texture(uTex, vUv);
    if (t.a < uAlphaCut) discard;
    frag = vec4(t.rgb * uColor.rgb, t.a * uColor.a);
}
