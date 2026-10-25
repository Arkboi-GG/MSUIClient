using System.Numerics;
using MSUIClient.Engine;

namespace MSUIClient.World;

/// <summary>
/// The Quest Tabletop's view transform for the world renderers (shared_docs/QUEST_TABLETOP.md §4): relief exaggeration
/// and the board cut, set ONLY on the tabletop's own renderer instances (World/Tabletop/TabletopNearScene). The game's
/// renderers never carry one, so their shader programs keep the GL defaults (uReliefOn = 0, clip distances 1-3 not
/// enabled) and render exactly as before. A view transform only: it never reaches collision, movement or the server.
///
/// Relief: z' = z + (k − 1)(z − baseZ) for terrain and water vertices (exactly z at k = 1); models get the same offset
/// of the ground under them on the CPU (<see cref="Lift"/>), so they ride the raised ground at true scale.
/// Cut: the board rectangle as four inward planes; plane 0 travels through the renderers' existing WorldClipPlane
/// argument (GL_CLIP_DISTANCE0), planes 1-3 through uBoardClip1..3 (GL_CLIP_DISTANCE1..3).
/// </summary>
public sealed class TabletopViewTransform
{
    public float K { get; set; } = 1f;
    public float BaseZ { get; set; }
    /// <summary>Inward planes: x ≤ top, x ≥ bottom, y ≤ left, y ≥ right.</summary>
    public WorldClipPlane[] Board { get; } = new WorldClipPlane[4];

    public void SetBoard(float top, float bottom, float left, float right)
    {
        Board[0] = new WorldClipPlane(new Vector3(-1f, 0f, 0f), top);
        Board[1] = new WorldClipPlane(new Vector3(1f, 0f, 0f), -bottom);
        Board[2] = new WorldClipPlane(new Vector3(0f, -1f, 0f), left);
        Board[3] = new WorldClipPlane(new Vector3(0f, 1f, 0f), -right);
    }

    /// <summary>Vertical offset for a model standing on <paramref name="ground"/>: (k − 1)(ground − baseZ); 0 at k = 1.</summary>
    public float Lift(float ground) => (K - 1f) * (ground - BaseZ);

    /// <summary>Upload the relief and cut planes 1-3 (camera-relative) to a shader that declares them.</summary>
    public void Apply(Shader shader, Vector3 cameraPosition, bool relief)
    {
        shader.Set("uReliefOn", relief && K != 1f ? 1 : 0);
        shader.Set("uReliefK", K);
        shader.Set("uReliefBase", BaseZ);
        shader.Set("uBoardClip1", Board[1].RelativeEquation(cameraPosition));
        shader.Set("uBoardClip2", Board[2].RelativeEquation(cameraPosition));
        shader.Set("uBoardClip3", Board[3].RelativeEquation(cameraPosition));
    }
}
