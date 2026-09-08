using System.Numerics;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool GroundPointAcceptable(uint spellId, ulong actor, Vector3 point)
    {
        if (!TryGetWorldBodyPose(actor, out WorldBodyPose pose)) return false;
        SpellRangeRow? range = _spellCatalog?.TryGet(spellId, out SpellInfo spell) == true &&
            _spellCatalog.TryGetRange(spell.RangeIndex, out SpellRangeRow raw)
                ? ActorSpellRange(spell, actor, raw) : null;
        return GroundTargetingLaw.InRange(range, pose.Position, point);
    }

    private float GroundMarkerRadius(in SpellInfo spell, ulong actor)
    {
        // GetCurrentCastRadius reads the first two radius indices; effect slot 2 is not
        // part of the native targeting decal. Tactical affected-area overlays are separate.
        float radius = 0f;
        uint level = _entities.TryGet(actor, out var body) ? body.Level : 1;
        if (spell.EffectRadiusIndices is { } indices)
            for (int slot = 0; slot < Math.Min(2, indices.Length); slot++)
                if (_spellCatalog?.TryGetRadius(indices[slot], out SpellRadiusRow row) == true)
                    radius = Math.Max(radius, row.Radius + level * row.RadiusPerLevel);
        return ActorSpellModifiers(actor, spell, MSUIClient.Net.SpellModifierStore.Radius).ApplyFloat(radius);
    }

    private void RenderGroundTargetingMarker()
    {
        if (_groundCastSpell == 0 || _groundCursorPoint is not { } point ||
            _spellCatalog?.TryGet(_groundCastSpell, out SpellInfo spell) != true) return;
        bool acceptable = GroundPointAcceptable(spell.Id, ControlledGuid, point);
        _spellEffectMeshes?.RenderTargetingMarker(_window.Camera, point,
            GroundTargetingLaw.Radius(acceptable, GroundMarkerRadius(spell, ControlledGuid)), acceptable);
    }

    /// <summary>One ground-decal seam for outdoor terrain and indoor collision floors.</summary>
    private void GatherGroundEffectTriangles(float minX, float minY, float minZ,
        float maxX, float maxY, float maxZ,
        List<(Vector3 A, Vector3 B, Vector3 C)> output)
    {
        _terrain?.GatherGroundTriangles(minX, minY, maxX, maxY, output);
        _collision?.GatherWalkableTriangles(minX, minY, minZ, maxX, maxY, maxZ, output);
    }
}
