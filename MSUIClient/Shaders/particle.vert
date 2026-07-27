#version 330 core

// M2 particle billboard (PLAN_14 stage 2).
//
// The quad is expanded here rather than on the CPU: four corners, one instance
// per particle, so the per-particle upload is 32 bytes instead of six full
// vertices. uRight/uUp come from the camera, so every sprite faces it.
//
// Positions are CAMERA-RELATIVE, matching the rest of this renderer: the world
// is large and float32 loses precision at ten thousand yards, which shows up as
// sprites jittering against the geometry they sit on.

layout(location = 0) in vec2 aCorner;      // quad corners, -1 .. 1 (vertex at centre +/- aSize)
layout(location = 1) in vec3 aCentre;      // world position
layout(location = 2) in float aSize;       // yards
layout(location = 3) in vec4 aColour;

uniform mat4 uViewProjection;
uniform vec3 uCameraOrigin;
uniform vec3 uRight;
uniform vec3 uUp;

out vec2 vUv;
out vec4 vColour;

void main()
{
    vec3 rel = aCentre - uCameraOrigin;
    vec3 offset = (uRight * aCorner.x + uUp * aCorner.y) * aSize;

    vUv = aCorner * 0.5 + vec2(0.5);
    vColour = aColour;

    gl_Position = uViewProjection * vec4(rel + offset, 1.0);
}
