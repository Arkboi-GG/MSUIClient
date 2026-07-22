#version 330 core

// MSUI Client - terrain vertex shader.
// Positions arrive in WoW world space (X north, Y west, Z up). There is no
// coordinate conversion in this client; the camera view matrix uses +Z as up,
// so world space goes to the GPU untouched.
//
// ASCII ONLY. Some GLSL compilers (Intel notably) abort with a bogus
// "pre-mature EOF" on any non-ASCII byte, even inside a comment.

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aNormal;
layout (location = 2) in vec2 aTileUV;    // 0..1 across the whole ADT tile
layout (location = 3) in vec4 aLayers;    // tileset array indices, -1 = unused

uniform mat4 uViewProjection;

out vec3 vWorldPos;
out vec3 vNormal;
out vec2 vTileUV;
flat out vec4 vLayers;

void main()
{
    vWorldPos = aPosition;
    vNormal   = normalize(aNormal);
    vTileUV   = aTileUV;
    vLayers   = aLayers;

    gl_Position = uViewProjection * vec4(aPosition, 1.0);
}
