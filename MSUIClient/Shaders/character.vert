#version 330 core

// MSUI Client - skinned character vertex shader.
//
// This is wmo.vert plus skinning, and nothing else. It writes the same world
// position, normal, and authored UV interface used by attached.vert.
//
// BONE MATRICES ARE ROWS, NOT A mat4 ARRAY
//   Each bone is three vec4 holding the ROWS of an affine transform, so
//   skinning is three dot products and there is no column-order convention to
//   get wrong. It is also compact: three vec4 instead of four.
//
//       out.x = dot(a, vec4(pos, 1.0))
//       out.y = dot(b, vec4(pos, 1.0))
//       out.z = dot(c, vec4(pos, 1.0))
//
//   M2Animator.Pack writes them in that order. The two must agree.
//
// MAX_BONES MUST MATCH M2Animator.MaxBones
//   HumanMale.m2 has 119 bones - vanilla characters carry a full set of finger
//   and facial joints. An earlier 80 here meant bones 80-118 were never
//   uploaded and their vertices were clamped onto bone 79, which is invisible
//   in bind pose (every matrix is the identity there) and looks like a folded
//   paper alien the moment anything animates.
//
//   160 * 3 = 480 vec4 = 1920 float components; with uViewProjection and uModel
//   that is 1952. Above the 1024 the spec guarantees, well inside the 4096 real
//   drivers report. If a driver ever refuses, this fails at LINK time with the
//   full log, which is a good failure.
//
//   Change this and M2Animator.MaxBones together or not at all.
//
// ASCII ONLY, NO BOM. Intel's GLSL compiler reports a bogus "pre-mature EOF"
// on a single non-ASCII byte even inside a comment.

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aNormal;
layout (location = 2) in vec2 aUV;
layout (location = 3) in vec4 aBoneWeights;
layout (location = 4) in vec4 aBoneIndices;

uniform mat4 uModel;
uniform mat4 uModelViewProjection;

const int MAX_BONES = 160;
uniform vec4 uBones[MAX_BONES * 3];

// Zero when the model has no skeleton. Skinning is then skipped entirely and
// the model draws in bind pose, which is the same thing DoodadRenderer does.
uniform int uBoneCount;

out vec3 vWorldPos;
out vec3 vNormal;
out vec2 vUV;
out vec2 vUV2;

vec3 skinPoint(vec3 p, int b)
{
    vec4 h = vec4(p, 1.0);
    return vec3(dot(uBones[b * 3 + 0], h),
                dot(uBones[b * 3 + 1], h),
                dot(uBones[b * 3 + 2], h));
}

vec3 skinVector(vec3 v, int b)
{
    return vec3(dot(uBones[b * 3 + 0].xyz, v),
                dot(uBones[b * 3 + 1].xyz, v),
                dot(uBones[b * 3 + 2].xyz, v));
}

void main()
{
    vec3 position = aPosition;
    vec3 normal = aNormal;

    if (uBoneCount > 0)
    {
        vec3 skinnedPosition = vec3(0.0);
        vec3 skinnedNormal = vec3(0.0);
        float total = 0.0;

        for (int i = 0; i < 4; i++)
        {
            float w = aBoneWeights[i];
            if (w <= 0.0) continue;

            int b = int(aBoneIndices[i] + 0.5);
            if (b < 0 || b >= uBoneCount) continue;

            skinnedPosition += skinPoint(aPosition, b) * w;
            skinnedNormal += skinVector(aNormal, b) * w;
            total += w;
        }

        // A vertex with no usable influence keeps its bind-pose position rather
        // than collapsing to the origin. A stray triangle stretching to (0,0,0)
        // is the loudest, least informative artefact in skinning.
        if (total > 0.0001)
        {
            position = skinnedPosition / total;
            normal = skinnedNormal / total;
        }
    }

    vec4 world = uModel * vec4(position, 1.0);

    vWorldPos = world.xyz;

    // Bone transforms are rotation and translation; the scale tracks vanilla
    // characters use are uniform where they exist at all. So normals rotate
    // with the same matrices and need no inverse-transpose, exactly as in
    // wmo.vert.
    vNormal = normalize(mat3(uModel) * normal);

    vUV = aUV;
    vUV2 = aUV;

    // Use the CPU-precombined matrix for clip position. If model and view are
    // applied separately, the model step first adds the character's ~-8950
    // world translation to tiny facial/clothing offsets. A float loses about
    // a millimetre there before the view matrix can subtract the camera again,
    // collapsing close layers onto one depth value as the camera moves.
    gl_Position = uModelViewProjection * vec4(position, 1.0);
}
