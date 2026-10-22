using System.Numerics;
using ImGuiNET;
using MSUIClient.Net;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// World Builder: props quality of life (shared_docs/WORLD_BUILDER.md §4, "Buildings & props").
//
// Scattering small objects (random heading/size rolled per placement and shown in the cursor preview before the
// click), quick-search words, the selection ring for the selected building/prop or server object, and a live
// model preview of unsaved edits to the selected placement. Saving stays explicit (Save object changes).
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private const ulong WbDraftGhostKey = 0xB0B0_FFFF_FFFF_FFFEUL;

    private static readonly string[] WbModelKinds = { "Everything", "Buildings", "Props and small objects" };
    private static readonly string[] WbPropChips = { "crate", "barrel", "tree", "bush", "rock", "fence", "lamp", "cart", "tent", "sign", "bench", "flower" };
    private static readonly string[] WbBuildingChips = { "house", "inn", "farm", "tower", "barn", "bridge", "ruin", "mine", "dock" };

    private int _wbModelKind;
    private bool _wbScatterYaw;
    private float _wbScatterScale;
    private float _wbYawRoll, _wbScaleRoll = 1f;
    private readonly Random _wbScatterRng = new();
    private string? _wbDraftGhostSig;

    /// <summary>The next placement's random heading/size, drawn now so the cursor preview shows exactly what a click places.</summary>
    private void WbRollScatter()
    {
        _wbYawRoll = _wbScatterYaw ? (float)_wbScatterRng.NextDouble() * 360f : 0f;
        _wbScaleRoll = _wbScatterScale > 0f ? 1f + ((float)_wbScatterRng.NextDouble() * 2f - 1f) * _wbScatterScale : 1f;
    }

    /// <summary>Selection ring (and, for a building/prop, a preview of its unsaved field edits).</summary>
    private void WbDrawSelectionOverlay()
    {
        bool open = _creatorPanel == CreatorPanel.World;
        WorldPackClient.Placement? draft = null;
        if (open && _wbPage == "buildings" && _wbPlacementDraft is { } d && _wbState?.Placements.FirstOrDefault(p => p.Id == d.Id && !p.Deleted) is { } saved &&
            WbSignature(d) != WbSignature(saved) && d.MapId == _config.Start.Map)
            draft = d;
        if (draft is null)
        {
            if (_wbDraftGhostSig is not null) WbRemoveGhost(WbDraftGhostKey);
            _wbDraftGhostSig = null;
        }
        else if (WbSignature(draft) != _wbDraftGhostSig)
        {
            WbRemoveGhost(WbDraftGhostKey);
            _wbDraftGhostSig = WbAddGhost(WbDraftGhostKey, draft) ? WbSignature(draft) : null;
        }
        if (!open || _terrain is null) return;

        Vector3? at = null;
        string label = "";
        if (_wbPage == "buildings" && _wbState?.Placements.FirstOrDefault(p => p.Id == _wbSelected && !p.Deleted && p.MapId == _config.Start.Map) is { } sel)
        {
            var p = draft ?? sel;
            at = new Vector3(p.PosX, p.PosY, p.PosZ);
            label = Path.GetFileNameWithoutExtension(sel.ModelPath.Replace('\\', '/'));
        }
        else if (_wbPage == "gameobjects" && WbGoSpawnDoc(_wbGoSelected) is { } go)
        {
            at = WbGoPosition(_wbGoDraft ?? go);
            label = WbGoName(WbNpcUInt(go["id"]));
        }
        if (at is not { } c) return;
        var draw = ImGui.GetBackgroundDrawList();
        var display = ImGui.GetIO().DisplaySize;
        Vector2? prev = null;
        for (int i = 0; i <= 32; i++)
        {
            float a = i / 32f * MathF.Tau;
            float x = c.X + MathF.Cos(a) * 1.5f, y = c.Y + MathF.Sin(a) * 1.5f;
            float z = _terrain.SampleHeight(x, y) ?? c.Z;
            Vector2? px = _window.Camera.TryProjectToScreen(new Vector3(x, y, MathF.Max(z, c.Z) + 0.15f), display, out var pixel, out _) ? pixel : null;
            if (prev is { } p0 && px is { } p1) draw.AddLine(p0, p1, 0xF0FFFFFFu, 2f);
            prev = px;
        }
        if (_window.Camera.TryWorldToScreen(c + new Vector3(0, 0, 2.5f), display, out var top))
        {
            var size = ImGui.CalcTextSize(label);
            draw.AddText(top - new Vector2(size.X / 2, 0) + Vector2.One, 0xE0000000u, label);
            draw.AddText(top - new Vector2(size.X / 2, 0), 0xFFFFFFFFu, label);
        }
    }

    private int? _wbPlacementPendingSelect;

    /// <summary>Select a building/prop; unsaved field edits of the current one are only dropped after a "discard?" answer.</summary>
    private void WbSelectPlacement(int id)
    {
        if (id == _wbSelected) return;
        if (_wbPlacementDraft is { } d && _wbState?.Placements.FirstOrDefault(p => p.Id == d.Id && !p.Deleted) is { } saved &&
            WbSignature(d) != WbSignature(saved)) { _wbPlacementPendingSelect = id; return; }
        _wbMoveArmed = false; _wbSelected = id; _wbPlacementDraft = null;
    }

    private void DrawWbPlacementSwitchPrompt()
    {
        if (_wbPlacementPendingSelect is not { } next) return;
        ImGui.TextColored(new Vector4(1f, .72f, .3f, 1f), "The selected object has unsaved changes.");
        if (CreatorButton("Discard them")) { _wbPlacementDraft = null; _wbPlacementPendingSelect = null; _wbMoveArmed = false; _wbSelected = next; }
        ImGui.SameLine();
        if (CreatorButton("Keep editing")) _wbPlacementPendingSelect = null;
    }
}
