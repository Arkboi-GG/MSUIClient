using MSUIClient.Net;

namespace MSUIClient.World.Units;

public sealed partial class CreatureRenderer
{
    private sealed class CorpseRenderView
    {
        public readonly WorldEntity Entity = new();
        public CorpseAppearance Appearance;
    }
    private readonly Dictionary<ulong, CorpseRenderView> _corpseRenderViews = [];
    private readonly HashSet<ulong> _corpseViewSeen = [];
    private readonly List<ulong> _corpseViewStale = [];

    private void AppendCorpseRenderViews(IReadOnlyList<WorldEntity>? corpses, List<WorldEntity> destination)
    {
        _corpseViewSeen.Clear();
        if (corpses is not null)
            foreach (WorldEntity source in corpses)
            {
                var appearance = new CorpseAppearance(source.Fields);
                if (source.Type != ObjectTypeId.Corpse || !appearance.CanRenderBody) continue;
                if (!_corpseRenderViews.TryGetValue(source.Guid, out var view))
                    _corpseRenderViews[source.Guid] = view = new CorpseRenderView();
                view.Entity.Guid = source.Guid;
                view.Appearance = appearance;
                appearance.UpdateRenderView(view.Entity);
                destination.Add(view.Entity);
                _corpseViewSeen.Add(source.Guid);
            }
        _corpseViewStale.Clear();
        foreach (ulong guid in _corpseRenderViews.Keys)
            if (!_corpseViewSeen.Contains(guid)) _corpseViewStale.Add(guid);
        foreach (ulong guid in _corpseViewStale) _corpseRenderViews.Remove(guid);
    }

    private bool TryCorpseAppearance(WorldEntity entity, out CorpseAppearance appearance)
    {
        if (_corpseRenderViews.TryGetValue(entity.Guid, out var view) && ReferenceEquals(entity, view.Entity))
        {
            appearance = view.Appearance;
            return true;
        }
        appearance = default;
        return false;
    }
}
