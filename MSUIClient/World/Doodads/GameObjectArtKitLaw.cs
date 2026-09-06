using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.World.Doodads;

public static class GameObjectArtKitLaw
{
    public static bool TryAttachmentTransform(M2Model source, int slot,
        IReadOnlyList<Matrix4x4> skin, Matrix4x4 parent, out Matrix4x4 transform)
    {
        transform = default;
        if ((uint)slot >= 4) return false;
        M2Attachment? attachment = source.Attachments.FirstOrDefault(a => a.Id == (uint)slot);
        if (attachment is null || attachment.BoneIndex >= skin.Count) return false;
        transform = Matrix4x4.CreateTranslation(attachment.Position) * skin[(int)attachment.BoneIndex] * parent;
        return true;
    }
}
