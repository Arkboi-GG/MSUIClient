using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.World.Collision;
using MSUIClient.World.Doodads;
using MSUIClient.World.Wmo;

namespace MSUIClient.World.Portals;

/// <summary>
/// Transferable ownership of one fully constructed world-renderer set. A
/// bundle returned by <see cref="PortalDestinationScene.TryExchangePreparedWorld"/>
/// belongs to the caller. Conversely, the replacement passed to that method is
/// consumed on success and belongs to the destination scene until a later
/// exchange or disposal.
/// </summary>
public sealed class PortalWorldBundle
{
    private readonly HashSet<(int col, int row)> _ring;
    private bool _claimed;

    public PortalWorldBundle(
        TerrainRenderer terrain,
        WmoRenderer wmo,
        DoodadRenderer? doodads,
        LiquidRenderer liquid,
        AdtCache adts,
        WdtFile wdt,
        string mapName,
        (int col, int row) ringCenter,
        IEnumerable<(int col, int row)> ring,
        CollisionWorld? collision,
        Task? externalDrain = null)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(wmo);
        ArgumentNullException.ThrowIfNull(liquid);
        ArgumentNullException.ThrowIfNull(adts);
        ArgumentNullException.ThrowIfNull(wdt);
        ArgumentNullException.ThrowIfNull(ring);
        if (string.IsNullOrWhiteSpace(mapName))
            throw new ArgumentException("A world bundle requires a map directory", nameof(mapName));
        if (!string.Equals(adts.MapName, mapName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The ADT cache and bundle map directory do not match", nameof(adts));

        Terrain = terrain;
        Wmo = wmo;
        Doodads = doodads;
        Liquid = liquid;
        Adts = adts;
        Wdt = wdt;
        MapName = mapName;
        RingCenter = ringCenter;
        _ring = new HashSet<(int col, int row)>(ring);
        Collision = collision;
        ExternalDrain = externalDrain;
    }

    public TerrainRenderer Terrain { get; }
    public WmoRenderer Wmo { get; }
    public DoodadRenderer? Doodads { get; }
    public LiquidRenderer Liquid { get; }
    public AdtCache Adts { get; }
    public WdtFile Wdt { get; }
    public string MapName { get; }
    public (int col, int row) RingCenter { get; }
    public IReadOnlySet<(int col, int row)> Ring => _ring;
    public CollisionWorld? Collision { get; }

    /// <summary>
    /// Optional work which still reads this bundle's CPU-side world state, such
    /// as the active world's background ADT parse or collision build. Retirement
    /// observes completion before clearing that state and never waits in Step.
    /// </summary>
    public Task? ExternalDrain { get; }

    internal HashSet<(int col, int row)> OwnedRing => _ring;

    internal bool TryClaim()
    {
        if (_claimed) return false;
        _claimed = true;
        return true;
    }
}

/// <summary>
/// One isolated, reusable destination-world candidate. CPU workers, the shared
/// upload context and the stateless sky program are borrowed; terrain/WMO/M2
/// placement, collision and the preview framebuffer are private to this slot.
/// All public methods (including Dispose) belong to the constructing GL thread.
/// </summary>
public sealed class PortalDestinationScene : IDisposable
{
    private enum Phase
    {
        Idle,
        WarmGeometry,
        QueueDoodads,
        WarmDoodads,
        PlaceDoodads,
        Visual,
        Retiring,
        Failed,
        Disposed,
    }

    private readonly record struct CollisionBuild(
        int Generation,
        CollisionWorld World,
        bool ArrivalSupport);

    private readonly GL _gl;
    private readonly ClientConfig _config;
    private readonly AssetWorkerPool _workers;
    private readonly SkyRenderer _sky;
    private TerrainRenderer _terrain;
    private WmoRenderer _wmo;
    private DoodadRenderer? _doodads;
    private LiquidRenderer _liquid;
    private readonly PortalRenderTarget _target;
    private readonly Camera _camera = new();
    private readonly int _ownerThread;
    private readonly int _tileRadius;

    private Phase _phase;
    private AdtCache? _adts;
    private WdtFile? _wdt;
    private string? _mapName;
    private HashSet<(int col, int row)> _ring = [];
    private (int col, int row) _ringCenter;
    private Task<CollisionBuild>? _collisionTask;
    private Task? _externalDrain;
    private CollisionWorld? _collision;
    private int _generation;
    private bool _geometryReady;
    private bool _arrivalSupport;
    private float _previewTime;
    private bool _disposed;

    public PortalDestinationScene(
        GL gl,
        ClientConfig config,
        GpuUploadWorker uploads,
        AssetWorkerPool workers,
        SkyRenderer sky,
        VisibilityOverrides? visibilityOverrides = null,
        string? shaderDirectory = null,
        int targetWidth = 768,
        int targetHeight = 768,
        int tileRadius = 1)
    {
        _gl = gl;
        _config = config;
        _workers = workers;
        _sky = sky;
        _ownerThread = Environment.CurrentManagedThreadId;
        // The active world and its prepared replacement must cover the same
        // inner residency. Program historically passed a hard-coded one here;
        // retaining the parameter for compatibility while taking the larger
        // configured radius prevents promotion from revealing a thin ring.
        _tileRadius = Math.Clamp(Math.Max(tileRadius, config.Start.TileRadius), 0, 3);

        string shaders = shaderDirectory ?? ResolveShaderDirectory(config);
        _terrain = new TerrainRenderer(gl, config, uploads, workers);
        _wmo = new WmoRenderer(gl, config, uploads, workers)
        {
            Overrides = visibilityOverrides,
        };
        _liquid = new LiquidRenderer(gl);
        if (config.Render.Doodads)
        {
            _doodads = new DoodadRenderer(gl, config, uploads, workers)
            {
                DemandStreaming = true,
                DrawDistance = config.Render.DoodadDistance,
                CollisionBasisIndex = config.Render.DoodadCollisionBasis,
            };
            _doodads.PortalVisibility = _wmo.IsDoodadPortalVisible;
        }

        PortalRenderTarget? target = null;
        try
        {
            _terrain.LoadShaders(shaders);
            _wmo.LoadShaders(shaders);
            _doodads?.LoadShaders(shaders);
            _liquid.LoadShaders(shaders);
            _liquid.LoadLiquidTextures(config.ClientDataPath);
            target = new PortalRenderTarget(gl, targetWidth, targetHeight);
            _target = target;
        }
        catch
        {
            // A constructor which loses a shader/FBO allocation is otherwise
            // unreachable by the caller and therefore cannot be disposed. Keep
            // startup failure a clean feature fallback rather than a GL leak.
            target?.Dispose();
            _doodads?.Dispose();
            _liquid.Dispose();
            _wmo.Dispose();
            _terrain.Dispose();
            throw;
        }
    }

    public PortalDescriptor? Descriptor { get; private set; }
    public bool VisualGeometryReady => Descriptor is not null && _geometryReady && !Retiring;
    public bool HasCompleteFrame => Descriptor is not null && _target.HasCompleteFrame && !Retiring;
    public bool VisualReady => VisualGeometryReady && HasCompleteFrame;
    public bool ArrivalSupport => VisualGeometryReady && _arrivalSupport;
    public uint Texture => VisualReady ? _target.Texture : 0;
    public Vector2 TargetSize => new(_target.DesiredWidth, _target.DesiredHeight);
    /// <summary>Dimensions of the currently published front texture. These may
    /// briefly differ from TargetSize while a resize replacement is in flight.</summary>
    public Vector2 PublishedSize => new(_target.Width, _target.Height);
    public string? Failure { get; private set; }
    public bool Retiring => _phase == Phase.Retiring;
    public bool RetirementComplete => _phase == Phase.Idle;
    public bool IsActive => Descriptor is not null && !Retiring;

    /// <summary>
    /// Player-cell WMO context at the authoritative arrival pose. Consumers use
    /// this to prepare destination-only UI assets while the portal is still in
    /// its pre-animation, before this renderer bundle becomes the active world.
    /// </summary>
    public WmoRenderer.InteriorMinimapContext? ResolveArrivalInteriorMinimap(float radius)
    {
        if (!VisualGeometryReady || Descriptor is not { } descriptor) return null;
        return ResolvePreparedInteriorMinimap(descriptor.PreviewPosition, radius);
    }

    /// <summary>
    /// Resolve minimap membership at a concrete feet position in this prepared
    /// world. Promotion uses the server's eventual authoritative position here,
    /// so the descriptor's small handoff tolerance cannot cross an unprepared
    /// room or exterior boundary.
    /// </summary>
    public WmoRenderer.InteriorMinimapContext? ResolvePreparedInteriorMinimap(
        in Vector3 feet, float radius)
    {
        EnsureOwnerThread();
        ThrowIfDisposed();
        if (!VisualGeometryReady) return null;
        Vector3 probe = feet + new Vector3(0f, 0f, 1.7f);
        return _wmo.ResolveInteriorMinimapContext(
            probe, radius, _terrain.SampleHeight(probe.X, probe.Y));
    }

    /// <summary>
    /// Keep the preview target at the source viewport's aspect without paying
    /// full-window pixel cost. Allocation is deferred until the next render.
    /// </summary>
    public void ResizeTarget(int width, int height)
    {
        EnsureOwnerThread();
        ThrowIfDisposed();
        _target.Resize(width, height);
    }

    /// <summary>
    /// Re-key a still-loading/ready candidate after a preparation lease refresh
    /// without throwing away identical destination assets. Correlation changes
    /// are safe only when every field which affects candidate placement and the
    /// virtual exit frame is unchanged.
    /// </summary>
    public bool TryRefreshDescriptor(PortalDescriptor descriptor)
    {
        EnsureOwnerThread();
        ThrowIfDisposed();
        if (!descriptor.IsValid || Descriptor is not { } current ||
            _phase is Phase.Idle or Phase.Retiring or Phase.Disposed ||
            !SameDestination(current, descriptor))
            return false;

        Descriptor = descriptor;
        return true;
    }

    /// <summary>
    /// Atomically promote the prepared destination and put the caller's former
    /// active world into this slot for incremental retirement. Every validation
    /// happens before ownership changes: a mismatch leaves both worlds exactly
    /// where they were. The replacement bundle is consumed only on success.
    /// </summary>
    public bool TryExchangePreparedWorld(
        in PortalDescriptor expectedDescriptor,
        uint authoritativeMapId,
        in Vector3 authoritativePosition,
        PortalWorldBundle replacement,
        out PortalWorldBundle? prepared)
    {
        EnsureOwnerThread();
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(replacement);
        prepared = null;

        // Polling is bounded and may turn the last submitted frame/collision
        // result into READY. It never waits for either producer.
        _target.PollComplete();
        AcceptCollisionIfComplete();

        if (_phase != Phase.Visual || !VisualReady || !_arrivalSupport ||
            _collisionTask is not null || Descriptor is not { } current ||
            !SamePreparedDescriptorExact(current, expectedDescriptor) ||
            !PortalHandoffLaw.MatchesPreparedDestination(
                current.PreviewMapId, current.PreviewPosition,
                authoritativeMapId, authoritativePosition) ||
            !HasNearbyArrivalSupport(authoritativePosition) ||
            _adts is null || _wdt is null || string.IsNullOrWhiteSpace(_mapName) ||
            !ReplacementIsDisjoint(replacement))
            return false;

        // Construct the outgoing owner before touching either slot. In
        // particular, allocation failure cannot strand the scene rendererless.
        var outgoing = new PortalWorldBundle(
            _terrain, _wmo, _doodads, _liquid,
            _adts, _wdt, _mapName, _ringCenter, _ring, _collision);
        if (!replacement.TryClaim()) return false;

        _terrain = replacement.Terrain;
        _wmo = replacement.Wmo;
        _doodads = replacement.Doodads;
        _liquid = replacement.Liquid;
        _adts = replacement.Adts;
        _wdt = replacement.Wdt;
        _mapName = replacement.MapName;
        _ringCenter = replacement.RingCenter;
        _ring = replacement.OwnedRing;
        _collision = replacement.Collision;
        _collisionTask = null;
        _externalDrain = replacement.ExternalDrain;

        // The old active renderer set is now hidden and drained by Step. The
        // target remains scene-owned; invalidating it also prevents a stale
        // destination frame being exposed while that retirement proceeds.
        EnterRetirement();
        prepared = outgoing;
        return true;
    }

    /// <summary>
    /// Start one candidate. The slot must be idle; Retire/Step drains all old
    /// async producers before a new map can reuse the same tile/model keys.
    /// </summary>
    public void Begin(PortalDescriptor descriptor, string mapName, WdtFile? wdt = null)
    {
        EnsureOwnerThread();
        ThrowIfDisposed();
        if (_phase != Phase.Idle)
            throw new InvalidOperationException("Portal destination slot is not idle");
        if (!descriptor.IsValid)
            throw new ArgumentException("Portal descriptor is incomplete or non-finite", nameof(descriptor));
        if (string.IsNullOrWhiteSpace(mapName))
            throw new ArgumentException("A destination map directory is required", nameof(mapName));

        try
        {
            _generation++;
            Descriptor = descriptor;
            Failure = null;
            _mapName = mapName;
            _wdt = wdt ?? WdtFile.Read(_config.ClientDataPath, mapName)
                ?? throw new InvalidOperationException($"Could not read destination WDT '{mapName}'");
            _adts = new AdtCache(_config.ClientDataPath, mapName);
            _ringCenter = TerrainRenderer.TileAt(
                descriptor.PreviewPosition.X, descriptor.PreviewPosition.Y);
            _ring = _wdt.UsesGlobalWmo
                ? []
                : TerrainRenderer.TileRing(_ringCenter.col, _ringCenter.row, _tileRadius);
            if (!_wdt.UsesGlobalWmo && _ring.Count == 0)
                throw new InvalidOperationException("Destination lies outside the map's ADT grid");
            _collision = null;
            _collisionTask = null;
            _externalDrain = null;
            _geometryReady = false;
            _arrivalSupport = false;
            _previewTime = 0f;
            _target.Invalidate();

            // A bundle which previously served as the active world carries a
            // positive appear-fade clock and persistent placement stamps. The
            // candidate is rendered outside GameLoop's active-world clock, so
            // retaining those stamps makes second/subsequent portal candidates
            // stay at fade alpha zero forever. Initial candidate residency must
            // be opaque; only assets streamed after promotion use active fades.
            _wmo.BeginOpaqueWorldEpoch();
            _doodads?.BeginOpaqueWorldEpoch();

            // The active-camera WMO portal traversal is deliberately view-
            // dependent: it removes every room/group which cannot be reached
            // through a doorway inside the current screen rectangle. That is a
            // useful main-world optimisation, but it is not a safe visibility
            // authority for a camera rendered into a separately projected
            // aperture. At destination WMO cell boundaries (notably Darnassus
            // and Ironforge), small source-angle changes can select a different
            // seed/doorway rectangle and expose clear-colour wedges where whole
            // destination groups and their embedded doodads were rejected.
            // Fail open for the isolated preview. Promotion copies the active
            // renderer's setting back onto this WMO before it becomes main-world
            // state, so normal gameplay portal culling remains unchanged.
            _wmo.UsePortalCulling = false;
            _wmo.SuppressDistanceLodShells = true;
            // A recycled active renderer can still point at its old world's BVH
            // and carry low-quality occlusion mode. Neither is authoritative for
            // this new isolated candidate, and a stale ray hit would make whole
            // destination groups disappear as the virtual camera turns.
            _wmo.OcclusionWorld = null;
            _wmo.OcclusionCulling = false;
            _wmo.ResetForMapChange();
            _doodads?.ResetPlacements();
            _liquid.UnloadAll();

            if (_wdt.UsesGlobalWmo)
            {
                if (_wdt.GlobalWmo is null)
                    throw new InvalidOperationException("Global-WMO WDT has no MODF placement");
                _wmo.QueuePreloadGlobal(_wdt.GlobalWmo);
            }
            else
            {
                _terrain.QueuePreload(_ring, _adts, _ringCenter);
                _wmo.QueuePreloadForTiles(_ring, _adts,
                    new Vector2(descriptor.PreviewPosition.X, descriptor.PreviewPosition.Y));
            }

            _phase = Phase.WarmGeometry;
        }
        catch (Exception ex)
        {
            Fail(ex);
            throw;
        }
    }

    /// <summary>Advance at most one model-finalization unit; never waits.</summary>
    public void Step()
    {
        EnsureOwnerThread();
        ThrowIfDisposed();
        try
        {
            _target.PollComplete();
            AcceptCollisionIfComplete();

            switch (_phase)
            {
                case Phase.Idle:
                case Phase.Visual:
                case Phase.Failed:
                    return;

                case Phase.WarmGeometry:
                    StepWarmGeometry();
                    return;

                case Phase.QueueDoodads:
                    QueueDoodadsAndLiquids();
                    return;

                case Phase.WarmDoodads:
                    _doodads!.WarmNextPreload(waitForWorker: false);
                    if (_doodads.PendingPreloads == 0) _phase = Phase.PlaceDoodads;
                    return;

                case Phase.PlaceDoodads:
                    PlaceDoodadsAndStartCollision();
                    return;

                case Phase.Retiring:
                    StepRetirement();
                    return;
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void StepWarmGeometry()
    {
        _wmo.WarmNextPreload(waitForWorker: false);
        if (_wdt is null || _adts is null) throw new InvalidOperationException("Candidate map is missing");

        if (!_wdt.UsesGlobalWmo)
        {
            _terrain.PumpPreloads();
            if (!_terrain.PreloadReady(_ring)) return;
            _terrain.SetResidency(_ringCenter.col, _ringCenter.row, _tileRadius, _adts);
        }

        if (_wmo.PendingPreloads != 0) return;

        _wmo.ResetPlacements();
        if (_wdt.UsesGlobalWmo)
        {
            if (_wdt.GlobalWmo is null || !_wmo.LoadGlobal(_wdt.GlobalWmo, warmedOnly: true))
                throw new InvalidOperationException("Destination global WMO did not become resident");
        }
        else
        {
            _wmo.LoadForTiles(_ring, _adts, warmedOnly: true);
        }
        _phase = Phase.QueueDoodads;
    }

    private void QueueDoodadsAndLiquids()
    {
        if (_wdt is null || _adts is null) throw new InvalidOperationException("Candidate map is missing");

        if (!_wdt.UsesGlobalWmo) _liquid.LoadForTiles(_ring, _adts);
        _liquid.UpdateWmoLiquid(_wmo.LiquidVersion, _wmo.EnumerateLiquid());

        if (_doodads is null)
        {
            StartCollisionBuild();
            _geometryReady = true;
            _phase = Phase.Visual;
            return;
        }

        _doodads.ResetPlacements();
        if (!_wdt.UsesGlobalWmo)
        {
            _doodads.QueuePreloadForTiles(_ring, _adts,
                new Vector2(Descriptor!.Value.PreviewPosition.X, Descriptor.Value.PreviewPosition.Y),
                _config.Render.DoodadDistance);
        }
        _doodads.QueuePreloadModels(_wmo.EnumerateDoodads().Select(d => d.ModelPath));
        _phase = _doodads.PendingPreloads == 0 ? Phase.PlaceDoodads : Phase.WarmDoodads;
    }

    private void PlaceDoodadsAndStartCollision()
    {
        if (_doodads is null || _adts is null || _wdt is null)
            throw new InvalidOperationException("Candidate doodad placement is missing state");

        var preview = Descriptor!.Value.PreviewPosition;
        if (!_wdt.UsesGlobalWmo)
        {
            _doodads.LoadForTiles(_ring, _adts, new Vector2(preview.X, preview.Y),
                _config.Render.DoodadDistance, reportDiagnostics: false);
        }
        foreach (var doodad in _wmo.EnumerateDoodads())
        {
            _doodads.AddPlaced(doodad.ModelPath, doodad.Transform, doodad.Light,
                doodad.WmoInstanceId, doodad.OwnerGroups);
        }

        StartCollisionBuild();
        _geometryReady = true;
        _phase = Phase.Visual;
    }

    /// <summary>
    /// Render the destination view corresponding to a source-camera/portal
    /// basis. The supplied atmosphere is read-only and may contain values
    /// resolved at the destination by the caller; the active world's atmosphere
    /// object is never mutated.
    /// </summary>
    public bool RenderPreview(
        Camera sourceCamera,
        Vector3 sourceCenter,
        Vector3 sourceRight,
        Vector3 sourceUp,
        Vector3 sourceNormal,
        WorldAtmosphere atmosphere,
        float deltaSeconds)
        => RenderPreview(sourceCamera,
            new PortalFrame(sourceCenter, sourceRight, sourceUp, sourceNormal),
            atmosphere, deltaSeconds, coupleFarPlaneToFog: true);

    public bool RenderPreview(
        Camera sourceCamera,
        PortalFrame sourceFrame,
        WorldAtmosphere atmosphere,
        float deltaSeconds,
        bool coupleFarPlaneToFog = true)
    {
        EnsureOwnerThread();
        ThrowIfDisposed();
        if (!VisualGeometryReady || Descriptor is not { } descriptor) return false;
        if (!PortalPreviewCameraLaw.TryCreate(
                sourceFrame, descriptor.DestinationFrame,
                sourceCamera.Position, sourceCamera.Forward,
                out PortalPreviewCameraLaw.Mapping cameraMapping))
        {
            Failure = "Portal frame is degenerate";
            return false;
        }

        try
        {
            if (_target.HasPendingFrame) return false;

            if (!PortalExitClipLaw.TryCreate(
                    descriptor.DestinationFrame,
                    descriptor.PlaneEpsilon,
                    out WorldClipPlane exitClip))
            {
                Failure = "Destination portal clip plane is degenerate";
                return false;
            }

            ConfigureCamera(sourceCamera, cameraMapping.ViewSource,
                cameraMapping.Destination, atmosphere, coupleFarPlaneToFog);
            ApplyLighting(atmosphere);
            float frameDelta = MathF.Max(0f, deltaSeconds);
            _previewTime += frameDelta;
            _wmo.NowSeconds = _previewTime;
            if (_doodads is not null) _doodads.NowSeconds = _previewTime;
            _liquid.Time += frameDelta;
            float? terrainZ = _terrain.SampleHeight(_camera.Position.X, _camera.Position.Y);
            _wmo.UpdateCameraCell(_camera.Position, terrainZ);

            bool publish = false;
            _target.Begin(new Vector4(atmosphere.SkyColor, 1f));
            try
            {
                _sky.Render(_camera, atmosphere);
                // The virtual eye is behind the synthetic destination doorway.
                // Clip all destination-world geometry to the forward half-space
                // so enclosing walls/terrain behind that exit cannot leak into
                // the oval as angle-dependent triangles or exterior shells.
                // PortalRenderTarget also snapshots this capability, but keep
                // the local scope explicit so the underwater full-screen shader
                // (which has no clip output) never observes it enabled.
                try
                {
                    _gl.Enable(EnableCap.ClipDistance0);
                    _terrain.Render(_camera, exitClip);
                    // WMO publishes this frame's room visibility for embedded M2s.
                    _wmo.Render(_camera, exitClip);
                    _doodads?.Render(_camera, exitClip);
                    _liquid.UpdateWmoLiquid(_wmo.LiquidVersion, _wmo.EnumerateLiquid());
                    _liquid.Render(_camera, exitClip);
                }
                finally
                {
                    // Begin establishes this private target with clip distance
                    // disabled, and End separately restores the caller's state.
                    _gl.Disable(EnableCap.ClipDistance0);
                }
                if (_liquid.TryGetSurface(_camera.Position.X, _camera.Position.Y,
                        out float surfaceZ, out byte liquidType) && _camera.Position.Z < surfaceZ)
                    _liquid.RenderUnderwater(surfaceZ - _camera.Position.Z, liquidType);
                publish = true;
                return true;
            }
            finally
            {
                _target.End(publish);
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
            return false;
        }
    }

    private void ConfigureCamera(Camera source, in PortalFrame sourceFrame,
        in PortalFrame destination, WorldAtmosphere atmosphere, bool coupleFarPlaneToFog)
    {
        Vector3 sourceForward = source.Forward;
        Vector3 sourceUp = source.AuthoredUp ?? Vector3.UnitZ;
        sourceUp -= sourceForward * Vector3.Dot(sourceUp, sourceForward);
        sourceUp = SafeNormalize(sourceUp, sourceFrame.Up);

        Vector3 desiredEye = destination.TransformPoint(source.Position, sourceFrame);
        // Transform the point the source camera is actually orbiting as well as
        // its eye. For a normal gameplay camera this is the destination player's
        // eye-height point; unlike an arbitrary one-unit look target, it gives us
        // a real boom segment which can be kept out of destination walls.
        Vector3 lookTarget = destination.TransformPoint(source.EyeTarget, sourceFrame);
        Vector3 forward = SafeNormalize(destination.TransformDirection(sourceForward, sourceFrame),
            destination.Normal);
        Vector3 up = SafeNormalize(destination.TransformDirection(sourceUp, sourceFrame),
            destination.Up);

        Vector3 eye = ResolvePreviewCameraCollision(lookTarget, desiredEye);
        Vector3 resolvedForward = lookTarget - eye;
        if (resolvedForward.LengthSquared() > 1e-8f)
            forward = Vector3.Normalize(resolvedForward);

        _camera.AuthoredPosition = eye;
        _camera.AuthoredTarget = eye + forward;
        _camera.AuthoredUp = up;
        float effectiveFovRadians = source.AuthoredVerticalFieldOfViewRadians ??
                                    source.FieldOfViewDegrees * MathF.PI / 180f;
        _camera.AuthoredVerticalFieldOfViewRadians = effectiveFovRadians;
        // SkyRenderer consumes the legacy degree field directly while world
        // geometry consumes Camera.Projection. Keep those two ray fields exact.
        _camera.FieldOfViewDegrees = effectiveFovRadians * 180f / MathF.PI;
        _camera.AspectRatio = _target.DesiredWidth / (float)_target.DesiredHeight;
        _camera.NearPlane = MathF.Max(0.03f, source.NearPlane);
        _camera.FarPlane = MathF.Max(_camera.NearPlane + 1f,
            coupleFarPlaneToFog && atmosphere.CullAtFogEnd
                ? MathF.Min(_config.Render.FarPlane, atmosphere.FogEnd + 50f)
                : _config.Render.FarPlane);
    }

    /// <summary>
    /// Apply the same essential boom safety as the active camera to the virtual
    /// destination eye. A coefficient-perfect portal transform can put a camera
    /// which is 10-20 yards behind the source doorway behind an Ironforge wall
    /// at the stock arrival pose. The resulting preview is a permanent clear-
    /// colour oval with a few clipped WMO strips even though preparation is
    /// complete. Collapse only the obstructed portion of the destination boom;
    /// unobstructed positional parallax remains exact.
    /// </summary>
    private Vector3 ResolvePreviewCameraCollision(in Vector3 lookTarget, in Vector3 desiredEye)
    {
        if (!_config.Camera.Collision) return desiredEye;

        Vector3 boom = desiredEye - lookTarget;
        float requested = boom.Length();
        if (!float.IsFinite(requested) || requested <= 1e-4f) return desiredEye;

        Vector3 direction = boom / requested;
        float clearance = MathF.Max(0.05f, _config.Camera.Clearance);
        float allowed = requested;

        if (_collision is { IsEmpty: false })
        {
            var hit = _collision.Raycast(lookTarget, direction, requested + clearance);
            if (hit is not null)
                allowed = MathF.Min(allowed, hit.Value.Distance - clearance);
        }

        // Outdoor terrain is not part of the candidate BVH. March the boom as
        // the active camera does, but ignore an ADT shell above an interior eye.
        float? terrainAtTarget = _terrain.SampleHeight(lookTarget.X, lookTarget.Y);
        if (!Camera.TerrainIsOverhead(lookTarget.Z, terrainAtTarget, 1f))
        {
            const int steps = 12;
            float step = requested / steps;
            for (int i = 1; i <= steps; i++)
            {
                float distance = step * i;
                if (distance > allowed) break;
                Vector3 sample = lookTarget + direction * distance;
                float? ground = _terrain.SampleHeight(sample.X, sample.Y);
                if (ground is null || sample.Z >= ground.Value + clearance) continue;
                allowed = distance - step;
                break;
            }
        }

        // Just beyond the near plane is preferable to leaving the camera inside
        // a wall. This mirrors the active camera's first-person corner case.
        allowed = Math.Clamp(allowed, MathF.Min(0.25f, requested), requested);
        return lookTarget + direction * allowed;
    }

    /// <summary>Immediately hide the candidate; Step drains it without blocking.</summary>
    public void Retire()
    {
        EnsureOwnerThread();
        if (_disposed || _phase is Phase.Idle or Phase.Disposed) return;
        EnterRetirement();
    }

    private void EnterRetirement()
    {
        if (_phase == Phase.Retiring) return;
        _generation++;
        _geometryReady = false;
        _arrivalSupport = false;
        Descriptor = null;
        _target.Invalidate();
        // Cancel deferred ADT discovery immediately. Completed/queued WMO
        // model uploads remain in the renderer and are drained below, but a
        // faulted ADT must not be rediscovered and requeued forever while this
        // slot is trying to retire.
        _wmo.ResetForMapChange();
        _phase = Phase.Retiring;
    }

    private void StepRetirement()
    {
        _target.PollComplete();
        _terrain.PumpPreloads();
        _wmo.WarmNextPreload(waitForWorker: false);
        // The discarded source can have many ready M2 jobs at the crossing.
        // Adopt only one per frame so cleanup never recreates the hitch which
        // prepared-world promotion is intended to remove.
        _doodads?.WarmNextPreload(waitForWorker: false, maxReadyJobs: 1);
        AcceptCollisionIfComplete();

        // A candidate may be retired before SetResidency establishes _desired,
        // and an exchanged old active world may have speculative tiles beyond
        // its resident ring. Only the renderer-wide task predicate proves that
        // UnloadAll cannot synchronously wait on either case.
        bool terrainDone = _terrain.AllPreloadsCompleted;
        bool collisionDone = _collisionTask is null || _collisionTask.IsCompleted;
        bool externalDone = _externalDrain is null || _externalDrain.IsCompleted;
        if (!terrainDone || _wmo.PendingPreloads != 0 ||
            (_doodads?.PendingPreloads ?? 0) != 0 || !collisionDone ||
            !externalDone || !_target.CanRetire)
            return;

        // Observe faults without surfacing them through retirement. This work
        // belongs to the world being discarded and its result is intentionally
        // no longer publishable.
        if (_externalDrain is { } external)
        {
            try { external.GetAwaiter().GetResult(); }
            catch { }
        }

        _terrain.UnloadAll();
        _wmo.ResetForMapChange();
        _doodads?.ResetPlacements();
        _liquid.UnloadAll();
        _adts?.Clear();
        _adts = null;
        _wdt = null;
        _mapName = null;
        _ring.Clear();
        _collision = null;
        _collisionTask = null;
        _externalDrain = null;
        _phase = Phase.Idle;
    }

    private void StartCollisionBuild()
    {
        var batches = new List<CollisionBatch>();
        _wmo.SnapshotCollision(batches);
        _doodads?.SnapshotCollision(batches);
        int generation = _generation;
        Vector3 arrival = Descriptor!.Value.PreviewPosition;
        _collisionTask = _workers.RunCritical(() => BuildCollision(generation, batches, arrival));
    }

    private static CollisionBuild BuildCollision(
        int generation, IReadOnlyList<CollisionBatch> batches, Vector3 arrival)
    {
        var world = new CollisionWorld();
        foreach (CollisionBatch batch in batches)
        {
            int source = world.RegisterSource(batch.Path);
            Vector3[] triangles = batch.Triangles;
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                world.AddTriangle(
                    Vector3.Transform(triangles[i], batch.Transform),
                    Vector3.Transform(triangles[i + 1], batch.Transform),
                    Vector3.Transform(triangles[i + 2], batch.Transform), source);
            }
        }
        world.Build();
        RayHit? floor = world.Raycast(arrival + new Vector3(0f, 0f, 4f),
            -Vector3.UnitZ, 100f);
        bool support = PortalArrivalLaw.HasNearbySupport(arrival, floor?.Point.Z);
        return new CollisionBuild(generation, world, support);
    }

    private void AcceptCollisionIfComplete()
    {
        if (_collisionTask is not { IsCompleted: true } task) return;
        _collisionTask = null;
        CollisionBuild build;
        try { build = task.GetAwaiter().GetResult(); }
        catch when (_phase == Phase.Retiring) { return; }
        if (build.Generation != _generation) return;
        _collision = build.World;
        bool terrainSupport = Descriptor is { } descriptor &&
            PortalArrivalLaw.HasNearbySupport(
                descriptor.PreviewPosition,
                _terrain.SampleHeight(
                    descriptor.PreviewPosition.X, descriptor.PreviewPosition.Y));
        _arrivalSupport = build.ArrivalSupport || terrainSupport;
    }

    private void ApplyLighting(WorldAtmosphere atmosphere)
    {
        ApplyTerrain(_terrain);
        ApplyWmo(_wmo);
        if (_doodads is not null) ApplyDoodads(_doodads);

        _liquid.SunDirection = atmosphere.SunDirection;
        _liquid.SunColor = atmosphere.SunColor;
        _liquid.SunIntensity = atmosphere.SunIntensity;
        _liquid.AmbientColor = atmosphere.AmbientColor;
        _liquid.AmbientIntensity = atmosphere.AmbientIntensity;
        _liquid.FogColor = atmosphere.FogColor;
        _liquid.FogStart = atmosphere.ShaderFogStart;
        _liquid.FogEnd = atmosphere.ShaderFogEnd;
        _liquid.UseAuthoredColors = atmosphere.AuthoredWaterReady;
        _liquid.HasAuthoredColors = atmosphere.AuthoredWaterReady;
        _liquid.OceanClose = atmosphere.OceanCloseColor;
        _liquid.OceanFar = atmosphere.OceanFarColor;
        _liquid.RiverClose = atmosphere.RiverCloseColor;
        _liquid.RiverFar = atmosphere.RiverFarColor;
        _liquid.OceanAlphaShallow = atmosphere.OceanShallowAlpha;
        _liquid.OceanAlphaDeep = atmosphere.OceanDeepAlpha;
        _liquid.RiverAlphaShallow = atmosphere.RiverShallowAlpha;
        _liquid.RiverAlphaDeep = atmosphere.RiverDeepAlpha;

        void ApplyTerrain(TerrainRenderer renderer)
        {
            renderer.SunDirection = atmosphere.SunDirection;
            renderer.SunColor = atmosphere.SunColor;
            renderer.SunIntensity = atmosphere.SunIntensity;
            renderer.AmbientColor = atmosphere.AmbientColor;
            renderer.AmbientIntensity = atmosphere.AmbientIntensity;
            renderer.FogColor = atmosphere.FogColor;
            renderer.FogStart = atmosphere.ShaderFogStart;
            renderer.FogEnd = atmosphere.ShaderFogEnd;
            renderer.VisibilityDistance = atmosphere.VisibilityDistance;
        }

        void ApplyWmo(WmoRenderer renderer)
        {
            renderer.SunDirection = atmosphere.SunDirection;
            renderer.SunColor = atmosphere.SunColor;
            renderer.SunIntensity = atmosphere.SunIntensity;
            renderer.AmbientColor = atmosphere.AmbientColor;
            renderer.AmbientIntensity = atmosphere.AmbientIntensity;
            renderer.FogColor = atmosphere.FogColor;
            renderer.FogStart = atmosphere.ShaderFogStart;
            renderer.FogEnd = atmosphere.ShaderFogEnd;
            renderer.VisibilityDistance = atmosphere.VisibilityDistance;
        }

        void ApplyDoodads(DoodadRenderer renderer)
        {
            renderer.SunDirection = atmosphere.SunDirection;
            renderer.SunColor = atmosphere.SunColor;
            renderer.SunIntensity = atmosphere.SunIntensity;
            renderer.AmbientColor = atmosphere.AmbientColor;
            renderer.AmbientIntensity = atmosphere.AmbientIntensity;
            renderer.FogColor = atmosphere.FogColor;
            renderer.FogStart = atmosphere.ShaderFogStart;
            renderer.FogEnd = atmosphere.ShaderFogEnd;
            renderer.VisibilityDistance = atmosphere.VisibilityDistance;
        }
    }

    private void Fail(Exception exception)
    {
        Failure = exception.Message;
        EnterRetirement();
    }

    private static string ResolveShaderDirectory(ClientConfig config)
    {
        string besideExecutable = Path.Combine(AppContext.BaseDirectory, "Shaders");
        return Directory.Exists(besideExecutable)
            ? besideExecutable
            : Path.Combine(config.RepoRoot, "MSUIClient", "Shaders");
    }

    private static Vector3 SafeNormalize(in Vector3 value, in Vector3 fallback)
    {
        float lengthSquared = value.LengthSquared();
        return lengthSquared > 1e-12f ? value / MathF.Sqrt(lengthSquared) : fallback;
    }

    private static bool SameDestination(
        in PortalDescriptor left, in PortalDescriptor right) =>
        Vector3.DistanceSquared(left.SourceCenter, right.SourceCenter) <= 0.0001f &&
        MathF.Abs(left.SourceYaw - right.SourceYaw) <= 0.0001f &&
        MathF.Abs(left.HalfWidth - right.HalfWidth) <= 0.0001f &&
        MathF.Abs(left.HalfHeight - right.HalfHeight) <= 0.0001f &&
        MathF.Abs(left.PlaneEpsilon - right.PlaneEpsilon) <= 0.0001f &&
        left.PreviewMapId == right.PreviewMapId &&
        Vector3.DistanceSquared(left.PreviewPosition, right.PreviewPosition) <= 0.0001f &&
        MathF.Abs(left.PreviewOrientation - right.PreviewOrientation) <= 0.0001f &&
        left.PortalEntry == right.PortalEntry &&
        left.TeleportSpellId == right.TeleportSpellId;

    private static bool SamePreparedDescriptorExact(
        in PortalDescriptor left, in PortalDescriptor right) =>
        right.IsValid && left.Identity == right.Identity &&
        left.SourceCenter == right.SourceCenter &&
        left.SourceYaw == right.SourceYaw &&
        left.HalfWidth == right.HalfWidth &&
        left.HalfHeight == right.HalfHeight &&
        left.PlaneEpsilon == right.PlaneEpsilon &&
        left.PreviewMapId == right.PreviewMapId &&
        left.PreviewPosition == right.PreviewPosition &&
        left.PreviewOrientation == right.PreviewOrientation &&
        left.PortalEntry == right.PortalEntry &&
        left.TeleportSpellId == right.TeleportSpellId;

    private bool HasNearbyArrivalSupport(in Vector3 arrival)
    {
        if (PortalArrivalLaw.HasNearbySupport(
                arrival, _terrain.SampleHeight(arrival.X, arrival.Y)))
            return true;

        RayHit? floor = _collision?.Raycast(
            arrival + Vector3.UnitZ * 4f, -Vector3.UnitZ, 100f);
        return PortalArrivalLaw.HasNearbySupport(arrival, floor?.Point.Z);
    }

    private bool ReplacementIsDisjoint(PortalWorldBundle replacement) =>
        !ReferenceEquals(_terrain, replacement.Terrain) &&
        !ReferenceEquals(_wmo, replacement.Wmo) &&
        !ReferenceEquals(_liquid, replacement.Liquid) &&
        !ReferenceEquals(_adts, replacement.Adts) &&
        (_doodads is null) == (replacement.Doodads is null) &&
        (_doodads is null || !ReferenceEquals(_doodads, replacement.Doodads));

    public void Dispose()
    {
        EnsureOwnerThread();
        if (_disposed) return;

        // Shutdown is the only blocking path. Renderer disposals await their
        // workers before deleting shared-context GPU objects.
        if (_collisionTask is not null)
        {
            try { _collisionTask.GetAwaiter().GetResult(); }
            catch { }
            _collisionTask = null;
        }
        if (_externalDrain is not null)
        {
            try { _externalDrain.GetAwaiter().GetResult(); }
            catch { }
            _externalDrain = null;
        }
        _doodads?.Dispose();
        _liquid.Dispose();
        _wmo.Dispose();
        _terrain.Dispose();
        _target.Dispose();
        _adts?.Clear();
        _adts = null;
        Descriptor = null;
        _disposed = true;
        _phase = Phase.Disposed;
    }

    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("Portal destination scene must run on its owning GL thread");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
