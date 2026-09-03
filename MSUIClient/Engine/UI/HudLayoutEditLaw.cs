using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>One frame's before/after inside an undoable change. Null = no override (authored).</summary>
public readonly record struct HudEditEntry(string FrameId, HudLayoutContext Context,
    HudPlacement? Before, HudPlacement? After);

/// <summary>An undo step: one or more frame edits applied together (a drag, a nudge, Reset all).</summary>
public sealed record HudEditChange(IReadOnlyList<HudEditEntry> Entries);

/// <summary>
/// One Edit Mode session. Entering snapshots the whole HudLayout settings block so Revert can
/// put everything back (including a Default -> Custom fork made mid-session); Save simply keeps
/// the live block. Undo/redo is a linear stack of <see cref="HudEditChange"/>. Drag state lives
/// here so the overlay is a pure function of (frames this frame, session).
/// </summary>
public sealed class HudEditSession
{
    public HudEditSession(HudLayoutSettings snapshot, HudLayoutContext context, string? selected)
    {
        Snapshot = snapshot;
        Context = context;
        Selected = selected;
    }

    public HudLayoutSettings Snapshot { get; }
    public HudLayoutContext Context { get; set; }
    public string? Selected { get; set; }
    public string? Dragging { get; set; }
    public Vector2 DragStartOrigin { get; set; }
    public Vector2 DragStartMouse { get; set; }
    public HudPlacement? DragBefore { get; set; }
    public bool FrameListOpen { get; set; }
    /// <summary>Where the player parked the selection card (logical top-left), or null for
    /// the automatic "edge farthest from the selection" placement.</summary>
    public Vector2? CardOrigin { get; set; }

    private readonly List<HudEditChange> _changes = [];
    private int _cursor;

    public bool CanUndo => _cursor > 0;
    public bool CanRedo => _cursor < _changes.Count;
    public int ChangeCount => _changes.Count;

    public void Push(HudEditChange change)
    {
        if (_cursor < _changes.Count) _changes.RemoveRange(_cursor, _changes.Count - _cursor);
        _changes.Add(change);
        _cursor = _changes.Count;
    }

    public HudEditChange? Undo() => CanUndo ? _changes[--_cursor] : null;
    public HudEditChange? Redo() => CanRedo ? _changes[_cursor++] : null;
}

/// <summary>Pure edit-session law: begin, apply/undo changes against a live settings block.</summary>
public static class HudLayoutEditLaw
{
    public static HudEditSession Begin(HudLayoutSettings live, HudLayoutContext context, string? select)
        => new(live.Clone(), context, select);

    /// <summary>Set (or clear, with null) one frame's override and return the undo entry.</summary>
    public static HudEditChange SetPlacement(HudLayoutSettings live, HudLayoutContext context,
        string frameId, HudPlacement? after)
    {
        HudPlacement? before = HudLayoutLaw.Override(live, context, frameId);
        Write(live, context, frameId, after);
        return new HudEditChange([new HudEditEntry(frameId, context, before, after)]);
    }

    /// <summary>Clear every override in one context; null when there was nothing to clear.</summary>
    public static HudEditChange? ResetAll(HudLayoutSettings live, HudLayoutContext context)
    {
        Dictionary<string, HudPlacement>? overrides = HudLayoutLaw.Overrides(live, context);
        if (overrides is null || overrides.Count == 0) return null;
        var entries = new List<HudEditEntry>(overrides.Count);
        foreach ((string id, HudPlacement before) in overrides)
            entries.Add(new HudEditEntry(id, context, before, null));
        overrides.Clear();
        return new HudEditChange(entries);
    }

    /// <summary>Re-apply a change forwards (redo) or backwards (undo).</summary>
    public static void Apply(HudLayoutSettings live, HudEditChange change, bool undo)
    {
        foreach (HudEditEntry e in change.Entries)
            Write(live, e.Context, e.FrameId, undo ? e.Before : e.After);
    }

    private static void Write(HudLayoutSettings live, HudLayoutContext context, string frameId,
        HudPlacement? placement)
    {
        if (placement is null)
        {
            HudLayoutLaw.Overrides(live, context)?.Remove(frameId);
            return;
        }
        HudLayoutLaw.EnsureEditable(live).For(context)[frameId] = placement;
    }

    /// <summary>The proposed logical origin of the dragged frame for the current pointer.</summary>
    public static Vector2 DragOrigin(HudEditSession session, Vector2 mouse, float scale)
        => session.DragStartOrigin + (mouse - session.DragStartMouse) / MathF.Max(.01f, scale);

    /// <summary>The settings card sits at the screen edge FARTHEST from the selection.</summary>
    public static bool CardOnLeft(Vector2 selectionCentre, Vector2 display)
        => selectionCentre.X > display.X * .5f;
}
