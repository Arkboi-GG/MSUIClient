#version 330 core

// MSUI Client - ground-effect foliage (grass/flowers) vertex shader.
//
// Instanced, exactly like the doodad path (wmo.vert): a mat4 per instance
// arrives as four vec4 attributes at locations 3..6, rebuilt as a GLSL mat4
// (columns = System.Numerics rows) so model * vec4(pos,1) matches
// Vector3.Transform on the CPU. Positions are camera-relative for float
// precision (the instance translation has the camera subtracted before upload),
// so uViewProjection is camera.RelativeViewProjection.
//
// The blade sways in the wind: the top bends and the base stays planted. M2
// vertices are Y-up in model space, so aPosition.y is the height up the blade;
// the horizontal offset grows with height^2 and is driven by a per-position
// phase using ABSOLUTE world XY (world + uCameraOrigin) so the field doesn't
// visibly pulse as the camera moves.
//
// ASCII ONLY.

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aNormal;
layout (location = 2) in vec2 aUV;
layout (location = 3) in vec4 aInstanceRow0;
layout (location = 4) in vec4 aInstanceRow1;
layout (location = 5) in vec4 aInstanceRow2;
layout (location = 6) in vec4 aInstanceRow3;

uniform mat4  uViewProjection;
uniform vec3  uCameraOrigin;   // camera world position, to rebuild absolute XY
uniform float uTime;
uniform float uWindStrength;   // 0 = still
uniform float uWindSpeed;

out vec3  vNormal;
out vec2  vUV;
out float vDist;

void main()
{
    mat4 model = mat4(aInstanceRow0, aInstanceRow1, aInstanceRow2, aInstanceRow3);
    vec4 world = model * vec4(aPosition, 1.0);

    // Wind sway. aPosition.y is up the blade in model space; bend grows with
    // height so the base is planted. Phase from absolute world XY.
    float h = max(aPosition.y, 0.0);
    vec2  wp = world.xy + uCameraOrigin.xy;
    float t  = uTime * uWindSpeed;
    float phase = wp.x * 0.15 + wp.y * 0.11 + t;
    vec2  sway  = vec2(sin(phase), cos(phase * 0.83)) * (uWindStrength * h * h);
    world.xy += sway;

    vNormal = normalize(mat3(model) * aNormal);
    vUV     = aUV;
    vDist   = length(world.xyz);   // camera-relative -> distance from camera

    gl_Position = uViewProjection * world;
}
