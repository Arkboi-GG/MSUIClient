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
layout (location = 3) in vec4 aInstanceRow0;
layout (location = 4) in vec4 aInstanceRow1;
layout (location = 5) in vec4 aInstanceRow2;
layout (location = 6) in vec4 aInstanceRow3;
// MOCV baked lighting, already fixed up and swizzled to RGBA on the CPU.
// Location 7 because 3-6 belong to the instancing matrix.
layout (location = 7) in vec4 aColor;

uniform mat4 uViewProjection;
uniform mat4 uModel;
uniform mat4 uModelViewProjection;
uniform int uUseInstancing;

out vec3 vWorldPos;
out vec3 vNormal;
out vec2 vUV;
out vec4 vColor;

void main()
{
    // System.Numerics rows arrive as four vertex attributes. A GLSL mat4
    // constructor treats them as columns, which performs the same row-to-column
    // flip as the existing uniform upload path.
    mat4 model = uUseInstancing == 1
        ? mat4(aInstanceRow0, aInstanceRow1, aInstanceRow2, aInstanceRow3)
        : uModel;
    vec4 world = model * vec4(aPosition, 1.0);

    vWorldPos = world.xyz;

    // The placement transform is rotation plus translation only - no scale and
    // no mirror (its linear part has determinant +1), so normals rotate with
    // the same matrix and need no inverse-transpose.
    vNormal = normalize(mat3(model) * aNormal);

    vUV = aUV;
    vColor = aColor;

    gl_Position = uUseInstancing == 1
        ? uViewProjection * world
        : uModelViewProjection * vec4(aPosition, 1.0);
}
