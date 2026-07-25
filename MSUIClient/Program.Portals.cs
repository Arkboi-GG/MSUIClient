using ImGuiNET;
using MSUIClient.World.Wmo;

namespace MSUIClient;

// ============================================================================
// The portal instrument (PLAN_10_WMO_PORTALS.md §6).
//
// Same rule as Program.DevTools.cs, Program.Hitch.cs and Program.LightProbe.cs:
// developer TOOLING. It reads renderer state and prints; core never depends on
// it, and nothing here culls anything.
//
// PLAN_10 D1 says the containing-group question comes first and gets confirmed
// on its own before any traversal exists, because every later symptom looks
// like a portal bug if this is wrong. This panel is that confirmation.
// ============================================================================
public sealed partial class GameLoop
{
    private bool _showPortalPolygons;
    private bool _portalDebugOnlyCameraWmo = true;

    /// <summary>
    /// Draw the portal quads, reusing the collision debug renderer's
    /// arbitrary-triangle path. Depth is off there, so doorways show through
    /// walls - which is what you want when checking whether one sits in the
    /// right opening.
    ///
    /// Defaults to the camera's own building: Stormwind alone has enough
    /// portals to fill the screen with quads and answer nothing.
    /// </summary>
    private void DrawPortalDebug()
    {
        if (!_showPortalPolygons || _wmo is null || _collisionDebug is null) return;

        var tris = _wmo.PortalDebugTriangles(_portalDebugOnlyCameraWmo);
        if (tris.Count >= 3) _collisionDebug.RenderHighlight(_window.Camera, tris);
    }

    private void DrawPortalPanel()
    {
        if (!ImGui.CollapsingHeader("Portals (PLAN_10)")) return;

        if (_wmo is null)
        {
            ImGui.TextDisabled("no WMO renderer");
            return;
        }

        var cell = _wmo.CameraGroup;
        if (cell is { } c)
        {
            ImGui.Text($"in: [{c.GroupIndex}] '{c.GroupName}'");
            ImGui.Text($"    {(c.IsInterior ? "INTERIOR" : "exterior")}   " +
                       $"{c.PortalCount} door(s)   volume {c.Volume:N0}");
            ImGui.TextDisabled($"    {Path.GetFileName(c.InstancePath)}");
        }
        else
        {
            ImGui.Text("in: outdoors");
        }

        // Group boxes NEST - a room inside a shell inside a district - so more
        // than one containing the camera is normal and expected. Seeing this
        // number lets the smallest-volume tie-break be checked rather than
        // trusted: walking into a room should raise it, not replace it.
        ImGui.TextDisabled($"    {_wmo.CameraGroupCandidates} group(s) contain the camera");

        ImGui.Separator();
        ImGui.TextWrapped(
            "PLAN_10 §7 step 1: the group must change AT the doorway walking in, " +
            "and return to 'outdoors' walking out. If it flips early or late, the " +
            "cell test is wrong and no traversal built on it can be right.");

        bool show = _showPortalPolygons;
        if (ImGui.Checkbox("Draw portal polygons", ref show)) _showPortalPolygons = show;
        ImGui.SameLine();
        bool onlyHere = _portalDebugOnlyCameraWmo;
        if (ImGui.Checkbox("this building only", ref onlyHere))
            _portalDebugOnlyCameraWmo = onlyHere;
        ImGui.TextDisabled("  doorways should stand IN the door openings. One lying in a");
        ImGui.TextDisabled("  floor or floating in a wall means the transform or vertex");
        ImGui.TextDisabled("  range is wrong - and no traversal built on it can be right.");

        if (ImGui.Button("Dump portal graph")) _wmo.DumpPortalGraph();

        ImGui.SameLine();
        if (ImGui.Button("Print camera cell"))
        {
            if (_wmo.CameraGroup is { } p)
                Console.WriteLine($"[portals] camera in [{p.GroupIndex}] '{p.GroupName}' " +
                                  $"{(p.IsInterior ? "INT" : "ext")} {p.PortalCount} door(s) " +
                                  $"volume {p.Volume:F0} of {Path.GetFileName(p.InstancePath)} " +
                                  $"({_wmo.CameraGroupCandidates} candidate(s))");
            else
                Console.WriteLine($"[portals] camera outdoors " +
                                  $"({_wmo.CameraGroupCandidates} candidate(s))");
        }
    }
}
