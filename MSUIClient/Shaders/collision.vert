#version 330 core

// MSUI Client - collision debug wireframe, vertex stage.
//
// Positions are already in WoW world space; the collision world is baked there
// at load. aNormalZ is the absolute Z of the triangle's normal, flat per
// triangle, so the fragment stage can colour standable surfaces differently
// from walls. aSource identifies which model instance the triangle came from,
// so a single building can be isolated out of the whole world.
//
// ASCII ONLY. Some GLSL compilers abort with a bogus "pre-mature EOF" on any
// non-ASCII byte, even inside a comment.

layout (location = 0) in vec3 aPosition;
layout (location = 1) in float aNormalZ;
layout (location = 2) in float aSource;

uniform mat4 uViewProjection;
uniform vec3 uOffset;

out vec3 vWorldPos;
flat out float vNormalZ;
flat out float vSource;

void main()
{
    vec3 world = aPosition + uOffset;

    vWorldPos = world;
    vNormalZ  = aNormalZ;
    vSource   = aSource;

    gl_Position = uViewProjection * vec4(world, 1.0);
}
