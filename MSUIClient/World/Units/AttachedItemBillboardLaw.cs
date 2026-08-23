using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.World.Units;

/// <summary>
/// Selects the attached-item models whose visible vertices depend on a camera-facing M2 bone and
/// prepares their otherwise-bind-pose palette. Ordinary equipment remains on the rigid fast path;
/// affected batches retain the authored per-vertex blends instead of being split into guessed cards.
/// </summary>
public static class AttachedItemBillboardLaw
{
    public const uint IgnoreParentRotation = 0x04;
    public const uint BillboardMask = 0x78;
    public const uint CameraFacingMask = IgnoreParentRotation | BillboardMask;

    public static bool UsesCameraFacingPalette(M2Model model)
    {
        int boneCount = Math.Min(model.Bones.Count, M2Animator.MaxBones);
        if (boneCount == 0 || model.Vertices.Count == 0) return false;

        foreach (M2Vertex vertex in model.Vertices)
        {
            if (InfluenceUsesCameraFacingBone(model, boneCount,
                    vertex.BoneIndex0, vertex.BoneWeight0) ||
                InfluenceUsesCameraFacingBone(model, boneCount,
                    vertex.BoneIndex1, vertex.BoneWeight1) ||
                InfluenceUsesCameraFacingBone(model, boneCount,
                    vertex.BoneIndex2, vertex.BoneWeight2) ||
                InfluenceUsesCameraFacingBone(model, boneCount,
                    vertex.BoneIndex3, vertex.BoneWeight3))
                return true;

            // The shared M2 skinning contract assigns a zero-total vertex fully to bone zero.
            if (vertex.BoneWeight0 == 0 && vertex.BoneWeight1 == 0 &&
                vertex.BoneWeight2 == 0 && vertex.BoneWeight3 == 0 &&
                BoneOrAncestorFacesCamera(model, boneCount, 0))
                return true;
        }
        return false;
    }

    public static int PreparePalette(M2Model model, bool enabled, Matrix4x4 modelTransform,
        Vector3 cameraWorld, Vector3 cameraForwardWorld, Matrix4x4[] skin)
    {
        if (!enabled) return 0;
        int boneCount = Math.Min(Math.Min(model.Bones.Count, M2Animator.MaxBones), skin.Length);
        if (boneCount == 0) return 0;
        Array.Fill(skin, Matrix4x4.Identity, 0, boneCount);
        SpellMeshSkinningLaw.ApplyBillboardBones(model, modelTransform, cameraWorld,
            cameraForwardWorld, boneCount, skin);
        return boneCount;
    }

    private static bool InfluenceUsesCameraFacingBone(M2Model model, int boneCount,
        byte bone, byte weight) =>
        weight != 0 && BoneOrAncestorFacesCamera(model, boneCount, bone);

    private static bool BoneOrAncestorFacesCamera(M2Model model, int boneCount, int bone)
    {
        for (int depth = 0; depth < boneCount && bone >= 0 && bone < boneCount; depth++)
        {
            M2Bone current = model.Bones[bone];
            if ((current.Flags & CameraFacingMask) != 0) return true;
            bone = current.ParentBone;
        }
        return false;
    }
}
