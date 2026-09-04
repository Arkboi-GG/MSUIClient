using System.Numerics;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.World.Units;

namespace MSUIClient.World.Doodads;

/// <summary>
/// Camera-facing M2 bones require a pose per placement because the camera direction in
/// model space changes with the placement transform. Ordinary doodads keep the shared
/// instanced VBO; only models whose visible vertices actually depend on such a bone use
/// DoodadRenderer's per-instance CPU skinning path.
/// </summary>
public static class DoodadBillboardLaw
{
    public const uint IgnoreParentRotation = 0x04;
    public const uint BillboardMask = 0x78;
    public const uint CameraFacingMask = IgnoreParentRotation | BillboardMask;

    public static bool RequiresPerInstancePose(M2Model model)
    {
        int boneCount = model.Bones.Count;
        if (boneCount == 0 || model.Vertices.Count == 0) return false;

        foreach (M2Vertex vertex in model.Vertices)
        {
            if (InfluenceFacesCamera(model, boneCount, vertex.BoneIndex0, vertex.BoneWeight0) ||
                InfluenceFacesCamera(model, boneCount, vertex.BoneIndex1, vertex.BoneWeight1) ||
                InfluenceFacesCamera(model, boneCount, vertex.BoneIndex2, vertex.BoneWeight2) ||
                InfluenceFacesCamera(model, boneCount, vertex.BoneIndex3, vertex.BoneWeight3))
                return true;

            // Shared M2 skinning treats a zero-total vertex as rigidly attached to bone zero.
            if (vertex.BoneWeight0 == 0 && vertex.BoneWeight1 == 0 &&
                vertex.BoneWeight2 == 0 && vertex.BoneWeight3 == 0 &&
                BoneOrAncestorFacesCamera(model, boneCount, 0))
                return true;
        }
        return false;
    }

    public static void Apply(M2Model model, Matrix4x4 placement, Camera camera,
        Matrix4x4[] skin) =>
        SpellMeshSkinningLaw.ApplyBillboardBones(model, placement, camera.Position,
            camera.Forward, Math.Min(model.Bones.Count, skin.Length), skin);

    private static bool InfluenceFacesCamera(M2Model model, int boneCount,
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
