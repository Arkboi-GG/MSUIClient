#version 330 core

in vec2 vUv;
in vec4 vColour;
in float vEyeDepth;

uniform sampler2D uTexture;
uniform float uMipBias;
uniform int uFogEnabled;
uniform int uFogPolicy; // 0 off, 1 scene colour, 2 black
uniform vec3 uFogColor;
uniform float uFogStart;
uniform float uFogEnd;
uniform float uFarClip; // <= 0 disables the hard wall

out vec4 FragColor;

void main()
{
    if (uFarClip > 0.0 && vEyeDepth > uFarClip)
        discard;

    vec4 c = texture(uTexture, vUv, uMipBias) * vColour;

    if (uFogEnabled != 0 && uFogPolicy != 0)
    {
        float visibility = clamp((uFogEnd - vEyeDepth) /
            max(0.001, uFogEnd - uFogStart), 0.0, 1.0);
        vec3 target = uFogPolicy == 2 ? vec3(0.0) : uFogColor;
        c.rgb = mix(target, c.rgb, visibility);
    }

    FragColor = c;
}
