using System.Globalization;
using System.Numerics;
using System.Text.Json;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Tabletop;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Quest Tabletop P2 - the real world on the table (shared_docs/QUEST_TABLETOP.md §4): the near layer (a world of its
// own: terrain, WMO buildings, M2 props, water) streamed around the camera's ground target with the table's
// exaggeration and board cut, plus the real creature models standing where the world DB spawns them. Creature spawns
// come read-only from the web app's census (GET /QuestAtlas/Creatures for the board); their models from the client's
// own creature catalogue (creator-creatures.tsv: entry → display id). Creatures are view-only synthetic units owned by
// the table - never added to the game's entity store.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private TabletopNearScene? _ttNear;
    private bool _ttNearFailed;
    private readonly List<(WorldEntity Unit, Vector3 Spawn)> _ttCreatures = new();
    private readonly List<WorldEntity> _ttUnitsThisFrame = new();
    private Task<List<(uint Entry, Vector3 Pos, float Facing, float Scale)>>? _ttCensus;
    private string _ttCensusStatus = "";
    private double _ttLastRenderAt;

    /// <summary>A new board: point the near layer at its map and fetch the creatures standing on it.</summary>
    private void TabletopNearBoard(TabletopLaw.Board board)
    {
        if (!TabletopEnsureNear()) return;
        _ttNear!.SetMap(board.MapDirectory);
        _ttNear.View.SetBoard(board.Top, board.Bottom, board.Left, board.Right);
        _ttCreatures.Clear();
        _ttCensus = null;
        if (string.IsNullOrWhiteSpace(SuiWebAppUrl)) { _ttCensusStatus = "creatures: no web app configured"; return; }
        string url = string.Create(CultureInfo.InvariantCulture,
            $"{SuiWebAppUrl.TrimEnd('/')}/QuestAtlas/Creatures?mapId={board.MapId}&top={board.Top}&bottom={board.Bottom}&left={board.Left}&right={board.Right}");
        _ttCensusStatus = "creatures: loading...";
        _ttCensus = Task.Run(async () =>
        {
            using var http = WebAppHttp.Create(TimeSpan.FromSeconds(30));
            using var doc = JsonDocument.Parse(await http.GetStringAsync(url));
            var scales = new Dictionary<uint, float>();
            if (doc.RootElement.TryGetProperty("templates", out var templates))
                foreach (var t in templates.EnumerateObject())
                    if (uint.TryParse(t.Name, out uint entry))
                        scales[entry] = t.Value.TryGetProperty("scale", out var sc) && sc.TryGetSingle(out float s) && s > 0 ? s : 1f;
            var list = new List<(uint, Vector3, float, float)>();
            foreach (var row in doc.RootElement.GetProperty("spawns").EnumerateArray())
            {
                var a = row.EnumerateArray().Select(v => v.GetSingle()).ToArray();
                if (a.Length < 5) continue;
                uint entry = (uint)a[0];
                list.Add((entry, new Vector3(a[1], a[2], a[3]), a[4], scales.GetValueOrDefault(entry, 1f)));
            }
            return list;
        });
    }

    private bool TabletopEnsureNear()
    {
        if (_ttNear is not null) return true;
        if (_ttNearFailed || _gl is null || _uploads is null || _assetWorkers is null || _mpq is null) return false;
        try
        {
            _ttNear = new TabletopNearScene(_gl, _config, _uploads, _assetWorkers, _mpq, radius: Math.Max(1, _config.Start.TileRadius));
            return true;
        }
        catch (Exception e)
        {
            _ttNearFailed = true;
            Console.WriteLine($"[tabletop] near layer unavailable: {e.Message}");
            return false;
        }
    }

    /// <summary>Per frame: follow the ground target, advance the staged load, adopt the creature census.</summary>
    private void TabletopNearUpdate()
    {
        if (_ttNear is null || _tabletop?.Board is not { } board) return;
        _ttNear.View.K = _tabletop.Exaggeration;
        _ttNear.View.BaseZ = _tabletop.BaseZ;
        if (Settings.Creator.Tabletop.World)
        {
            var focus = new Vector2(Math.Clamp(_ttTarget.X, board.Bottom, board.Top), Math.Clamp(_ttTarget.Y, board.Right, board.Left));
            _ttNear.Focus(focus);
            _ttNear.Step();
        }

        if (_ttCensus is { IsCompleted: true } census)
        {
            _ttCensus = null;
            if (census.IsFaulted) { _ttCensusStatus = "creatures: " + census.Exception?.GetBaseException().Message; return; }
            EnsureCreatorCreatures();
            int missing = 0;
            ulong n = 0;
            foreach (var (entry, pos, facing, scale) in census.Result)
            {
                if (_creatorCreatures is null || !_creatorCreatures.TryGet(entry, out var c) || c.DisplayId == 0) { missing++; continue; }
                var fields = ObjectFields.ForSyntheticUnit((int)c.DisplayId, scale > 0 ? scale : c.Scale > 0 ? c.Scale : 1f);
                fields.SetU32(ObjectFields.OBJECT_ENTRY, entry);
                var unit = new WorldEntity
                {
                    Guid = 0xF130_7AB1_0000_0000UL | ++n, Type = ObjectTypeId.Unit, Entry = entry, Fields = fields,
                    Position = pos, Orientation = facing,
                };
                _ttCreatures.Add((unit, pos));
            }
            _ttCensusStatus = $"creatures: {_ttCreatures.Count} on the board" + (missing > 0 ? $" ({missing} without a model)" : "");
            Console.WriteLine($"[tabletop] {_ttCensusStatus}");
        }
    }

    /// <summary>Creatures inside the near layer's loaded tiles, standing on the shown (exaggerated) ground.</summary>
    private IReadOnlyList<WorldEntity> TabletopUnits()
    {
        _ttUnitsThisFrame.Clear();
        if (_ttNear is null || !Settings.Creator.Tabletop.Creatures || _ttCreatures.Count == 0) return _ttUnitsThisFrame;
        var tiles = _ttNear.ReadyTiles;
        if (tiles.Count == 0) return _ttUnitsThisFrame;
        foreach (var (unit, spawn) in _ttCreatures)
        {
            if (!tiles.Contains(TabletopLawTile(spawn))) continue;
            float ground = _ttNear.GroundAt(spawn.X, spawn.Y) ?? spawn.Z;
            unit.Position = spawn with { Z = spawn.Z + _ttNear.View.Lift(ground) };
            _ttUnitsThisFrame.Add(unit);
            if (_ttUnitsThisFrame.Count >= 1500) break;
        }
        return _ttUnitsThisFrame;
    }

    private static (int col, int row) TabletopLawTile(Vector3 p)
        => ((int)MathF.Floor(32f - p.Y / TabletopLaw.TileYards), (int)MathF.Floor(32f - p.X / TabletopLaw.TileYards));

    /// <summary>The table's warm light in the world renderers' terms (their colours are display values).</summary>
    private TabletopNearScene.NearLighting TabletopNearLighting()
    {
        float span = _tabletop?.Board?.Span ?? 3000f;
        var sun = Vector3.Normalize(TabletopLaw.SunOffset(span));
        // Fog fades to the table's background exactly as the far layer shows it (tone-mapped).
        var fog = TabletopLaw.ToneMap(TabletopLaw.SrgbToLinear(TabletopLaw.Rgb(TabletopLaw.Background)));
        var sky = TabletopLaw.Rgb(TabletopLaw.HemiSky);
        var ground = TabletopLaw.Rgb(TabletopLaw.HemiGround);
        return new TabletopNearScene.NearLighting(sun, TabletopLaw.Rgb(TabletopLaw.Sun), 0.95f,
            Vector3.Lerp(ground, sky, 0.7f), 0.55f, fog, span * TabletopLaw.FogNearSpans, span * TabletopLaw.FogFarSpans,
            TabletopLaw.ShownWater(sun.Z), TabletopLaw.WaterAlpha);
    }

    /// <summary>The near layer's draw, run inside the table's scene pass.</summary>
    private Action? TabletopNearDraw()
    {
        if (_ttNear is null || !Settings.Creator.Tabletop.World) return null;
        double now = NowSeconds();
        float dt = _ttLastRenderAt > 0 ? (float)Math.Clamp(now - _ttLastRenderAt, 0, 0.25) : 0f;
        _ttLastRenderAt = now;
        var units = TabletopUnits();
        var light = TabletopNearLighting();
        return () => _ttNear.Render(_ttCamera, light, units, dt);
    }
}
