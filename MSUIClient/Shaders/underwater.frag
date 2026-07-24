#version 330 core

// MSUI Client - underwater overlay fragment shader.
//
// Tints the whole screen with the liquid colour when the camera is submerged.
// The opacity grows with how deep the eye is (uSubmersion, in yards) and there
// is a soft darkened vignette plus a slow caustic wobble so it reads as being
// under moving water rather than behind a flat coloured pane.
//
// ASCII ONLY.

in vec2 vUV;                 // 0..1 across the screen

uniform vec3  uTint;         // liquid colour (blue water, green slime, ...)
uniform float uSubmersion;   // yards the eye is below the surface
uniform float uTime;

out vec4 FragColor;

float hash(vec2 p){ return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float noise(vec2 p)
{
    vec2 i = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash(i), b = hash(i + vec2(1,0));
    float c = hash(i + vec2(0,1)), d = hash(i + vec2(1,1));
    return mix(mix(a,b,f.x), mix(c,d,f.x), f.y);
}

void main()
{
    // Denser tint the deeper you go, easing toward a cap.
    float depth = clamp(uSubmersion / 6.0, 0.0, 1.0);
    float alpha = mix(0.35, 0.82, depth);

    // Slow caustic shimmer.
    float caustic = noise(vUV * 6.0 + vec2(uTime * 0.25, uTime * 0.18))
                  + noise(vUV * 12.0 - vec2(uTime * 0.15, uTime * 0.22)) * 0.5;
    vec3 col = uTint * (0.75 + 0.25 * caustic);

    // Darkened edges - light falls off away from screen centre underwater.
    float vign = smoothstep(1.15, 0.25, length(vUV - 0.5) * 1.4);
    col *= mix(0.55, 1.0, vign);

    FragColor = vec4(col, alpha);
}
