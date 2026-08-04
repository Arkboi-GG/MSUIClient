#version 330 core

// Spell particles carry their per-quad axes explicitly. This supports ordinary camera
// billboards, authored XY planes, spun heads, and velocity streaks in one instanced path.
layout(location = 0) in vec2 aCorner;
layout(location = 1) in vec3 aCentre;
layout(location = 2) in vec3 aAxisRight;
layout(location = 3) in vec3 aAxisUp;
layout(location = 4) in vec4 aColour;
layout(location = 5) in vec4 aCellRect;
layout(location = 6) in float aUvMode;

uniform mat4 uViewProjection;
uniform mat4 uView;
uniform vec3 uCameraOrigin;

out vec2 vUv;
out vec4 vColour;
out float vEyeDepth;

void main()
{
    vec3 rel = aCentre - uCameraOrigin;
    vec3 offset = aAxisRight * aCorner.x + aAxisUp * aCorner.y;
    vec2 localUv = aCorner * 0.5 + vec2(0.5);
    // Tail strips run U from the particle head to the streak tip and V across width.
    if (aUvMode > 0.5)
        localUv = vec2(localUv.y, 1.0 - localUv.x);
    vUv = aCellRect.xy + localUv * aCellRect.zw;
    vColour = aColour;
    vec3 vertex = rel + offset;
    vEyeDepth = -(uView * vec4(vertex, 1.0)).z;
    gl_Position = uViewProjection * vec4(vertex, 1.0);
}
