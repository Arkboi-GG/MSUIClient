using MSUIClient.Formats;

namespace MSUIClient.World.Doodads;

/// <summary>
/// Narrow fallback for one vanilla scenery effect whose visible mesh is made
/// entirely from camera-facing M2 billboard bones. DoodadRenderer's shared
/// per-model VBO cannot pose those cards per placement/camera: drawing their
/// bind geometry exposes five solid green shrub triangles across the room.
///
/// Keep this structural as well as name-gated. It must never become a broad
/// "hide billboards" rule; ordinary billboarded doodads need a real
/// per-instance pose implementation instead.
/// </summary>
public static class DoodadBillboardFallbackLaw
{
    private const string AshenvaleWispsStem = "AshenvaleWisps";

    public static bool SuppressUnsupportedMesh(string modelPath, M2Model model)
    {
        if (!Path.GetFileNameWithoutExtension(modelPath)
                .Equals(AshenvaleWispsStem, StringComparison.OrdinalIgnoreCase))
            return false;

        // The vanilla asset is five independent triangular cards: fifteen
        // vertices/indices, each triplet rigidly weighted to one spherical
        // billboard bone (flags 0x08). Bone 5 owns the real flare emitter and
        // is deliberately not part of this mesh signature.
        if (model.Vertices.Count != 15 || model.Indices.Count != 15 ||
            model.Bones.Count < 5 || model.ParticleEmitters.Count == 0)
            return false;

        for (int card = 0; card < 5; card++)
        {
            M2Bone bone = model.Bones[card];
            if ((bone.Flags & 0x78) != 0x08) return false;

            for (int vertex = card * 3; vertex < card * 3 + 3; vertex++)
            {
                M2Vertex v = model.Vertices[vertex];
                if (v.BoneWeight0 != 255 || v.BoneIndex0 != card ||
                    v.BoneWeight1 != 0 || v.BoneWeight2 != 0 || v.BoneWeight3 != 0)
                    return false;
            }
        }

        return true;
    }
}
