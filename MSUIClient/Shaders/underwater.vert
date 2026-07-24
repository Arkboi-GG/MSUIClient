#version 330 core

// MSUI Client - underwater overlay vertex shader.
//
// A single full-screen triangle generated from gl_VertexID, so it needs no
// vertex buffer at all - just glDrawArrays(Triangles, 0, 3) with any VAO bound.
// The fragment shader tints the whole screen when the camera eye is below a
// water surface, which is what gives the "I am underwater" feeling that a
// surface-only water pass can never provide.
//
// ASCII ONLY.

out vec2 vUV;

void main()
{
    // (0,0) (2,0) (0,2) in UV -> a triangle that covers the [-1,1] clip square.
    vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    vUV = p;
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}
