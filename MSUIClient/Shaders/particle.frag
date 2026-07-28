#version 330 core

// The sprite is the texture times the ramp colour. Nothing is lit: every
// emitter in the sample carries the unlit flag, and a particle that took the
// world's lighting would go dark at night exactly when a torch should not.

in vec2 vUv;
in vec4 vColour;

uniform sampler2D uTexture;

// Mip LOD bias for the sprite texture. 0 = full trilinear (softest; blurs a
// shrinking converging speck into vapour); negative pulls toward the sharp base
// level so each particle reads as a distinct speck again. Set per draw group -
// portal (model-space) sprites use the knob, other effects stay at 0.
uniform float uMipBias;

out vec4 FragColor;

void main()
{
    vec4 texel = texture(uTexture, vUv, uMipBias);
    vec4 c = texel * vColour;

    // Fully transparent fragments still cost a blend. Additive sprites overlap
    // heavily - a portal is ~800 of them in a small volume - so this is worth
    // the branch.
    if (c.a <= 0.003) discard;

    FragColor = c;
}
