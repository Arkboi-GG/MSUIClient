#version 330 core

// MSUI Client - M2 doodad vertex shader.
//
// FORKED FROM wmo.vert ON PURPOSE. Doodads used to load wmo.vert/wmo.frag
// directly, which meant any change made for props risked changing how building
// walls light. The interior wall lighting is baked MOCV and is correct; it must
// not move. This pair exists so the two can diverge safely.
//
// Vertices arrive in M2 model space. The instance matrix (or uModel) carries
// the whole placement into WoW world space (X north, Y west, Z up), so lighting
// and fog match terrain exactly as they do for buildings.
//
// Both matrices are uploaded with transpose = false. System.Numerics stores
// row-major, GL reads those bytes as column-major, and that flip is the one
// GLSL wants - so M * vec4(pos, 1.0) is correct here.
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

// PER-INSTANCE BAKED INTERIOR LIGHT.
//   rgb = MODD.color / 255, the light Blizzard baked into this one placement
//         when the WMO was authored. Same scale as raw MOCV, so it goes through
//         the same uVertexColorScale the walls use and a barrel ends up lit
//         like the floor it stands on.
//   a   = how much DAYLIGHT to use instead. 0 = fully interior, 1 = fully
//         exterior, values between fade a prop standing in a doorway.
//
// The default value of a disabled vertex attribute is (0, 0, 0, 1), which is
// exactly "no baked light, full daylight" - so a doodad that never gets an
// interior light assigned renders precisely as it did before this shader
// existed. That default is load-bearing: terrain doodads never set it.
layout (location = 7) in vec4 aInstanceLight;

uniform mat4 uViewProjection;
uniform mat4 uModel;
uniform mat4 uModelViewProjection;
uniform int  uUseInstancing;

// Same payload as aInstanceLight, for the non-instanced draw path.
uniform vec4 uInstanceLight;

out vec3 vWorldPos;
out vec3 vNormal;
out vec2 vUV;
out vec4 vLight;

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

    // Doodad placements DO carry scale (MDDF and MODD both scale uniformly),
    // but a uniform scale leaves directions unchanged once renormalised, so the
    // inverse-transpose is still unnecessary here.
    vNormal = normalize(mat3(model) * aNormal);

    vUV = aUV;
    vLight = uUseInstancing == 1 ? aInstanceLight : uInstanceLight;

    gl_Position = uUseInstancing == 1
        ? uViewProjection * world
        : uModelViewProjection * vec4(aPosition, 1.0);
}
