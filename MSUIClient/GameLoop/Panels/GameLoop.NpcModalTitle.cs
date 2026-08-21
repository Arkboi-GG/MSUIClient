using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;

namespace MSUIClient;

public sealed partial class GameLoop
{
    /// <summary>
    /// NPC service-window names use GameFontNormal (gold). Keeping this in one
    /// seam prevents gossip, quest, vendor, trainer, and taxi headers from
    /// independently drifting back to GameFontHighlight/raw white.
    /// </summary>
    private static void DrawNpcModalTitle(ImDrawListPtr draw, string title,
        Vector2 center, float scale) =>
        GameText.DrawCentered(draw, "GameFontNormal", title, center, scale);
}
