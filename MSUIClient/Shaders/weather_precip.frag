#version 330 core

in vec2 vUv;
in vec4 vColor;
in float vDistance;

uniform sampler2D uTexture;
uniform int uFogEnabled;
uniform vec3 uFogColor;
uniform float uFogStart;
uniform float uFogEnd;

out vec4 FragColor;

void main()
{
    vec4 sampleColor = texture(uTexture, vUv) * vColor;
    if (sampleColor.a <= 0.0039) discard;
    if (uFogEnabled != 0)
    {
        float fog = clamp((vDistance - uFogStart) / max(uFogEnd - uFogStart, 0.001), 0.0, 1.0);
        sampleColor.rgb = mix(sampleColor.rgb, uFogColor, fog);
    }
    FragColor = sampleColor;
}
