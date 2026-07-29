#version 330 core

// MSUI Client - terrain vertex shader.
// Positions arrive in WoW world space (X north, Y west, Z up). There is no
// coordinate conversion in this client; the camera view matrix uses +Z as up,
// so world space goes to the GPU untouched.
//
// ASCII ONLY. Some GLSL compilers (Intel notably) abort with a bogus
// "pre-mature EOF" on any non-ASCII byte, even inside a comment.
//
// THE UV IS PER CHUNK, NOT PER TILE, AND THAT IS DELIBERATE.
// It used to run 0..1 across the whole ADT, which forced the fragment shader to
// wrap it with fract() to get a per-chunk tiling coordinate - and a fract()
// inside a triangle is a derivative cliff that made every chunk edge sample the
// deepest mip, drawing a dark grid over the world. A per-chunk 0..1 coordinate
// needs no wrap at all: the tiling factor is a plain multiply, the wrap happens
// in the sampler's address unit, and the discontinuity between chunks lands on
// a vertex boundary between two separate triangles where derivatives stay
// correct on both sides.
//
// It is also exactly what the alpha array wants, since each chunk's mask is its
// own layer addressed 0..1.

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aNormal;
layout (location = 2) in vec2 aChunkUV;     // 0..1 across this vertex's own MCNK
layout (location = 3) in vec4 aLayers;      // tileset array indices, -1 = unused
layout (location = 4) in float aAlphaLayer; // this chunk's layer in the alpha array

uniform mat4 uViewProjection;
uniform vec3 uCameraOrigin;

out vec3 vWorldPos;
out vec3 vNormal;
out vec2 vChunkUV;
flat out vec4 vLayers;
flat out float vAlphaLayer;

void main()
{
    vec3 relativePosition = aPosition - uCameraOrigin;

    vWorldPos   = relativePosition;
    vNormal     = normalize(aNormal);
    vChunkUV    = aChunkUV;
    vLayers     = aLayers;
    vAlphaLayer = aAlphaLayer;

    gl_Position = uViewProjection * vec4(relativePosition, 1.0);
}
