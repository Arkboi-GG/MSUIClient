using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MSUIClient.Engine;

/// <summary>
/// A saved viewpoint: everything that determines what is on screen except the
/// geometry itself - world position, camera, time-of-day and every scene toggle.
/// Load one and the frame is reproduced exactly, so a screenshot from Nico and a
/// scene dump for the assistant describe the same instant and can both be lined
/// up against the real 1.12 client. See FOUNDATION_PLAN.md and PLAN_01_VANTAGES.md.
///
/// This is a plain serializable object on purpose: the scene dump (step 2) embeds
/// a Vantage as its reproducible half. Property defaults match the renderer and
/// atmosphere defaults, so a hand-edited partial vantages.json still loads sanely.
/// </summary>
public sealed class Vantage
{
    public string Name { get; set; } = "";

    // World placement.
    public int Map { get; set; }
    public string MapName { get; set; } = "Azeroth";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public bool Flying { get; set; }

    // Camera. Facing is the character yaw; OrbitYaw is the camera-only offset.
    public float Facing { get; set; }
    public float OrbitYaw { get; set; }
    public float Pitch { get; set; } = 0.35f;
    public float Distance { get; set; } = 9f;
    public float Fov { get; set; } = 70f;
    public float FarPlane { get; set; } = 2000f;

    // Atmosphere / lighting.
    public float TimeOfDay { get; set; } = 12f;
    public bool DynamicLighting { get; set; } = true;
    public bool FogEnabled { get; set; } = true;
    public bool CullAtFogEnd { get; set; } = true;
    public float FogStart { get; set; } = 350f;
    public float FogEnd { get; set; } = 777f;
    public float SunStrength { get; set; } = 1f;
    public float AmbientStrength { get; set; } = 1f;

    // Loop-owned lighting flags.
    public bool CycleTimeOfDay { get; set; }
    public bool CoupleFarPlaneToFog { get; set; } = true;
    public float GameHoursPerMinute { get; set; } = 1f;

    // WMO visibility set (mirrors the HUD's building controls).
    public bool WmoEnabled { get; set; } = true;
    public bool WmoFrustumCulling { get; set; } = true;
    public bool UseDistanceLodShells { get; set; } = true;
    /// <summary>
    /// Defaults OFF, matching the renderer and the settings default.
    ///
    /// WORTH KNOWING WHEN A VANTAGE LOOKS SLOW: vantages.json stores this per
    /// saved vantage, and every vantage captured before the default changed has
    /// it recorded as true. Loading one of those turns backface culling off for
    /// the whole WMO pass again, so an old vantage will measure much worse than
    /// live play at the same spot. Re-capture, or edit the field, before reading
    /// anything into the difference.
    /// </summary>
    public bool WmoForceTwoSided { get; set; }
    public bool WmoOcclusionCulling { get; set; }
    public bool WmoVisTrace { get; set; }
    public bool WmoDumpGroups { get; set; }
    public float WmoInsideInstanceMargin { get; set; }
    public float WmoInteriorCullDistance { get; set; } = 120f;
    public float WmoShellNearGuard { get; set; } = 196f;
    public float WmoDrawDistance { get; set; } = 777f;
    public float WmoOcclusionMinDistance { get; set; } = 40f;
    public float WmoAlphaCutoff { get; set; } = 0.35f;
    public int WmoImpostorMaxVertices { get; set; } = 2000;

    // Doodad visibility set.
    public bool DoodadEnabled { get; set; } = true;
    public bool DoodadFrustumCulling { get; set; } = true;
    public bool DoodadUseInstancing { get; set; } = true;
    public float DoodadDrawDistance { get; set; } = 300f;
    public float DoodadAlphaCutoff { get; set; } = 0.5f;
}

/// <summary>
/// Loads and saves named <see cref="Vantage"/>s to vantages.json at the repo root
/// (the same location convention as client-config.json). The file is
/// human-readable and hand-editable, and is meant to be committed so useful spots
/// are shared. Never throws on read: a missing or malformed file starts empty.
/// </summary>
public sealed class VantageStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly List<Vantage> _vantages;

    private VantageStore(string path, List<Vantage> vantages)
    {
        _path = path;
        _vantages = vantages;
    }

    /// <summary>Every saved vantage, in file order.</summary>
    public IReadOnlyList<Vantage> All => _vantages;

    /// <summary>Read vantages.json from the repo root, or start empty.</summary>
    public static VantageStore Load(string repoRoot)
    {
        string path = Path.Combine(repoRoot, "vantages.json");
        var list = new List<Vantage>();

        try
        {
            if (File.Exists(path))
            {
                var parsed = JsonSerializer.Deserialize<List<Vantage>>(File.ReadAllText(path), Options);
                if (parsed is not null) list = parsed;
                Console.WriteLine($"[vantage] {list.Count} saved vantage(s) in {path}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[vantage] could not read {path} - starting empty ({ex.Message})");
            list = new List<Vantage>();
        }

        return new VantageStore(path, list);
    }

    /// <summary>Find a vantage by name (case-insensitive), or null.</summary>
    public Vantage? Find(string name)
    {
        foreach (var v in _vantages)
            if (string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
                return v;
        return null;
    }

    /// <summary>Add, or replace an existing vantage of the same name, then persist.</summary>
    public void Upsert(Vantage vantage)
    {
        for (int i = 0; i < _vantages.Count; i++)
        {
            if (string.Equals(_vantages[i].Name, vantage.Name, StringComparison.OrdinalIgnoreCase))
            {
                _vantages[i] = vantage;
                Save();
                return;
            }
        }

        _vantages.Add(vantage);
        Save();
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_vantages, Options));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[vantage] could not write {_path} - {ex.Message}");
        }
    }
}
