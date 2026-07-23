#version 330 core

// MSUI Client - WMO (building) vertex shader.
//
// Vertices arrive in WMO LOCAL space. uModel carries the whole placement:
// the MODF rotation, the MODF translation, and the conversion from ADT
// placement space into WoW world space (X north, Y west, Z up). After uModel
// everything is world space, exactly like terrain, so lighting and fog match.
//
// Both matrices are uploaded with transpose = false. System.Numerics stores
// row-major, GL reads those bytes as column-major, and that flip is the one
// GLSL wants - so M * vec4(pos, 1.0) is correct here, same as terrain.
//
// ASCII ONLY. Some GLSL compilers abort with a bogus "pre-mature EOF" on any
// non-ASCII byte, even inside a comment.

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aNormal;
layout (location = 2) in vec2 aUV;

uniform mat4 uViewProjection;
uniform mat4 uModel;
uniform mat4 uModelViewProjection;

out vec3 vWorldPos;
out vec3 vNormal;
out vec2 vUV;

void main()
{
    vec4 world = uModel * vec4(aPosition, 1.0);

    vWorldPos = world.xyz;

    // The placement transform is rotation plus translation only - no scale and
    // no mirror (its linear part has determinant +1), so normals rotate with
    // the same matrix and need no inverse-transpose.
    vNormal = normalize(mat3(uModel) * aNormal);

    vUV = aUV;

    gl_Position = uModelViewProjection * vec4(aPosition, 1.0);
}
