#version 330 core

// MSUI Client - creator gizmo lines, vertex stage.
//
// Positions are absolute WoW world space (the same convention as
// collision.vert); every vertex carries its own RGBA so one upload can hold
// the reference grid, the emitter frames, the reach rays and the highlight
// state without a uniform per primitive.
//
// ASCII ONLY. Some GLSL compilers abort with a bogus "pre-mature EOF" on any
// non-ASCII byte, even inside a comment.

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec4 aColor;

uniform mat4 uViewProjection;

out vec4 vColor;

void main()
{
    vColor = aColor;
    gl_Position = uViewProjection * vec4(aPosition, 1.0);
}
