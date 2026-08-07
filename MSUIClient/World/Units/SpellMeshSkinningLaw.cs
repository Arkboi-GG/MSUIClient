using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.World.Units;

/// <summary>
/// The complete CPU/GPU contract for a spell-effect mesh vertex.
///
/// M2Reader has already placed vertices, normals, pivots, and animation tracks in
/// MSUI model space. Matrices here use System.Numerics' row-vector convention:
/// bind vertex * T(-pivot) * posed joint global * effect root. Pack converts the
/// affine skin matrices to the three explicit GLSL dot-product rows below.
/// </summary>
public static class SpellMeshSkinningLaw
{
    public readonly record struct VertexSkin(Vector4 Weights, Vector4 Indices);

    /// <summary>
    /// Normalize all four authored byte weights. Benilla's zero-total fallback is
    /// full weight on bone zero. Indices remain the raw global M2 bone indices;
    /// the shader rejects an influence outside the palette and renormalizes the
    /// surviving weights. This keeps every invalid index class under one policy.
    /// </summary>
    public static VertexSkin Resolve(in M2Vertex vertex)
    {
        float total = vertex.BoneWeight0 + vertex.BoneWeight1 +
            vertex.BoneWeight2 + vertex.BoneWeight3;
        Vector4 weights = total <= 0f
            ? new Vector4(1f, 0f, 0f, 0f)
            : new Vector4(vertex.BoneWeight0 / total, vertex.BoneWeight1 / total,
                vertex.BoneWeight2 / total, vertex.BoneWeight3 / total);
        Vector4 indices = total <= 0f
            ? Vector4.Zero
            : new Vector4(vertex.BoneIndex0, vertex.BoneIndex1,
                vertex.BoneIndex2, vertex.BoneIndex3);
        return new VertexSkin(weights, indices);
    }

    /// <summary>Blend the valid skin matrices exactly as the vertex shader does.</summary>
    public static bool TryBlendSkin(ReadOnlySpan<Matrix4x4> palette, in VertexSkin skin,
        out Matrix4x4 blended)
    {
        blended = default;
        float sum = 0f;
        for (int i = 0; i < 4; i++)
        {
            float weight = Component(skin.Weights, i);
            int bone = (int)(Component(skin.Indices, i) + .5f);
            if (weight <= 0f || bone < 0 || bone >= palette.Length) continue;
            AddWeighted(ref blended, palette[bone], weight);
            sum += weight;
        }
        if (sum <= .0001f) return false;
        blended = Multiply(blended, 1f / sum);
        return true;
    }

    public static Vector3 SkinPoint(Vector3 bindPoint, ReadOnlySpan<Matrix4x4> palette,
        in VertexSkin skin)
        => TryBlendSkin(palette, skin, out Matrix4x4 blended)
            ? Vector3.Transform(bindPoint, blended)
            : bindPoint;

    /// <summary>
    /// Normal policy used by Benilla's Bevy 0.18 skinning shader: blend the four
    /// affine joint matrices first, append the effect root once, then apply the
    /// inverse-transpose of that combined linear map. Translation is excluded.
    /// </summary>
    public static Vector3 SkinNormal(Vector3 bindNormal, ReadOnlySpan<Matrix4x4> palette,
        in VertexSkin skin, Matrix4x4 model)
    {
        Matrix4x4 skinMatrix = TryBlendSkin(palette, skin, out Matrix4x4 blended)
            ? blended : Matrix4x4.Identity;
        Matrix4x4 world = skinMatrix * model;
        world.M14 = world.M24 = world.M34 = 0f;
        world.M41 = world.M42 = world.M43 = 0f;
        world.M44 = 1f;
        if (!Matrix4x4.Invert(world, out Matrix4x4 inverse)) return Vector3.Zero;
        Vector3 result = Vector3.TransformNormal(bindNormal, Matrix4x4.Transpose(inverse));
        return result.LengthSquared() > 1e-12f ? Vector3.Normalize(result) : Vector3.Zero;
    }

    /// <summary>
    /// Convert a world-root transform to the camera-relative model matrix. For
    /// every affine point this is exactly Transform(point, model) - camera.
    /// </summary>
    public static Matrix4x4 CameraRelativeModel(Matrix4x4 model, Vector3 camera)
    {
        model.M41 -= camera.X;
        model.M42 -= camera.Y;
        model.M43 -= camera.Z;
        return model;
    }

    /// <summary>Execute one packed shader point transform on the CPU.</summary>
    public static Vector3 TransformPackedPoint(ReadOnlySpan<float> packed, int bone, Vector3 point)
    {
        int o = bone * 12;
        return new Vector3(
            packed[o] * point.X + packed[o + 1] * point.Y + packed[o + 2] * point.Z + packed[o + 3],
            packed[o + 4] * point.X + packed[o + 5] * point.Y + packed[o + 6] * point.Z + packed[o + 7],
            packed[o + 8] * point.X + packed[o + 9] * point.Y + packed[o + 10] * point.Z + packed[o + 11]);
    }

    /// <summary>Execute one packed shader vector transform on the CPU.</summary>
    public static Vector3 TransformPackedVector(ReadOnlySpan<float> packed, int bone, Vector3 vector)
    {
        int o = bone * 12;
        return new Vector3(
            packed[o] * vector.X + packed[o + 1] * vector.Y + packed[o + 2] * vector.Z,
            packed[o + 4] * vector.X + packed[o + 5] * vector.Y + packed[o + 6] * vector.Z,
            packed[o + 8] * vector.X + packed[o + 9] * vector.Y + packed[o + 10] * vector.Z);
    }

    /// <summary>
    /// A/B gate for non-mesh consumers of the billboard palette. Disabled is a strict no-op:
    /// callers may pass their live palette without copying it and receive the current production
    /// particle/ribbon behavior byte-for-byte. The inspector keeps this false by default while
    /// the candidate path is validated against ward and known-good spell fixtures.
    /// </summary>
    public static void ApplyBillboardBonesIfEnabled(bool enabled, M2Model model,
        Matrix4x4 modelTransform, Vector3 cameraWorld, Vector3 cameraForwardWorld,
        int boneCount, Matrix4x4[] skin)
    {
        if (!enabled) return;
        ApplyBillboardBones(model, modelTransform, cameraWorld, cameraForwardWorld,
            boneCount, skin);
    }

    /// <summary>
    /// Rewrite billboard/ignore-parent-rotation joint globals, then fold each
    /// pivot back into the skin palette. Descendants are evaluated in a real
    /// parents-before-children order even if a malformed/synthetic model stores
    /// them out of order.
    /// </summary>
    public static void ApplyBillboardBones(M2Model model, Matrix4x4 modelTransform,
        Vector3 cameraWorld, Vector3 cameraForwardWorld, int boneCount, Matrix4x4[] skin)
    {
        boneCount = Math.Min(Math.Min(boneCount, model.Bones.Count), skin.Length);
        if (boneCount <= 0 || !Matrix4x4.Invert(modelTransform, out Matrix4x4 inverse)) return;
        Vector3 forward = NormalizeOr(Vector3.TransformNormal(cameraForwardWorld, inverse),
            -Vector3.UnitZ);
        Vector3 cameraRightWorld = NormalizeOr(Vector3.Cross(cameraForwardWorld, Vector3.UnitZ),
            Vector3.UnitY);
        Vector3 right = NormalizeOr(Vector3.TransformNormal(cameraRightWorld, inverse),
            Vector3.UnitX);
        Vector3 cameraUpWorld = NormalizeOr(Vector3.Cross(cameraRightWorld, cameraForwardWorld),
            Vector3.UnitZ);
        Vector3 up = NormalizeOr(Vector3.TransformNormal(cameraUpWorld, inverse),
            Vector3.UnitY);

        var original = new Matrix4x4[boneCount];
        var rewritten = new Matrix4x4[boneCount];
        var replaced = new bool[boneCount];
        for (int i = 0; i < boneCount; i++)
            original[i] = Matrix4x4.CreateTranslation(model.Bones[i].Pivot) * skin[i];

        foreach (int i in ParentFirstOrder(model, boneCount))
        {
            M2Bone bone = model.Bones[i];
            int parent = bone.ParentBone;
            bool parentChanged = parent >= 0 && parent < boneCount && replaced[parent];
            bool ignoreParentRotation = (bone.Flags & 0x04) != 0;
            uint billboard = bone.Flags & 0x78;
            if (!parentChanged && !ignoreParentRotation && billboard == 0) continue;

            Matrix4x4 global = original[i];
            if (parentChanged && Matrix4x4.Invert(original[parent], out Matrix4x4 parentInverse))
            {
                Matrix4x4 local = original[i] * parentInverse;
                global = local * rewritten[parent];
            }
            if (!Matrix4x4.Decompose(global, out Vector3 scale, out Quaternion kept,
                    out Vector3 position))
                continue;

            if (ignoreParentRotation)
            {
                global = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(position);
            }
            else if (billboard != 0)
            {
                Vector3 bx, by, bz;
                if ((billboard & 0x08) != 0)
                {
                    bx = -forward; by = right; bz = up;
                }
                else if ((billboard & 0x40) != 0)
                {
                    bz = NormalizeOr(Vector3.Transform(Vector3.UnitY, kept), Vector3.UnitY);
                    by = NormalizeOr(Vector3.Cross(forward, bz), right);
                    bx = NormalizeOr(Vector3.Cross(by, bz), -forward);
                }
                else if ((billboard & 0x10) != 0)
                {
                    bx = NormalizeOr(Vector3.Transform(Vector3.UnitX, kept), -forward);
                    bz = NormalizeOr(Vector3.Cross(forward, bx), up);
                    by = NormalizeOr(Vector3.Cross(bz, bx), right);
                }
                else
                {
                    by = NormalizeOr(Vector3.Transform(-Vector3.UnitZ, kept), right);
                    bx = NormalizeOr(Vector3.Cross(forward, by), -forward);
                    bz = NormalizeOr(Vector3.Cross(bx, by), up);
                }
                Matrix4x4 facing = new(
                    bx.X, bx.Y, bx.Z, 0,
                    bz.X, bz.Y, bz.Z, 0,
                    -by.X, -by.Y, -by.Z, 0,
                    0, 0, 0, 1);
                global = Matrix4x4.CreateScale(scale) * facing *
                    Matrix4x4.CreateTranslation(position);
            }

            rewritten[i] = global;
            skin[i] = Matrix4x4.CreateTranslation(-bone.Pivot) * global;
            replaced[i] = true;
        }
    }

    private static IEnumerable<int> ParentFirstOrder(M2Model model, int count)
    {
        var emitted = new bool[count];
        for (int pass = 0; pass < count; pass++)
        {
            bool progress = false;
            for (int i = 0; i < count; i++)
            {
                if (emitted[i]) continue;
                int parent = model.Bones[i].ParentBone;
                if (parent >= 0 && parent < count && !emitted[parent] && parent != i) continue;
                emitted[i] = true;
                progress = true;
                yield return i;
            }
            if (!progress) break;
        }
        for (int i = 0; i < count; i++)
            if (!emitted[i]) yield return i;
    }

    private static float Component(Vector4 value, int index) => index switch
    {
        0 => value.X, 1 => value.Y, 2 => value.Z, _ => value.W,
    };

    private static void AddWeighted(ref Matrix4x4 result, Matrix4x4 value, float weight)
    {
        result.M11 += value.M11 * weight; result.M12 += value.M12 * weight;
        result.M13 += value.M13 * weight; result.M14 += value.M14 * weight;
        result.M21 += value.M21 * weight; result.M22 += value.M22 * weight;
        result.M23 += value.M23 * weight; result.M24 += value.M24 * weight;
        result.M31 += value.M31 * weight; result.M32 += value.M32 * weight;
        result.M33 += value.M33 * weight; result.M34 += value.M34 * weight;
        result.M41 += value.M41 * weight; result.M42 += value.M42 * weight;
        result.M43 += value.M43 * weight; result.M44 += value.M44 * weight;
    }

    private static Matrix4x4 Multiply(Matrix4x4 value, float factor) => new(
        value.M11 * factor, value.M12 * factor, value.M13 * factor, value.M14 * factor,
        value.M21 * factor, value.M22 * factor, value.M23 * factor, value.M24 * factor,
        value.M31 * factor, value.M32 * factor, value.M33 * factor, value.M34 * factor,
        value.M41 * factor, value.M42 * factor, value.M43 * factor, value.M44 * factor);

    private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback)
        => value.LengthSquared() > 1e-8f ? Vector3.Normalize(value) : fallback;

    public const string VertexShaderSource = @"#version 330 core
layout(location=0) in vec3 aPosition;
layout(location=1) in vec3 aNormal;
layout(location=2) in vec2 aUV;
layout(location=3) in vec4 aBoneWeights;
layout(location=4) in vec4 aBoneIndices;
uniform mat4 uModel;
uniform mat4 uModelViewProjection;
uniform mat4 uView;
const int MAX_BONES = 160;
uniform vec4 uBones[MAX_BONES * 3];
uniform int uBoneCount;
out vec3 vWorldPos;
out vec3 vNormal;
out vec2 vUV;
out float vEyeDepth;
vec3 skinPoint(vec3 p, int b) {
    vec4 h=vec4(p,1.0);
    return vec3(dot(uBones[b*3],h),dot(uBones[b*3+1],h),dot(uBones[b*3+2],h));
}
mat3 skinLinear(int b) {
    vec3 r0=uBones[b*3].xyz, r1=uBones[b*3+1].xyz, r2=uBones[b*3+2].xyz;
    return mat3(vec3(r0.x,r1.x,r2.x),vec3(r0.y,r1.y,r2.y),vec3(r0.z,r1.z,r2.z));
}
void main() {
    vec3 p=aPosition; mat3 sm=mat3(1.0);
    if (uBoneCount > 0) {
        vec3 sp=vec3(0.0); sm=mat3(0.0); float sum=0.0;
        for (int i=0;i<4;i++) {
            float w=aBoneWeights[i]; int b=int(aBoneIndices[i]+0.5);
            if (w<=0.0 || b<0 || b>=uBoneCount) continue;
            sp += skinPoint(aPosition,b)*w; sm += skinLinear(b)*w; sum += w;
        }
        if (sum>0.0001) { p=sp/sum; sm/=sum; } else { sm=mat3(1.0); }
    }
    vec4 world=uModel*vec4(p,1.0);
    mat3 worldLinear=mat3(uModel)*sm;
    vWorldPos=world.xyz; vNormal=normalize(transpose(inverse(worldLinear))*aNormal); vUV=aUV;
    vEyeDepth=-(uView*world).z;
    gl_Position=uModelViewProjection*vec4(p,1.0);
}";
}
