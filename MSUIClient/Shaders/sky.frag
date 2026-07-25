#version 330 core

// Vanilla's sky is five authored colour bands stacked from the horizon to the
// zenith (LightIntBand 2..6). This reconstructs the ray direction per pixel and
// picks a colour by ELEVATION, so it is correct under any camera orientation and
// any field of view without a single vertex.
//
// Band order, bottom to top: smog, band2, band1, middle, top.
// The stop heights are NOT authored anywhere we can find - only the colours are.
// They are uniforms with sliders for that reason; see SYSTEM_EXTERIOR_LIGHTING.

in vec2 vNdc;
out vec4 FragColor;

uniform vec3  uForward;
uniform vec3  uRight;
uniform vec3  uUp;
uniform float uTanHalfFov;
uniform float uAspect;

uniform vec3 uSkyTop;
uniform vec3 uSkyMiddle;
uniform vec3 uSkyBand1;
uniform vec3 uSkyBand2;
uniform vec3 uSkySmog;

uniform float uStopMiddle;
uniform float uStopBand1;
uniform float uStopBand2;

// Guards a divide when two stops are dragged onto each other.
float safeSpan(float a, float b) { return max(a - b, 1e-4); }

void main()
{
    vec3 dir = normalize(
        uForward
      + uRight * (vNdc.x * uTanHalfFov * uAspect)
      + uUp    * (vNdc.y * uTanHalfFov));

    // World is Z-up, so elevation is simply the z component: 1 at the zenith,
    // 0 at the horizon, negative below it.
    float e = clamp(dir.z, -1.0, 1.0);

    vec3 c;
    if (e >= uStopMiddle)
        c = mix(uSkyMiddle, uSkyTop, (e - uStopMiddle) / safeSpan(1.0, uStopMiddle));
    else if (e >= uStopBand1)
        c = mix(uSkyBand1, uSkyMiddle, (e - uStopBand1) / safeSpan(uStopMiddle, uStopBand1));
    else if (e >= uStopBand2)
        c = mix(uSkyBand2, uSkyBand1, (e - uStopBand2) / safeSpan(uStopBand1, uStopBand2));
    else if (e >= 0.0)
        c = mix(uSkySmog, uSkyBand2, e / max(uStopBand2, 1e-4));
    else
        // Below the horizon the smog colour continues. Terrain covers this
        // almost everywhere; it shows through on a cliff edge and must not be
        // black, or the world ends in a hard line - the exact failure the flat
        // clear colour was originally chosen to avoid.
        c = uSkySmog;

    FragColor = vec4(c, 1.0);
}
