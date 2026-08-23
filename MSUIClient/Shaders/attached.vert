#version 330 core

// Character attachment vertex shader. Most equipment remains on the rigid path; item models whose
// authored vertices depend on billboard bones receive a bind-pose palette with those joints
// camera-faced by AttachedItemBillboardLaw.
//
// Attachments need a separate clip matrix for the same reason the skinned
// character does: applying their world transform first adds tiny helmet,
// shoulder and weapon geometry to a roughly -8950 world coordinate. That
// rounds away close surface separation before the view matrix subtracts the
// camera. A precombined model-view-projection keeps the local detail intact.

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aNormal;
layout (location = 2) in vec2 aUV;
layout (location = 3) in vec4 aBoneWeights;
layout (location = 4) in vec4 aBoneIndices;

uniform mat4 uModel;
uniform mat4 uModelViewProjection;
const int MAX_BONES = 160;
uniform vec4 uBones[MAX_BONES * 3];
uniform int uBoneCount;
uniform vec2 uUvOffset;

out vec3 vWorldPos;
out vec3 vNormal;
out vec2 vUV;

void main()
{
    vec3 p = aPosition;
    mat3 skinLinear = mat3(1.0);
    if (uBoneCount > 0)
    {
        vec3 skinnedPoint = vec3(0.0);
        skinLinear = mat3(0.0);
        float sum = 0.0;
        for (int i = 0; i < 4; i++)
        {
            float weight = aBoneWeights[i];
            int bone = int(aBoneIndices[i] + 0.5);
            if (weight <= 0.0 || bone < 0 || bone >= uBoneCount) continue;
            vec4 homogeneous = vec4(aPosition, 1.0);
            vec3 point = vec3(dot(uBones[bone * 3], homogeneous),
                              dot(uBones[bone * 3 + 1], homogeneous),
                              dot(uBones[bone * 3 + 2], homogeneous));
            vec3 r0 = uBones[bone * 3].xyz;
            vec3 r1 = uBones[bone * 3 + 1].xyz;
            vec3 r2 = uBones[bone * 3 + 2].xyz;
            skinnedPoint += point * weight;
            skinLinear += mat3(vec3(r0.x, r1.x, r2.x),
                               vec3(r0.y, r1.y, r2.y),
                               vec3(r0.z, r1.z, r2.z)) * weight;
            sum += weight;
        }
        if (sum > 0.0001)
        {
            p = skinnedPoint / sum;
            skinLinear /= sum;
        }
        else
        {
            skinLinear = mat3(1.0);
        }
    }

    vec4 world = uModel * vec4(p, 1.0);

    vWorldPos = world.xyz;
    vNormal = normalize(transpose(inverse(mat3(uModel) * skinLinear)) * aNormal);
    vUV = aUV + uUvOffset;

    gl_Position = uModelViewProjection * vec4(p, 1.0);
}
