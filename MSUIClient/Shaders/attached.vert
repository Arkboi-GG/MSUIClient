#version 330 core

// Unskinned character attachment vertex shader.
//
// Attachments need a separate clip matrix for the same reason the skinned
// character does: applying their world transform first adds tiny helmet,
// shoulder and weapon geometry to a roughly -8950 world coordinate. That
// rounds away close surface separation before the view matrix subtracts the
// camera. A precombined model-view-projection keeps the local detail intact.

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aNormal;
layout (location = 2) in vec2 aUV;

uniform mat4 uModel;
uniform mat4 uModelViewProjection;

out vec3 vWorldPos;
out vec3 vNormal;
out vec2 vUV;

void main()
{
    vec4 world = uModel * vec4(aPosition, 1.0);

    vWorldPos = world.xyz;
    vNormal = normalize(mat3(uModel) * aNormal);
    vUV = aUV;

    gl_Position = uModelViewProjection * vec4(aPosition, 1.0);
}
