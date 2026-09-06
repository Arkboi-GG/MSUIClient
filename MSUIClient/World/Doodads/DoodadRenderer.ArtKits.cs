using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.World.Doodads;

public sealed partial class DoodadRenderer
{
    private readonly Dictionary<ulong, GameObjectArtKit> _dynamicArtKits = [];
    private readonly List<ulong> _artKitOwnerScratch = [];
    private readonly Dictionary<(ulong Owner, int Slot), (Model Model, Instance Instance)> _dynamicArtKitParts = [];

    /// <summary>Art-kit children have presentation identities, never server GUIDs, collision or
    /// independent picking. Model caches stay shared; each owner's pose and lifetime stay separate.</summary>
    public void SyncDynamicArtKit(ulong owner, GameObjectArtKit? kit)
    {
        if (!_dynamicByKey.TryGetValue(owner, out var parent) || kit is null ||
            parent.Model.AttachmentSource is not { } source || parent.Model.AttachmentSkin is not { } skin)
        {
            RemoveDynamicArtKit(owner);
            return;
        }
        _dynamicArtKits[owner] = kit;
        DynamicAnimation? oneShot = parent.Instance.OneShot;
        if (oneShot is not null && NowSeconds - oneShot.StartedAt >= oneShot.DurationSeconds) oneShot = null;
        EvaluateAnimationMatrices(parent.Model, source, oneShot, parent.Instance.StateAnimation, skin);
        for (int slot = 0; slot < 4; slot++)
        {
            string path = slot < kit.Attachments.Count ? kit.Attachments[slot] : "";
            var key = (owner, slot);
            if (string.IsNullOrWhiteSpace(path) ||
                !GameObjectArtKitLaw.TryAttachmentTransform(source, slot, skin, parent.Instance.Transform, out var transform))
            {
                RemoveArtKitPart(key);
                continue;
            }
            if (_dynamicArtKitParts.TryGetValue(key, out var old))
            {
                if (ModelCacheKey(old.Instance.Path) == ModelCacheKey(path))
                {
                    UpdateArtKitTransform(old.Model, old.Instance, transform);
                    continue;
                }
                RemoveArtKitPart(key);
            }
            Model? model = ResolveModel(path);
            if (model is null)
            {
                if (!_models.ContainsKey(ModelCacheKey(path))) QueuePreloadModel(path, 0f, "gameobject-artkit");
                continue;
            }
            var (min, max) = TransformedBounds(model, transform);
            var instance = new Instance
            {
                Transform = transform, WorldMin = min, WorldMax = max, Path = path,
                DynamicGuid = 0xFFFF_0000_0000_0000UL | (_nextDynamicWmoPropIdentity++ & 0x0000_FFFF_FFFF_FFFFUL),
                Light = parent.Instance.Light, CosmeticOnly = true,
            };
            if (!_byModel.TryGetValue(model, out var list)) _byModel[model] = list = [];
            list.Add(instance); CullBoundsFor(model).Add(new CullBounds(min, max));
            _dynamicArtKitParts[key] = (model, instance);
            InstanceCount++; TotalTriangles += model.TriangleCount;
            if (instance.Light.W < .5f) InteriorLitCount++;
        }
    }

    private void UpdateArtKitTransform(Model model, Instance instance, Matrix4x4 transform)
    {
        var (min, max) = TransformedBounds(model, transform);
        instance.Transform = transform; instance.WorldMin = min; instance.WorldMax = max;
        if (_byModel.TryGetValue(model, out var list) && _cullBounds.TryGetValue(model, out var bounds))
        {
            int index = list.IndexOf(instance);
            if (index >= 0 && index < bounds.Count) bounds[index] = new CullBounds(min, max);
        }
    }

    private void RemoveArtKitPart((ulong Owner, int Slot) key)
    {
        if (_dynamicArtKitParts.Remove(key, out var part)) RemoveDynamicInstance(part.Model, part.Instance);
    }

    private void UpdateDynamicArtKitPoses()
    {
        _artKitOwnerScratch.Clear();
        _artKitOwnerScratch.AddRange(_dynamicArtKits.Keys);
        foreach (ulong owner in _artKitOwnerScratch)
            if (_dynamicArtKits.TryGetValue(owner, out var kit)) SyncDynamicArtKit(owner, kit);
    }

    private void RemoveDynamicArtKit(ulong owner)
    {
        _dynamicArtKits.Remove(owner);
        for (int slot = 0; slot < 4; slot++) RemoveArtKitPart((owner, slot));
    }
}
