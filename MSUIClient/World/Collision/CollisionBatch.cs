using System.Numerics;

namespace MSUIClient.World.Collision;

/// <summary>
/// One placed thing's contribution to the collision world, as a REFERENCE to
/// immutable model geometry plus where to put it - not as expanded triangles.
///
/// WHY THIS EXISTS
///   Rebuilding collision used to expand ~509,000 triangles on the render
///   thread: for every placed WMO and doodad, transform each of its collision
///   vertices into world space and append. The hitch recorder measured that at
///   92.9 ms, and - worse - it fired on a timer every few seconds while doodads
///   streamed in, when the actual change was a handful of new props. Half a
///   million transforms to append a rounding error, while nothing on screen
///   changed. That is the "what is being forced onto processing" answer.
///
///   The expansion itself was never the reason it had to be on the main thread.
///   The ownership rule (handbook 5.4) forbids a worker reading live renderer
///   placement collections while they mutate - and that applies to the LIST, not
///   to the geometry. A model's <c>CollisionTriangles</c> is immutable once
///   loaded. So the main thread now copies only the list (a few thousand of
///   these structs, sub-millisecond) and the worker does every transform.
///
/// The array is shared, never copied and never written to. Treat it as
/// read-only: many instances of the same model reference the same one.
/// </summary>
/// <param name="Triangles">Model-space collision vertices, three per triangle.</param>
/// <param name="Transform">Model to world.</param>
/// <param name="Path">Asset path; the source label is derived on the worker.</param>
/// <param name="Skipped">Detail triangles this model excluded, for reporting.</param>
public readonly record struct CollisionBatch(
    Vector3[] Triangles,
    Matrix4x4 Transform,
    string Path,
    int Skipped);
