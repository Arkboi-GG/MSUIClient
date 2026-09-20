using System.Numerics;
using ImGuiNET;
using MSUIClient.Creator.Sketch;
using MSUIClient.World.Spells;

namespace MSUIClient;

// ═══════════════════════════════════════════════════════════════════════════
// PLACE BY POINTING, WITH PEELABLE WALLS — the owner's "cube idea", twice:
//
//   "have the mouse highlight a cube, and use that as the start"
//   "We should be able to remove 1 layer at a time from the grid such that we
//    can control every wall of cube in any direction ... so I can collapse it
//    with ground based autocad esque controls"
//
// WHY PEELING IS THE WHOLE FEATURE. A cube of cells is ambiguous to click in:
// every screen pixel has a column of cells behind it and only the author knows
// which one they meant. Nearest-centre-on-screen guesses, and a guess is worse
// than a question. Peeling turns the guess into arithmetic - shave the box down
// to one slab and a pick has exactly one answer, the way a CAD tool works you
// down to a plane before it lets you draw on it.
//
// So the box has six walls and each one moves independently, one cell at a
// time. The lattice DRAWS the box that is left, so what you can see is exactly
// what you can pick, and "flat" collapses an axis to the layer you are on.
//
// HOW THE POINT IS PICKED, in two cases. FLAT: a real ray through the mouse
// (Camera.ScreenPointToRay) meeting the one collapsed plane - exact and O(1),
// and the reason "ground based" is literally the Up axis flattened. DEEP: no
// correct answer exists, so guess cheaply - project the grid corners inside the
// box and take the nearest, with a tight radius so a wild pick reads as "peel
// first" rather than as a wrong answer given confidently.
//
// Points are grid CORNERS, so an origin lands on a round 0.25 instead of the
// 0.125 that cell centres would force on you.
//
// Placing needs no live effect: it writes the piece's Origin directly, which is
// why it works while the loop is mid-animation and why the owner does not have
// to pause to use it.
// ═══════════════════════════════════════════════════════════════════════════
public sealed partial class GameLoop
{
    private bool _sketchPlaceMode;
    private Vector3? _sketchPlaceCell;          // caster-frame centre of the hovered cell
    private (int X, int Y, int Z)? _sketchPlaceIndex;

    private static readonly Vector4 SketchPlaceColour = new(1f, 0.85f, 0.35f, 0.95f);
    private static readonly Vector4 SketchPlaceGhost = new(1f, 0.85f, 0.35f, 0.30f);

    /// <summary>The picking box, in CELL indices about the caster's feet. Defaults to the
    /// volume a spell effect actually lives in: a couple of yards around, three up.</summary>
    private int _placeMinX = -8, _placeMaxX = 7;
    private int _placeMinY = -8, _placeMaxY = 7;
    private int _placeMinZ, _placeMaxZ = 11;

    private const int PlaceIndexLimit = 40;     // a sane cap so a held button cannot run away

    private float PlaceCellYards => MathF.Max(Settings.Creator.GridMinorSixthYard
        ? SpellEmitterGizmoLaw.SixthYardMinor
        : SpellEmitterGizmoLaw.DefaultMinorYards, 0.05f);

    /// <summary>True while the box is one cell thick on some axis - i.e. a plane, where a pick
    /// is unambiguous.</summary>
    private bool PlaceBoxIsFlat =>
        _placeMinX == _placeMaxX || _placeMinY == _placeMaxY || _placeMinZ == _placeMaxZ;

    private void ResetSketchPlaceBox()
    {
        float cell = PlaceCellYards;
        int reach = Math.Max((int)MathF.Round(2f / cell), 1);
        int rise = Math.Max((int)MathF.Round(3f / cell), 1);
        _placeMinX = -reach; _placeMaxX = reach - 1;
        _placeMinY = -reach; _placeMaxY = reach - 1;
        _placeMinZ = 0; _placeMaxZ = rise - 1;
    }

    /// <summary>Move one wall by one cell, never past its opposite wall: the box may collapse
    /// to a single layer but never inside out.</summary>
    private static void PeelWall(ref int near, ref int far, int delta, bool moveNear)
    {
        if (moveNear)
        {
            int wanted = Math.Clamp(near + delta, -PlaceIndexLimit, PlaceIndexLimit);
            near = Math.Min(wanted, far);
        }
        else
        {
            int wanted = Math.Clamp(far + delta, -PlaceIndexLimit, PlaceIndexLimit);
            far = Math.Max(wanted, near);
        }
    }

    /// <summary>Collapse an axis onto the cell currently hovered (or the piece's own), which is
    /// the "put me on this plane and stop guessing" move.</summary>
    private void FlattenPlaceAxis(int axis, SketchPiece piece)
    {
        float cell = PlaceCellYards;
        Vector3 at = _sketchPlaceCell ?? piece.Place.Origin;
        int index = (int)MathF.Floor((axis == 0 ? at.X : axis == 1 ? at.Y : at.Z) / cell);
        switch (axis)
        {
            case 0: _placeMinX = _placeMaxX = index; break;
            case 1: _placeMinY = _placeMaxY = index; break;
            default: _placeMinZ = _placeMaxZ = index; break;
        }
    }

    // ── the world pass ──────────────────────────────────────────────────────

    /// <summary>Draw the picking box, find the cell under the mouse, and on a click make it the
    /// selected piece's origin. Called from the gizmo pass, which owns the line list.</summary>
    private void UpdateSketchPlaceCube(List<GizmoLine> lines)
    {
        _sketchPlaceCell = null;
        _sketchPlaceIndex = null;
        if (!_sketchPlaceMode || !_sketchOpen || _controller is null || _creatorSpell is not { } doc) return;

        SketchDoc sketch = SketchForStage(doc, _sketchStage);
        if (SelectedSketchPiece(sketch) is not { } piece) return;

        float minor = PlaceCellYards;
        Vector3 feet = _controller.Position;
        float yaw = _controller.Yaw;
        var forward = new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0f);
        var left = new Vector3(-MathF.Sin(yaw), MathF.Cos(yaw), 0f);
        Vector3 up = Vector3.UnitZ;

        // The box IS the grid while placing: what you can see is what you can pick. No pocket -
        // the cells nearest the body are the ones a cast effect wants, and hiding them to keep
        // the character clear would hide the answer.
        SpellEmitterGizmoLaw.LatticeBox(lines, feet, yaw, minor, SpellEmitterGizmoLaw.MajorYards,
            _placeMinX, _placeMaxX, _placeMinY, _placeMaxY, _placeMinZ, _placeMaxZ,
            pocketRadius: 0f, pocketHeight: 0f);

        var io = ImGui.GetIO();
        if (io.WantCaptureMouse || _window.MouseCaptured || CreatorHandlesActive) return;

        Vector2 display = io.DisplaySize;
        Vector2 mouse = io.MousePos;

        Vector3? hit = PlaceBoxIsFlat
            ? PickFlatPlane(feet, forward, left, up, minor, mouse, display)
            : PickNearestCorner(feet, forward, left, up, minor, mouse, display);
        if (hit is not { } cellPoint) return;

        _sketchPlaceCell = cellPoint;
        var centreCell = cellPoint;

        Vector3 centre = feet + forward * centreCell.X + left * centreCell.Y + up * centreCell.Z;
        AddSketchCube(lines, centre, forward, left, up, minor * 0.5f, SketchPlaceColour);

        Vector3 current = feet + forward * piece.Place.Origin.X + left * piece.Place.Origin.Y +
                          up * piece.Place.Origin.Z;
        if (Vector3.DistanceSquared(current, centre) > 1e-4f)
        {
            AddSketchCube(lines, current, forward, left, up, minor * 0.32f, SketchPlaceGhost);
            lines.Add(new GizmoLine(current, centre, SketchPlaceGhost));
        }

        if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;
        piece.Place.Origin = centreCell;
        SketchChanged();
        _sketchStatus = $"{piece.Name} origin set to {centreCell.X:0.00} fwd, " +
                        $"{centreCell.Y:0.00} left, {centreCell.Z:0.00} up";
    }

    /// <summary>A real ray against the one collapsed plane, snapped to the grid and clamped to
    /// the box. Exact: there is only one plane, so there is only one answer.</summary>
    private Vector3? PickFlatPlane(Vector3 feet, Vector3 forward, Vector3 left, Vector3 up,
        float minor, Vector2 mouse, Vector2 display)
    {
        if (_window.Camera.ScreenPointToRay(mouse, display) is not { } ray) return null;

        // Which axis is flat, and where its plane sits in caster-frame yards.
        int axis = _placeMinX == _placeMaxX ? 0 : _placeMinY == _placeMaxY ? 1 : 2;
        int planeIndex = axis == 0 ? _placeMinX : axis == 1 ? _placeMinY : _placeMinZ;
        Vector3 normal = axis == 0 ? forward : axis == 1 ? left : up;
        float plane = planeIndex * minor;

        float denominator = Vector3.Dot(ray.Direction, normal);
        if (MathF.Abs(denominator) < 1e-5f) return null;             // looking along the plane
        // Distance along the ray to the plane, in yards of the caster frame.
        float t = (plane - Vector3.Dot(ray.Origin - feet, normal)) / denominator;
        if (t <= 0f) return null;                                     // behind the camera

        Vector3 world = ray.Origin + ray.Direction * t;
        Vector3 local = world - feet;
        var at = new Vector3(Vector3.Dot(local, forward), Vector3.Dot(local, left),
                             Vector3.Dot(local, up));

        int ix = Math.Clamp((int)MathF.Round(at.X / minor), _placeMinX, _placeMaxX + 1);
        int iy = Math.Clamp((int)MathF.Round(at.Y / minor), _placeMinY, _placeMaxY + 1);
        int iz = Math.Clamp((int)MathF.Round(at.Z / minor), _placeMinZ, _placeMaxZ + 1);
        if (axis == 0) ix = planeIndex;
        else if (axis == 1) iy = planeIndex;
        else iz = planeIndex;
        return new Vector3(ix * minor, iy * minor, iz * minor);
    }

    /// <summary>No plane to land on, so guess: the grid corner inside the box whose projection
    /// is nearest the mouse. Bounded by the box, which is what keeps it cheap.</summary>
    private Vector3? PickNearestCorner(Vector3 feet, Vector3 forward, Vector3 left, Vector3 up,
        float minor, Vector2 mouse, Vector2 display)
    {
        float best = float.MaxValue;
        Vector3? bestPoint = null;
        for (int ix = _placeMinX; ix <= _placeMaxX + 1; ix++)
        for (int iy = _placeMinY; iy <= _placeMaxY + 1; iy++)
        for (int iz = _placeMinZ; iz <= _placeMaxZ + 1; iz++)
        {
            var cell = new Vector3(ix * minor, iy * minor, iz * minor);
            Vector3 world = feet + forward * cell.X + left * cell.Y + up * cell.Z;
            if (!_window.Camera.TryWorldToScreen(world, display, out Vector2 pixel)) continue;
            float d = Vector2.DistanceSquared(pixel, mouse);
            if (d >= best) continue;
            best = d;
            bestPoint = cell;
        }
        // Tight on purpose: a wild pick in a deep box should read as "peel first".
        return best <= 34f * 34f ? bestPoint : null;
    }

    private static void AddSketchCube(List<GizmoLine> lines, Vector3 centre,
        Vector3 forward, Vector3 left, Vector3 up, float half, Vector4 colour)
    {
        Vector3 f = forward * half, l = left * half, u = up * half;
        Span<Vector3> corner = stackalloc Vector3[8];
        int n = 0;
        for (int sx = -1; sx <= 1; sx += 2)
        for (int sy = -1; sy <= 1; sy += 2)
        for (int sz = -1; sz <= 1; sz += 2)
            corner[n++] = centre + f * sx + l * sy + u * sz;

        // Corner index bits are (x, y, z) with z least significant, so an edge joins two
        // corners differing in exactly one bit: 1 for up, 2 for left, 4 for forward.
        for (int a = 0; a < 8; a++)
            foreach (int bit in stackalloc[] { 1, 2, 4 })
                if ((a & bit) == 0) lines.Add(new GizmoLine(corner[a], corner[a | bit], colour));
    }

    // ── the controls ────────────────────────────────────────────────────────

    /// <summary>The six walls, one cell at a time, plus flatten and reset. Drawn inside the
    /// PLACE card when placing is on.</summary>
    private void DrawSketchPlaceWalls(SketchPiece piece, float cs)
    {
        float cell = PlaceCellYards;
        ImGui.TextDisabled(PlaceBoxIsFlat
            ? "the grid is one layer thick - a pick has one answer"
            : "peel walls until one answer is left");
        CreatorHelp("Take layers off the grid until only the part you care about is left.\\n\\n" +
                    "A solid cube of cells cannot be clicked in without guessing: every point on " +
                    "screen has a whole column of cells behind it. Shave a wall down - or press " +
                    "flat to collapse an axis onto the layer you are on - and the pick becomes " +
                    "exact. The grid you can SEE is exactly the grid you can pick.");

        Wall("Forward", 0, ref _placeMinX, ref _placeMaxX);
        Wall("Left", 1, ref _placeMinY, ref _placeMaxY);
        Wall("Up", 2, ref _placeMinZ, ref _placeMaxZ);

        if (ImGui.SmallButton("whole grid back")) ResetSketchPlaceBox();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Put every wall back where it started.");

        void Wall(string label, int axis, ref int near, ref int far)
        {
            ImGui.PushID(axis);
            ImGui.TextDisabled($"{label,-8}");
            ImGui.SameLine(72f * cs);

            // The NEAR wall.
            if (ImGui.SmallButton("<##nearOut")) PeelWall(ref near, ref far, -1, moveNear: true);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Grow the {label.ToLowerInvariant()} side out one cell.");
            ImGui.SameLine();
            if (ImGui.SmallButton(">##nearIn")) PeelWall(ref near, ref far, +1, moveNear: true);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Peel one layer off the {label.ToLowerInvariant()} side.");

            ImGui.SameLine();
            ImGui.TextDisabled($"{near * cell,5:0.00} .. {(far + 1) * cell,5:0.00} yd");

            // The FAR wall.
            ImGui.SameLine();
            if (ImGui.SmallButton("<##farIn")) PeelWall(ref near, ref far, -1, moveNear: false);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Peel one layer off the far side.");
            ImGui.SameLine();
            if (ImGui.SmallButton(">##farOut")) PeelWall(ref near, ref far, +1, moveNear: false);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Grow the far side out one cell.");

            ImGui.SameLine();
            bool flat = axis == 0 ? _placeMinX == _placeMaxX
                : axis == 1 ? _placeMinY == _placeMaxY : _placeMinZ == _placeMaxZ;
            if (flat) ImGui.BeginDisabled();
            if (ImGui.SmallButton("flat")) FlattenPlaceAxis(axis, piece);
            if (flat) ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Collapse this axis onto the layer under your mouse, so there is " +
                                 "nothing in front of or behind what you are pointing at.");
            ImGui.PopID();
        }
    }
}
