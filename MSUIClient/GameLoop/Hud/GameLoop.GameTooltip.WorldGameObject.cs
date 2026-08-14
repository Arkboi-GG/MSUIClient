using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

// World-gameobject hover tooltip (2026-08-12). The shared-GameTooltip law
// already carried a conditional GO responder (TryShowWorldGameObjectGameTooltip)
// waiting on "a stable GO picker/cursor verdict"; PickGameObject in
// Program.GameObjectRender.cs now supplies that verdict, and this adapter is
// the world-unit adapter's shape applied to it: consume the hover, publish the
// name, fade on leave. No live-health channel — a mailbox has no health bar —
// and no requirement lines yet (lock/skill lines are a later slice).
public sealed partial class GameLoop
{
    private sealed record WorldGameObjectTooltipRuntime(
        ulong Guid,
        GameTooltipOwnerToken Token,
        string Name,
        bool Hovering);

    private WorldGameObjectTooltipRuntime? _worldGameObjectTooltip;

    private bool UpdateAndQueueWorldGameObjectGameTooltip(double now)
    {
        if (!_sharedTooltipFrameOpen || _sharedTooltipFrameResolved) return false;

        // `_hoveredGameObjectGuid` is the PickGameObject verdict from
        // UpdateTargeting; it is exclusive with the unit hover by construction
        // (nonzero only when the GO hit was strictly nearer than any unit hit).
        // The NAME comes from the same template cache the gathering snapshot
        // uses; a miss issues one CMSG_GAMEOBJECT_QUERY per entry and shows
        // NOTHING until the response lands — vanilla's cache-miss behaviour.
        WorldEntity? hovered = null;
        string name = "";
        if (_hoveredGameObjectGuid != 0 &&
            _entities.TryGet(_hoveredGameObjectGuid, out WorldEntity candidate) &&
            candidate.IsGameObject)
        {
            RequireGameObjectTemplate(candidate);
            if (_gameObjectTemplates.TryGetValue(candidate.Entry, out var template) &&
                template.Name.Length > 0)
            {
                hovered = candidate;
                name = template.Name;
            }
        }

        if (hovered is not null)
        {
            GameTooltipRuntimeSnapshot shared = SharedGameTooltipSnapshot();
            bool exactOwner = _worldGameObjectTooltip is { } current &&
                current.Guid == hovered.Guid && SharedGameTooltipIsOwned(current.Token);
            bool fading = exactOwner && shared.Lifecycle.FadeStartedAt is not null;
            bool rebuild = !exactOwner || !_worldGameObjectTooltip!.Hovering ||
                _worldGameObjectTooltip.Name != name || fading;

            if (rebuild)
            {
                // Name line only, gold, at the frozen default bottom-right
                // anchor — the same anchor the world-unit tooltip publishes.
                var snapshot = new GameTooltipGameObjectSnapshot(
                    name, Lines: [], CursorAnchored: false);
                if (!TryShowWorldGameObjectGameTooltip(hovered.Guid, snapshot,
                        cursor: null, out GameTooltipOwnerToken token))
                {
                    _worldGameObjectTooltip = null;
                    return false;
                }
                _worldGameObjectTooltip = new(hovered.Guid, token, name, Hovering: true);
            }
            else if (_worldGameObjectTooltip is { } retained)
            {
                _worldGameObjectTooltip = retained with { Hovering = true };
            }
        }
        else
        {
            if (_worldGameObjectTooltip is not { } departing) return false;
            if (!SharedGameTooltipIsOwned(departing.Token))
            {
                _worldGameObjectTooltip = null;
                return false;
            }

            _worldGameObjectTooltip = departing with { Hovering = false };
            // Same leave law as the world-unit tooltip: content freezes and
            // fades over the frozen half-second window.
            BeginSharedGameTooltipFade(departing.Token, now,
                GameTooltipUiLaw.WorldFadeSeconds);
        }

        if (_worldGameObjectTooltip is not { } runtime ||
            !SharedGameTooltipIsOwned(runtime.Token))
        {
            _worldGameObjectTooltip = null;
            return false;
        }

        GameTooltipRuntimeSnapshot rendererSnapshot = SharedGameTooltipSnapshot();
        PreparedSharedGameTooltipRenderer? prepared =
            PrepareSharedGameTooltipRenderer(rendererSnapshot);
        if (prepared is null) return false;
        return QueueSharedGameTooltipRenderer(runtime.Token,
            SharedGameTooltipLeavePolicy.Fade(GameTooltipUiLaw.WorldFadeSeconds),
            () => DrawPreparedSharedGameTooltip(prepared));
    }
}
