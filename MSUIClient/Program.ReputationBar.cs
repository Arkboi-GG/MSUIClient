using System.Globalization;
using System.Numerics;
using ImGuiNET;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private void DrawReputationWatchBar(ImDrawListPtr dl, Vector2 mainBarMin, float s)
    {
        if (_gameplayArt is null) return;
        Vector2 root = mainBarMin + new Vector2(0, -8) * s;
        BeginUiParityFrame(root, s);
        CollectUiParity("ReputationWatchBar", "Frame", root, new Vector2(1024, 11) * s,
            parent: "", point: "BOTTOM", relativeTo: "MainMenuBar", relativePoint: "TOP",
            offsetX: "0", offsetY: "-3", strata: "");
        CollectUiParity("ReputationWatchStatusBar", "StatusBar", root, new Vector2(1024, 8) * s,
            parent: "ReputationWatchBar", point: "TOP",
            texture: @"Interface\TargetingFrame\UI-StatusBar", layer: "ARTWORK", strata: "");

        Vector2 size = new(1024, 8);
        dl.AddRectFilled(root, root + size * s, 0x80000000u);
        DrawVanillaStatusBar(dl, root, size * s, .65f, new Vector4(.05f, .6f, .1f, 1));

        uint reputation = _gameplayArt.Handle(@"Interface\PaperDollInfoFrame\UI-ReputationWatchBar");
        string[] repTex = ["0|0|1.0|0.171875", "0|0.171875|1.0|0.34375",
            "0|0.34375|1.0|0.515625", "0|0.515625|1.0|0.6875"];
        for (int i = 0; i < 4; i++)
        {
            Vector2 min = root + new Vector2(i * 256, -2) * s;
            float top = i * .171875f;
            if (reputation != 0) dl.AddImage((nint)reputation, min, min + new Vector2(256, 11) * s,
                new Vector2(0, top), new Vector2(1, top + .171875f));
            CollectUiParity($"ReputationWatchBarTexture{i}", "Texture", min, new Vector2(256, 11) * s,
                parent: "ReputationWatchStatusBar", point: i == 0 ? "TOPLEFT" : "LEFT",
                relativeTo: i == 0 ? "" : $"ReputationWatchBarTexture{i - 1}",
                relativePoint: i == 0 ? "" : "RIGHT", offsetX: i == 0 ? "0" : "",
                offsetY: i == 0 ? "2" : "", texture: @"Interface\PaperDollInfoFrame\UI-ReputationWatchBar",
                layer: "OVERLAY", strata: "", texCoords: repTex[i]);
        }

        uint dwarf = _gameplayArt.Handle(@"Interface\MainMenuBar\UI-MainMenuBar-Dwarf");
        float[] dwarfTop = [.79296875f, .54296875f, .29296875f, .04296875f];
        string[] dwarfTex = ["0|0.79296875|1.0|0.83203125", "0|0.54296875|1.0|0.58203125",
            "0|0.29296875|1.0|0.33203125", "0|0.04296875|1.0|0.08203125"];
        for (int i = 0; i < 4; i++)
        {
            Vector2 min = root + new Vector2(i * 256, 0) * s;
            if (dwarf != 0) dl.AddImage((nint)dwarf, min, min + new Vector2(256, 10) * s,
                new Vector2(0, dwarfTop[i]), new Vector2(1, dwarfTop[i] + .0390625f));
            CollectUiParity($"ReputationXPBarTexture{i}", "Texture", min, new Vector2(256, 10) * s,
                parent: "ReputationWatchStatusBar", point: i == 0 ? "TOPLEFT" : "LEFT",
                relativeTo: i == 0 ? "" : $"ReputationXPBarTexture{i - 1}",
                relativePoint: i == 0 ? "" : "RIGHT", offsetX: i == 0 ? "0" : "",
                offsetY: i == 0 ? "0" : "", texture: @"Interface\MainMenuBar\UI-MainMenuBar-Dwarf",
                layer: "OVERLAY", strata: "", texCoords: dwarfTex[i]);
        }

        CollectUiParity("ReputationWatchStatusBarBackground", "Texture", root, new Vector2(1024, 8) * s,
            parent: "ReputationWatchStatusBar", layer: "BACKGROUND", strata: "");
        CollectUiParity("ReputationWatchBarOverlayFrame", "Frame", root, new Vector2(1024, 11) * s,
            parent: "ReputationWatchBar", strata: "DIALOG");
        MarkUiParityFrameComplete();
    }
}
