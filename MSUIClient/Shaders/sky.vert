#version 330 core

// Fullscreen triangle with no vertex buffer at all: three vertices generated
// from gl_VertexID. A sky dome would be geometry to build, upload, cull and get
// wrong at the poles; a triangle covering the screen is none of those, and the
// sky is a function of view direction rather than of position.
out vec2 vNdc;

void main()
{
    vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    vNdc = p * 2.0 - 1.0;

    // z = 1 puts it on the far plane. The pass runs with depth writes off, so
    // the world still draws over it normally.
    gl_Position = vec4(vNdc, 1.0, 1.0);
}
