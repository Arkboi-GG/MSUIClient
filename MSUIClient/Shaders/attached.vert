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
layout (location = 5) in vec2 aUV1;

uniform mat4 uModel;
uniform mat4 uModelViewProjection;
uniform mat4 uView;
const int MAX_BONES = 160;
uniform vec4 uBones[MAX_BONES * 3];
uniform int uBoneCount;
uniform vec2 uUvOffset;
uniform vec2 uUvOffset2;
uniform int uUvSet;
uniform int uUvSet2;
uniform int uEnvironmentMap;
uniform int uEnvironmentMap2;

out vec3 vWorldPos;
out vec3 vNormal;
out vec2 vUV;
out vec2 vUV2;

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
    vec2 mappedUV = uUvSet != 0 ? aUV1 : aUV;
    vec2 mappedUV2 = uUvSet2 != 0 ? aUV1 : aUV;
    if (uEnvironmentMap != 0 || uEnvironmentMap2 != 0)
    {
        // Exact build-5875 Model2 generated coordinate. The original client
        // computes this from view-space position and normal PER VERTEX, then
        // lets the rasterizer interpolate it across the blade. That positional
        // variation is what turns ArmorReflect3's narrow diagonal streak into
        // a line that travels along the Warglaive instead of making a whole
        // low-poly face brighten and dim together.
        vec3 viewPosition = (uView * world).xyz;
        vec3 viewNormal = normalize(mat3(uView) * vNormal);
        vec3 reflected = viewPosition
            - 2.0 * dot(viewPosition, viewNormal) * viewNormal;
        float reflectedLength = length(reflected);
        vec2 generatedUV = reflectedLength > 0.000001
            ? reflected.xy / reflectedLength * 0.5 + vec2(0.5)
            : vec2(0.5);
        if (uEnvironmentMap != 0) mappedUV = generatedUV;
        if (uEnvironmentMap2 != 0) mappedUV2 = generatedUV;
    }

    // Generated coordinates never inherit an authored texture transform.
    vUV = mappedUV + (uEnvironmentMap != 0 ? vec2(0.0) : uUvOffset);
    vUV2 = mappedUV2 + (uEnvironmentMap2 != 0 ? vec2(0.0) : uUvOffset2);

    gl_Position = uModelViewProjection * vec4(p, 1.0);
}
