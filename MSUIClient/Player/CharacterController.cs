using System.Numerics;
using MSUIClient.Net;
using MSUIClient.World;
using MSUIClient.World.Collision;

namespace MSUIClient.Player;

/// <summary>One owner-aware support hit from a moving world platform.</summary>
public readonly record struct MovingGroundHit(
    ulong OwnerGuid, float Distance, Vector3 Point, Vector3 Normal);

/// <summary>Per-frame movement intent. Filled from the window's input state.</summary>
public struct MovementInput
{
    /// <summary>-1 back .. +1 forward.</summary>
    public float Forward;

    /// <summary>-1 left .. +1 right.</summary>
    public float Strafe;

    /// <summary>-1 down .. +1 up. Only used while flying.</summary>
    public float Up;

    /// <summary>Absolute facing in radians CCW about +Z from +X - i.e. the camera yaw.</summary>
    public float Yaw;

    /// <summary>Swim travel pitch: positive aims upward, zero is level.</summary>
    public float Pitch;

    public bool Jump;
    public bool Walking;
    public bool Boost;
}

/// <summary>
/// Local character movement and collision, in WoW world space.
///
/// Vanilla WoW is CLIENT-AUTHORITATIVE for movement: the client decides where it
/// stands and tells the server. So this is the real simulation, not a prediction
/// of one, and it has to be right before any networking work starts.
///
/// Per frame:
///   1. input -> intended horizontal velocity
///   2. horizontal sweep, sliding along walls, stepping up small ledges
///   3. gravity and vertical integration
///   4. ground resolution - terrain height grid, plus a downward collision probe
///      for anything standing above terrain (bridges, WMO floors)
///
/// This is a port of the abandoned browser build's controller.ts. The one real
/// change is the coordinate space: that version worked in three.js space and
/// converted at the edges, this one is WoW space end to end. Vertical is Z, not
/// Y, and yaw is measured CCW about +Z from +X, which is exactly WoW's own
/// orientation value - so <see cref="WowState"/> needs no conversion at all.
///
/// GROUND IS THE HEIGHT GRID, NOT A RAYCAST.
/// <see cref="TerrainRenderer.SampleHeight"/> is an O(1) bilinear sample of the
/// grid the server agreed with to 0.00, and it uses the same arithmetic the mesh
/// does - so what you see and what you stand on cannot disagree. The collision
/// raycast only supplements it, for surfaces above terrain.
///
/// KNOWN LIMITATION: the horizontal sweep is a single probe ray from mid-body,
/// not a capsule sweep. It will let you clip the outside corner of a wall at
/// speed. Faithful to the browser build on purpose - swap in a capsule sweep
/// once vmap collision is confirmed working, not before, so a behaviour change
/// never gets confused with a data problem.
/// </summary>
public sealed class CharacterController
{
    private static readonly Vector3 Down = -Vector3.UnitZ;

    // A single ray is structurally unreliable on fence rails, stair lips and
    // other supports narrower than the character. Probe the capsule footprint:
    // centre first, then four cardinals and four diagonals. The directions are
    // unit length so every outer sample stays inside the configured radius.
    private static readonly Vector2[] SupportProbeDirections =
    [
        Vector2.Zero,
        Vector2.UnitX,
        -Vector2.UnitX,
        Vector2.UnitY,
        -Vector2.UnitY,
        new(0.70710677f, 0.70710677f),
        new(0.70710677f, -0.70710677f),
        new(-0.70710677f, 0.70710677f),
        new(-0.70710677f, -0.70710677f),
    ];

    private TerrainRenderer _terrain;
    private readonly ClientConfig.MovementConfig _opts;

    /// <summary>cos(maxSlope): a surface normal's Z must exceed this to be standable.</summary>
    private readonly float _minGroundZ;

    private bool _warnedNoGround;

    /// <summary>vmap collision. Null until vmaps are configured - terrain still works.</summary>
    public CollisionWorld? Collision { get; set; }

    /// <summary>
    /// Live moving-platform support, kept outside the immutable world BVH so a
    /// boat never leaves collision behind at an old timetable pose.
    /// </summary>
    public Func<Vector3, float, MovingGroundHit?>? MovingGroundProbe { get; set; }

    /// <summary>
    /// Owner-local collision that cannot live in the immutable world BVH.
    /// Doors/buttons use this lane so an ACTIVE door is immediately passable
    /// and a READY door is immediately solid without rebuilding the map.
    /// </summary>
    public Func<Vector3, Vector3, float, RayHit?>? DynamicCollisionProbe { get; set; }

    private bool HasGeometryCollision =>
        Collision is { IsEmpty: false } || DynamicCollisionProbe is not null;

    private RayHit? RaycastGeometry(Vector3 origin, Vector3 direction, float maxDistance)
    {
        RayHit? best = Collision is { IsEmpty: false }
            ? Collision.Raycast(origin, direction, maxDistance)
            : null;
        RayHit? dynamic = DynamicCollisionProbe?.Invoke(
            origin, direction, best?.Distance ?? maxDistance);
        return dynamic is { } live && (best is null || live.Distance < best.Value.Distance)
            ? live : best;
    }

    /// <summary>Current boat-local wire pose while attached to a transport.</summary>
    public TransportPose? Transport { get; set; }

    public Vector3 Position;
    public Vector3 Velocity;

    /// <summary>
    /// This frame's COMMANDED horizontal velocity, in yards per second. Zero
    /// when no direction key is held.
    ///
    /// Horizontal motion is applied straight to <see cref="Position"/>, so
    /// <see cref="Velocity"/> only ever carries Z and there was nothing for the
    /// animation layer to read - which is why it resorted to differencing the
    /// position and smoothing the result, and inherited a frame of lag, a wall
    /// slide that read as a change of direction, and a jittery leg-cycle rate.
    ///
    /// This is the intent, before collision: what the character is TRYING to do,
    /// which is what selects a gait. What the world did about it is a separate
    /// question and belongs to the position.
    /// </summary>
    public Vector3 HorizontalVelocity { get; private set; }

    /// <summary>Magnitude of <see cref="HorizontalVelocity"/>, for convenience.</summary>
    public float PlanarSpeed => HorizontalVelocity.Length();

    /// <summary>Radians CCW about +Z from +X. This IS the WoW orientation value.</summary>
    public float Yaw;

    /// <summary>
    /// Scales every ground speed the options carry — run, walk and backpedal alike, so their
    /// relationship is preserved. 1 is the configured speed and is the only value the server
    /// agrees with: this is client PREDICTION, so anything else will be argued with by
    /// position corrections on a live realm. Set by the mount toolkit while riding.
    /// </summary>
    public float SpeedMultiplier { get; set; } = 1f;

    private float? _serverWalkSpeed;
    private float? _serverRunSpeed;
    private float? _serverRunBackSpeed;
    private float? _serverSwimSpeed;
    private float? _serverSwimBackSpeed;

    public float EffectiveWalkSpeed => _serverWalkSpeed ?? _opts.WalkSpeed;
    public float EffectiveRunSpeed => _serverRunSpeed ?? _opts.RunSpeed;
    public float EffectiveRunBackSpeed => _serverRunBackSpeed ?? _opts.BackwardSpeed;
    public float EffectiveSwimSpeed => _serverSwimSpeed ?? SwimmingMovementLaw.DefaultForwardSpeed;
    public float EffectiveSwimBackSpeed => _serverSwimBackSpeed ?? SwimmingMovementLaw.DefaultBackwardSpeed;

    public void ApplyServerSpeed(MovementSpeedKind kind, float speed)
    {
        if (!float.IsFinite(speed) || speed < 0f) return;
        switch (kind)
        {
            case MovementSpeedKind.Walk: _serverWalkSpeed = speed; break;
            case MovementSpeedKind.Run: _serverRunSpeed = speed; break;
            case MovementSpeedKind.RunBack: _serverRunBackSpeed = speed; break;
            case MovementSpeedKind.Swim: _serverSwimSpeed = speed; break;
            case MovementSpeedKind.SwimBack: _serverSwimBackSpeed = speed; break;
        }
    }

    public void ResetServerSpeeds()
    {
        (_serverWalkSpeed, _serverRunSpeed, _serverRunBackSpeed) = (null, null, null);
        (_serverSwimSpeed, _serverSwimBackSpeed) = (null, null);
    }

    /// <summary>Scales launch velocity, same prediction caveat as <see cref="SpeedMultiplier"/>.</summary>
    public float JumpMultiplier { get; set; } = 1f;

    public bool WaterWalking { get; set; }
    public bool FeatherFalling { get; set; }
    public bool Hovering { get; set; }
    public bool Swimming { get; private set; }
    public float SwimPitch { get; private set; }
    public float? LiquidSurfaceZ { get; set; }
    private bool _swimJumpWasDown;
    private bool _swimBreachActive;
    public float? ExternalWalkableSurfaceZ { get; set; }
    public MovementFlags GrantedMovementFlags =>
        (WaterWalking ? MovementFlags.WaterWalking : MovementFlags.None) |
        (FeatherFalling ? MovementFlags.FeatherFalling : MovementFlags.None) |
        (Hovering ? MovementFlags.Hover : MovementFlags.None);

    /// <summary>
    /// How close to the ground counts as standing on it. Small, because it only
    /// has to absorb one frame of gravity plus float error.
    ///
    /// It is deliberately NOT scaled by dt or by jump velocity. It used to be
    /// compared against a rising character, which made the epsilon a frame-rate
    /// dependent jump killer - see the guard in ResolveGround. With that guard
    /// in place this value only ever meets a descending or resting character,
    /// where a fixed distance is the right thing.
    /// </summary>
    private const float GroundContactEpsilon = 0.05f;
    private const float TerrainSkin = 0.02f;
    private const float StepUpAdvance = 1.1917536f;
    private const float StepSlopeRatio = 1.849399f;
    private const float StepSnapSlack = 0.0277778f;

    /// <summary>
    /// Z of the surface that supported the character at the END of the previous
    /// frame, or null if it was not grounded. Read only by
    /// <see cref="ResolveGround"/>, to guarantee that a floor you were standing
    /// on cannot become invisible to this frame's probe - see the lift
    /// calculation there.
    /// </summary>
    private float? _lastSupportZ;

    public bool Grounded { get; private set; }

    private bool _flying;

    /// <summary>
    /// A flight exit or teleport is an explicit discontinuity: the supplied Z
    /// is more trustworthy than the outdoor height field on the first landing.
    /// Keep that fact until the controller finds support so an interior point
    /// below a mountain is not immediately lifted onto the mountain surface.
    /// </summary>
    private bool _landingAfterDiscontinuousMove;

    /// <summary>
    /// True while the character is known to be beneath the outdoor ADT height
    /// field and standing/falling in WMO collision instead.
    ///
    /// This is continuity, not a permanent indoor flag.  A WMO floor can vanish
    /// under one footprint probe while jumping across a narrow prop, stair lip,
    /// bridge edge, or collision seam.  Without remembering the already-proven
    /// relationship for that short gap, the overhead terrain shell becomes the
    /// only height candidate and the ordinary landing test teleports the player
    /// upward onto the mountain.  The state clears naturally as soon as the
    /// character reaches the outdoor side of the terrain surface.
    /// </summary>
    private bool _underTerrainShell;

    /// <summary>
    /// True while the controller is known to be beneath the outdoor height field and supported by
    /// WMO collision instead — inside Ironforge, Undercity, Blackrock, a cave, the Deeprun Tram.
    ///
    /// Exposed so callers stop deriving "altitude" from the terrain sample while it is overhead:
    /// Position.Z minus a shell 90 yards above you is not an altitude, and anything that scales
    /// off it (fly speed, wheel step) collapses to its floor the moment you step indoors.
    /// </summary>
    public bool UnderTerrainShell => _underTerrainShell;

    public bool Flying
    {
        get => _flying;
        set
        {
            if (_flying && !value) _landingAfterDiscontinuousMove = true;
            _flying = value;
        }
    }

    /// <summary>
    /// Minimum height the FLY rig keeps above sampled terrain, or null for the
    /// classic unclamped free-fly. The free view sets this: an RTS camera that
    /// can sink beneath the map is jank, while the plain F fly toggle stays a
    /// go-anywhere debug tool. Terrain only — WMO floors (city streets, bridges)
    /// are not sampled, so interiors stay reachable from above.
    /// </summary>
    public float? FlyFloorClearance { get; set; }

    /// <summary>
    /// The FLY rig sweeps against the collision world instead of ghosting through
    /// it. The free view sets this (owner decision 2026-08-11): the camera is a
    /// floating body that stops at walls and ceilings, so a room naturally
    /// contains its own view and you fly through the DOOR to see the next one.
    /// Plain F fly stays a ghost.
    /// </summary>
    public bool FlyCollide { get; set; }

    public float FallTimeMs { get; private set; }

    /// <summary>True when downward ground adhesion, rather than penetration, kept support this frame.</summary>
    public bool GroundAdhesion { get; private set; }

    /// <summary>Offset from the capsule centre of the collision probe that supplied support.</summary>
    public Vector2 GroundProbeOffset { get; private set; }

    /// <summary>Collision support rays used this frame: normally one, nine only near a lost edge.</summary>
    public int GroundProbesLastFrame { get; private set; }

    /// <summary>Last resolved ground height, for the HUD. Null means nothing below.</summary>
    public float? GroundZ { get; private set; }

    /// <summary>
    /// The two candidates ResolveGround chooses between, kept separately.
    ///
    /// Merging them into one number hid which of the two is actually holding
    /// the character up — and "the collision mesh is in the wrong place" and
    /// "the terrain is what is lifting me" look identical from the outside
    /// while needing completely different fixes.
    /// </summary>
    public float? TerrainGroundZ { get; private set; }
    public Vector3 TerrainGroundNormal { get; private set; } = Vector3.UnitZ;
    public bool TerrainGroundSteep { get; private set; }
    public bool TerrainChunkImpassable { get; private set; }
    public float? CollisionGroundZ { get; private set; }

    /// <summary>Which candidate won: "terrain", "collision", or "none".</summary>
    public string GroundSource { get; private set; } = "none";

    /// <summary>Index of the collision triangle currently acting as ground, or -1.</summary>
    public int GroundTriangle { get; private set; } = -1;

    /// <summary>Server GUID of the moving platform supporting the feet, or zero.</summary>
    public ulong GroundOwnerGuid { get; private set; }

    /// <summary>True when neither terrain nor collision could say what is below.</summary>
    public bool NoGroundBelow { get; private set; }

    /// <summary>True when the feet are over a quad the MCNK holes field cut away.</summary>
    public bool InTerrainHole { get; private set; }

    /// <summary>
    /// The active map deliberately has no terrain height field because its
    /// entire world is one WMO. Missing terrain on these maps means "keep
    /// falling and look for a WMO floor", not corrupt/missing streaming data.
    /// </summary>
    public bool TerrainAbsentByDesign { get; set; }

    /// <summary>
    /// Choose ground the way vanilla's Map::GetHeight does rather than by
    /// "highest surface wins".
    ///
    /// Highest-wins can never put you inside a dungeon. A mine floor is BELOW
    /// the mountain the height grid reports, so terrain beats it every frame
    /// no matter how good the WMO collision is. Vanilla instead takes the vmap
    /// surface when it is higher than terrain OR simply CLOSER to where the
    /// character already is — and that second half is the entire reason
    /// tunnels work.
    /// </summary>
    public bool VanillaHeightPrecedence { get; set; } = true;

    /// <summary>
    /// How far below the terrain surface the feet must be before the closer-
    /// surface rule is allowed to pick a lower floor.
    ///
    /// Vanilla's literal GROUND_HEIGHT_TOLERANCE is 0.05, but that constant
    /// guards a server-side query, not a movement loop. Walking uphill at
    /// 7 yd/s legitimately leaves the feet ~0.16 yd under the new terrain
    /// height for a frame, so 0.05 here would hand the frame to whatever
    /// collision triangle happened to be nearby and drop the character through
    /// the world. One yard is far more than any single frame of walking can
    /// produce and far less than any tunnel's headroom.
    /// </summary>
    public float UndergroundSlack { get; set; } = 1f;

    /// <summary>
    /// The surface that last stopped horizontal movement: where it was hit, its
    /// normal, and how long ago.
    ///
    /// A probe ray answers "what is in front of me", which is a different
    /// question from "what just stopped me" — and when a wall is somewhere it
    /// should not be, only the second question locates it. Stand where you get
    /// stuck, read the world coordinate off the HUD, then fly to where the wall
    /// looks like it should be and read that. The difference is the bug, in
    /// yards, with no interpretation in between.
    /// </summary>
    public Vector3 LastBlockPoint { get; private set; }
    public Vector3 LastBlockNormal { get; private set; }
    public float LastBlockAgeSeconds { get; private set; } = float.MaxValue;
    public int LastBlockTriangle { get; private set; } = -1;
    public bool HasBlock => LastBlockAgeSeconds < 3f;

    public CharacterController(TerrainRenderer terrain, ClientConfig.MovementConfig options)
    {
        _terrain = terrain;
        _opts = options;
        _minGroundZ = MathF.Cos(options.MaxSlopeDegrees * MathF.PI / 180f);
        Flying = options.StartFlying;
    }

    /// <summary>
    /// Rebind height queries after an already-prepared world renderer is
    /// promoted into the active slot. Movement state is deliberately retained;
    /// the authoritative teleport which follows owns the pose reset.
    /// </summary>
    public void RebindTerrain(TerrainRenderer terrain)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        _terrain = terrain;
    }

    public void Teleport(float x, float y, float z)
    {
        Position = new Vector3(x, y, z);
        Velocity = Vector3.Zero;
        HorizontalVelocity = Vector3.Zero;
        Swimming = false;
        SwimPitch = 0f;
        LiquidSurfaceZ = null;
        _swimJumpWasDown = false;
        _swimBreachActive = false;
        Grounded = false;
        FallTimeMs = 0;
        _warnedNoGround = false;
        _lastSupportZ = null;
        _landingAfterDiscontinuousMove = true;
        _underTerrainShell = false;
        Transport = null;
    }

    /// <summary>How far the last depenetration pass had to push, in yards.</summary>
    public float LastPushOut { get; private set; }

    /// <summary>
    /// Push out of any wall the character is already inside.
    ///
    /// WITHOUT THIS THE CONTROLLER IS A ONE-WAY DOOR. The sweep only ever stops
    /// motion; nothing moves the character back out. So the moment anything
    /// puts it inside geometry — a step-up onto a stair flush against a wall, a
    /// ground snap under an overhang, a ray slipping past a corner — it is
    /// stuck permanently. Every subsequent probe hits at ~0 distance, advance
    /// clamps to zero, and the character sits welded in place a couple of yards
    /// short of where the wall appears to be. That reads exactly like "collision
    /// is offset", which is why it was so misleading.
    ///
    /// Eight rays around the body at chest height. The raycast returns normals
    /// facing the ray, so a hit from inside points back out of the surface and
    /// pushing along it is the escape direction. Walkable slopes are skipped —
    /// those are floors, and shoving off them would make ramps unclimbable.
    /// </summary>
    private void Depenetrate()
    {
        LastPushOut = 0f;

        if (!HasGeometryCollision) return;

        var origin = Position + new Vector3(0, 0, _opts.Height * 0.5f);
        var push = Vector3.Zero;

        const int rays = 8;
        for (int i = 0; i < rays; i++)
        {
            float angle = i * MathF.PI * 2f / rays;
            var dir = new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0);

            var hit = RaycastGeometry(origin, dir, _opts.Radius);
            if (hit is null) continue;
            if (hit.Value.Normal.Z > _minGroundZ) continue;

            float depth = _opts.Radius - hit.Value.Distance;
            if (depth <= 0) continue;

            var outward = new Vector3(hit.Value.Normal.X, hit.Value.Normal.Y, 0);
            if (outward.LengthSquared() < 1e-6f) continue;

            push += Vector3.Normalize(outward) * depth;
        }

        if (push.LengthSquared() < 1e-8f) return;

        // Cap it. A corner hits several rays at once and would otherwise fire
        // the character across the room.
        float magnitude = push.Length();
        float capped = MathF.Min(magnitude, _opts.Radius);
        push = push / magnitude * capped;

        Position += push;
        LastPushOut = capped;
    }

    public void Update(float dt, in MovementInput input)
    {
        // Clamp so an alt-tab or a breakpoint doesn't teleport the character
        // through a wall. Same clamp the window applies, kept here so the
        // controller is correct even if it is ever driven from elsewhere.
        dt = MathF.Min(dt, 0.05f);

        Yaw = Normalize(input.Yaw);

        if (_swimBreachActive && Velocity.Z <= SwimmingMovementLaw.JumpSpeed * 0.5f)
            _swimBreachActive = false;
        bool nextSwimming = SwimmingMovementLaw.NextState(Swimming, LiquidSurfaceZ,
            Position.Z, _opts.Height, _swimBreachActive);
        if (nextSwimming && !Swimming)
        {
            Velocity = Vector3.Zero;
            Grounded = false;
            FallTimeMs = 0f;
        }
        Swimming = nextSwimming;

        // Facing and its right-hand side, in WoW space. Matches Camera.FlatForward
        // and Camera.FlatRight exactly - +Y is west, so right is (sin, -cos).
        var forward = new Vector3(MathF.Cos(Yaw), MathF.Sin(Yaw), 0f);
        var right = new Vector3(MathF.Sin(Yaw), -MathF.Cos(Yaw), 0f);

        if (Flying)
        {
            UpdateFlying(dt, input, forward, right);
            return;
        }

        if (Swimming)
        {
            bool breach = input.Jump && !_swimJumpWasDown;
            _swimJumpWasDown = input.Jump;
            if (!breach)
            {
                UpdateSwimming(dt, input);
                return;
            }
            Swimming = false;
            _swimBreachActive = true;
            Velocity.Z = SwimmingMovementLaw.JumpSpeed;
        }
        else
        {
            _swimJumpWasDown = input.Jump;
        }

        // Vanilla has a distinct MOVE_RUN_BACK speed. The old controller used
        // 7 yd/s in every direction, making backpedalling as fast as running
        // forward even though the server and original client use 4.5 yd/s.
        float speed = (input.Walking
            ? EffectiveWalkSpeed
            : input.Forward < -0.01f ? EffectiveRunBackSpeed : EffectiveRunSpeed)
            * MathF.Max(0.05f, SpeedMultiplier);

        var wish = forward * input.Forward + right * input.Strafe;
        var move = Vector3.Zero;
        HorizontalVelocity = Vector3.Zero;
        if (wish.LengthSquared() > 1e-6f)
        {
            HorizontalVelocity = Vector3.Normalize(wish) * speed;
            move = HorizontalVelocity * dt;
        }

        bool wasGrounded = Grounded;
        bool jumped = input.Jump && Grounded;

        if (jumped)
        {
            Velocity.Z = _opts.JumpVelocity * MathF.Max(0.05f, JumpMultiplier);
            Grounded = false;
        }

        Depenetrate();

        var terrainMoveStart = new Vector2(Position.X, Position.Y);
        MoveHorizontal(ref move);

        Velocity.Z -= _opts.Gravity * dt;
        float terminalVelocity = FeatherFalling ? 7f : _opts.TerminalVelocity;
        if (Velocity.Z < -terminalVelocity) Velocity.Z = -terminalVelocity;
        float verticalStartZ = Position.Z;
        Position.Z += Velocity.Z * dt;

        ResolveGround(wasGrounded && !jumped,
            MathF.Max(0f, verticalStartZ - Position.Z), terrainMoveStart);

        LastBlockAgeSeconds += dt;

        FallTimeMs = Grounded ? 0f : FallTimeMs + dt * 1000f;
    }

    private void UpdateSwimming(float dt, in MovementInput input)
    {
        // This path never reaches ResolveGround, so the remembered floor would
        // survive the whole swim and reappear as a probe lift on the far side.
        _lastSupportZ = null;
        SwimPitch = Math.Clamp(input.Pitch, -1.45f, 1.45f);
        Vector3 desired = SwimmingMovementLaw.DesiredVelocity(Yaw, SwimPitch,
            input.Forward, input.Strafe, EffectiveSwimSpeed, EffectiveSwimBackSpeed);
        if (LiquidSurfaceZ is float surface && desired.Z > 0f)
        {
            float cap = SwimmingMovementLaw.RestLine(surface, _opts.Height);
            float rise = MathF.Max(0f, cap - Position.Z);
            desired = SwimmingMovementLaw.RedirectAtRestLine(desired,
                dt > 0f ? rise / dt : 0f);
        }

        HorizontalVelocity = new Vector3(desired.X, desired.Y, 0f);
        Vector3 horizontal = HorizontalVelocity * dt;
        Depenetrate();
        MoveHorizontal(ref horizontal);
        Position.Z += desired.Z * dt;
        if (LiquidSurfaceZ is float top)
            Position.Z = MathF.Min(Position.Z, SwimmingMovementLaw.RestLine(top, _opts.Height));
        Velocity = new Vector3(0f, 0f, desired.Z);
        Grounded = false;
        FallTimeMs = 0f;
        GroundZ = null;
        GroundSource = "liquid";
        NoGroundBelow = false;
        LastBlockAgeSeconds += dt;
    }

    /// <summary>
    /// Free-fly. Keeps the pre-collision camera behaviour available as a toggle,
    /// which is the fastest way to check whether a movement problem is the
    /// controller or the world.
    /// </summary>
    private void UpdateFlying(float dt, in MovementInput input, Vector3 forward, Vector3 right)
    {
        // See UpdateSwimming: flying below a remembered floor and then landing
        // must not let that floor lift the probe origin toward head height.
        _lastSupportZ = null;
        float speed = _opts.FlySpeed * (input.Boost ? _opts.FlyBoost : 1f);

        var wish = forward * input.Forward + right * input.Strafe + Vector3.UnitZ * input.Up;

        HorizontalVelocity = Vector3.Zero;
        if (wish.LengthSquared() > 1e-6f)
        {
            var velocity = Vector3.Normalize(wish) * speed;
            HorizontalVelocity = new Vector3(velocity.X, velocity.Y, 0f);
            FlyMove(velocity * dt);
        }

        Velocity = Vector3.Zero;
        Grounded = false;
        FallTimeMs = 0;
        GroundZ = _terrain.SampleHeight(Position.X, Position.Y);

        // THE FLOOR CLEARANCE MUST NOT LIFT THE RIG THROUGH A MOUNTAIN.
        //
        // Ironforge, Undercity, Blackrock, the Deeprun Tram and every cave sit BELOW the outdoor
        // ADT height field, so a bare "you are under the terrain, rise to it" clamp teleported the
        // free view from the city floor onto the mountain surface overhead the instant it was
        // raised. Reported in Ironforge, 2026-08-26.
        //
        // This is the same terrain-shell problem the walking path solves at UpdateGrounded, and
        // it uses the same two signals, in the same order: is the terrain meaningfully OVERHEAD,
        // and have we proven we are underneath it rather than sunk under the world? The proof is
        // a single upward ray - a ceiling between the rig and the height field means we are
        // inside something and the shell is not a floor. Retained afterwards like the walking
        // path retains it, so a gap in that ceiling (Ironforge's own gate tunnel, a chimney, a
        // collision seam) cannot re-arm the clamp for the one frame it takes to fire.
        bool terrainOverhead = GroundZ is float overhead &&
                               overhead - Position.Z > UndergroundSlack;
        if (terrainOverhead)
        {
            if (!_underTerrainShell && HasGeometryCollision && GroundZ is float roofSearch &&
                RaycastGeometry(Position, Vector3.UnitZ, roofSearch - Position.Z) is not null)
                _underTerrainShell = true;
        }
        else if (GroundZ is not null) _underTerrainShell = false;

        if (FlyFloorClearance is float clearance && GroundZ is float ground &&
            Position.Z < ground + clearance && !(terrainOverhead && _underTerrainShell))
            Position = new Vector3(Position.X, Position.Y, ground + clearance);
        NoGroundBelow = false;
    }

    /// <summary>
    /// Move the FLY rig by <paramref name="delta"/>, sweeping against the collision
    /// world when <see cref="FlyCollide"/> is set and sliding along whatever it
    /// hits — like <see cref="MoveHorizontal"/> but full-3D and without its
    /// walkable-slope pass-through or step-up (a drone has no feet). Public so the
    /// free-view edge pan moves through the same wall test as WASD flight.
    /// </summary>
    public void FlyMove(Vector3 delta)
    {
        if (!FlyCollide || !HasGeometryCollision)
        {
            Position += delta;
            return;
        }
        var move = delta;
        for (int iter = 0; iter < 3; iter++)
        {
            float dist = move.Length();
            if (dist < 1e-5f) return;
            var dir = move / dist;
            var hit = RaycastGeometry(Position, dir, dist + _opts.Radius);
            if (hit is null || hit.Value.Distance > dist + _opts.Radius)
            {
                Position += move;
                return;
            }
            float advance = MathF.Max(0f, hit.Value.Distance - _opts.Radius);
            if (advance > 1e-5f) Position += dir * advance;
            // Slide: strip the component pushing into the surface.
            float into = Vector3.Dot(move, hit.Value.Normal);
            move -= hit.Value.Normal * into;
            move *= 0.98f;
        }
    }

    /// <summary>
    /// Horizontal sweep. On a wall hit the remaining motion is projected onto the
    /// wall plane so the character slides instead of sticking, then a step-up is
    /// attempted - which is what makes stairs and the abbey steps walkable
    /// without jumping. Two iterations handles inside corners.
    ///
    /// SAMPLED THROUGH THE BODY, NOT ONCE AT THE CHEST.
    ///
    /// This used to cast a single ray from mid-body, "so low rubble doesn't
    /// count as a wall". The cost of that shortcut is a band of walls the
    /// character cannot feel at all: anything whose top sits between the step
    /// height it may climb and the chest height the ray occupied was invisible,
    /// so the sweep reported clear air and the character walked straight
    /// through a face the collision debug view draws in wall red. Walk off the
    /// far side of one and there is no floor, which presents as falling through
    /// a building whose mesh has no hole in it.
    ///
    /// The foot sample discovers low risers so the atomic step maneuver gets a
    /// chance to certify their top; if certification fails, the same face still
    /// blocks instead of becoming intangible. The remaining samples cover the
    /// step-ceiling, chest, and near-head bands.
    /// </summary>
    private void MoveHorizontal(ref Vector3 move)
    {
        if (move.LengthSquared() < 1e-8f) return;

        ConstrainTerrainHorizontal(ref move);
        if (move.LengthSquared() < 1e-8f) return;

        if (!HasGeometryCollision)
        {
            Position += move;
            return;
        }

        Span<float> sampleHeights =
        [
            GroundContactEpsilon,
            _opts.StepHeight + GroundContactEpsilon,
            _opts.Height * 0.5f,
            _opts.Height * 0.9f,
        ];

        for (int iter = 0; iter < 2; iter++)
        {
            float dist = move.Length();
            if (dist < 1e-5f) return;

            var dir = move / dist;
            float reach = dist + _opts.Radius;

            RayHit? nearestWall = null;
            foreach (float height in sampleHeights)
            {
                var origin = Position + new Vector3(0, 0, height);
                if (RaycastGeometry(origin, dir, reach) is not { } candidate) continue;

                // Only a genuinely STEEP face stops horizontal motion. A floor,
                // a ramp, or the underside of a deck is the ground resolver's
                // business, and the test is absolute because the raycast turns
                // normals to face the ray - so a ceiling arrives with the same
                // sign as a floor and neither should act as a wall.
                //
                // Skipping such a hit rather than returning on it is the second
                // half of this fix. The old code took one walkable reading as
                // proof the whole path was clear and applied the full move with
                // no clamp, which made any ramp - or any deck seen from below -
                // a licence to walk into solid geometry.
                if (MathF.Abs(candidate.Normal.Z) > _minGroundZ) continue;

                if (nearestWall is null || candidate.Distance < nearestWall.Value.Distance)
                    nearestWall = candidate;
            }

            if (nearestWall is not { } wall)
            {
                Position += move;
                return;
            }

            if (TryStepUp(move, wall.Distance)) return;

            // This is the surface that actually stops the character. Record it
            // before sliding, because after the slide the information is gone.
            LastBlockPoint = wall.Point;
            LastBlockNormal = wall.Normal;
            LastBlockTriangle = wall.Triangle;
            LastBlockAgeSeconds = 0f;

            float advance = MathF.Max(0f, wall.Distance - _opts.Radius);
            if (advance > 1e-5f) Position += dir * advance;

            // Vanilla's steep-face response writes only the two horizontal
            // axes. An orthogonal plane projection would add Normal.Z lift to
            // this nominally horizontal vector, which is the jump-climb ratchet.
            Vector3 horizontalNormal = new(wall.Normal.X, wall.Normal.Y, 0f);
            float horizontalLengthSq = horizontalNormal.LengthSquared();
            float into = Vector3.Dot(move, wall.Normal);
            if (into < 0f && horizontalLengthSq > 1e-8f)
                move -= horizontalNormal * (into / horizontalLengthSq);
            move.Z = 0f;
            move *= 0.98f;
        }
    }

    /// <summary>
    /// Apply the two terrain-only collision laws before the vmap sweep.
    ///
    /// ADT terrain deliberately stays out of CollisionWorld's million-triangle
    /// BVH, but that cannot mean it is only a post-move height snap. This local
    /// fan-triangle query supplies the same face normal a swept terrain mesh
    /// would: impassable MCNK walls reject entry, and an opposing face over the
    /// 50-degree standability limit removes only the uphill horizontal push.
    /// </summary>
    private void ConstrainTerrainHorizontal(ref Vector3 move)
    {
        Vector2 start = new(Position.X, Position.Y);
        Vector2 requested = start + new Vector2(move.X, move.Y);
        bool haveStart = _terrain.TrySampleMovementSurface(
            start.X, start.Y, out TerrainSurfaceSample startSurface, out _);
        bool haveTarget = _terrain.TrySampleMovementSurface(
            requested.X, requested.Y, out TerrainSurfaceSample targetSurface, out _);

        static bool SameChunk(in TerrainSurfaceSample a, in TerrainSurfaceSample b) =>
            a.TileCol == b.TileCol && a.TileRow == b.TileRow &&
            a.ChunkX == b.ChunkX && a.ChunkY == b.ChunkY;
        // Ironforge and other interiors sit beneath the outdoor ADT. Once WMO
        // support has proven that relationship, neither the mountain's steep
        // contour nor its chunk fence may be projected downward into the room.
        bool IsProvenOverheadShell(in TerrainSurfaceSample surface) =>
            _underTerrainShell && surface.Height - Position.Z > UndergroundSlack;
        bool IsWmoSupportedLip(in TerrainSurfaceSample surface, Vector2 point) =>
            CollisionWalkwayDisplacesSteepTerrain(surface, point.X, point.Y);
        bool FenceActive(in TerrainSurfaceSample surface) =>
            !IsProvenOverheadShell(surface) && surface.Impassable &&
            Position.Z + _opts.Height >= surface.ChunkMinimumHeight - TerrainSkin;

        // MCNK_IMPASSABLE is four outward-facing walls around each authored
        // chunk. Entry is blocked; starting inside and leaving remains legal.
        if (haveTarget && FenceActive(targetSurface) &&
            (!haveStart || !SameChunk(startSurface, targetSurface)))
        {
            float lo = 0f, hi = 1f;
            for (int i = 0; i < 16; i++)
            {
                float mid = (lo + hi) * 0.5f;
                Vector2 point = Vector2.Lerp(start, requested, mid);
                bool blocked = _terrain.TrySampleMovementSurface(
                    point.X, point.Y, out TerrainSurfaceSample probe, out _) &&
                    FenceActive(probe) &&
                    (!haveStart || !SameChunk(startSurface, probe));
                if (blocked) hi = mid;
                else lo = mid;
            }

            float distance = move.Length();
            float allowed = MathF.Max(0f, distance * hi - _opts.Radius);
            Vector3 direction = distance > 1e-6f ? move / distance : Vector3.Zero;
            move = direction * allowed;
            LastBlockPoint = new Vector3(
                start.X + direction.X * distance * hi,
                start.Y + direction.Y * distance * hi,
                Position.Z);
            LastBlockNormal = -direction;
            LastBlockTriangle = -1;
            LastBlockAgeSeconds = 0f;

            requested = start + new Vector2(move.X, move.Y);
            haveTarget = _terrain.TrySampleMovementSurface(
                requested.X, requested.Y, out targetSurface, out _);
        }

        TerrainSurfaceSample face = default;
        bool contact = false;
        if (haveTarget && !IsProvenOverheadShell(targetSurface) &&
            !IsWmoSupportedLip(targetSurface, requested) &&
            targetSurface.Normal.Z < _minGroundZ &&
            Position.Z <= targetSurface.Height + TerrainSkin)
        {
            face = targetSurface;
            contact = true;
        }
        else if (haveStart && !IsProvenOverheadShell(startSurface) &&
                 !IsWmoSupportedLip(startSurface, start) &&
                 startSurface.Normal.Z < _minGroundZ &&
                 Position.Z <= startSurface.Height + TerrainSkin)
        {
            face = startSurface;
            contact = true;
        }

        if (!contact) return;

        Vector3 horizontalNormal = new(face.Normal.X, face.Normal.Y, 0f);
        float lengthSq = horizontalNormal.LengthSquared();
        float into = Vector3.Dot(move, face.Normal);
        if (into >= 0f || lengthSq <= 1e-8f) return;

        move -= horizontalNormal * (into / lengthSq);
        move.Z = 0f;
        LastBlockPoint = new Vector3(requested.X, requested.Y, face.Height);
        LastBlockNormal = face.Normal;
        LastBlockTriangle = -1;
        LastBlockAgeSeconds = 0f;
    }

    /// <summary>
    /// A walkable collision floor continuing beneath a steep ADT face proves
    /// that the face is the lip/shell around an authored WMO entrance, not an
    /// outdoor hillside the character is trying to climb.  This proof is local
    /// to the requested footprint and deliberately requires a floor within the
    /// ordinary step/snap band; a bridge somewhere below a mountain cannot
    /// disable terrain collision from a distance.
    /// </summary>
    private bool CollisionWalkwayDisplacesSteepTerrain(
        in TerrainSurfaceSample terrain, float worldX, float worldY)
    {
        if (!HasGeometryCollision || terrain.Normal.Z >= _minGroundZ)
            return false;

        float lift = MathF.Max(_opts.StepHeight, GroundContactEpsilon);
        float depth = lift + _opts.GroundSnapDistance + GroundContactEpsilon;
        Vector3 origin = new(worldX, worldY, Position.Z + lift);
        if (RaycastGeometry(origin, Down, depth) is not { } hit ||
            hit.Normal.Z <= _minGroundZ)
            return false;

        float floorZ = origin.Z - hit.Distance;
        return floorZ >= Position.Z - _opts.GroundSnapDistance - TerrainSkin &&
               floorZ <= Position.Z + _opts.StepHeight + TerrainSkin &&
               terrain.Height > floorZ + TerrainSkin;
    }

    /// <summary>
    /// Certify and atomically commit a low ledge step. Current Benilla raises by
    /// the available one-yard ceiling, advances at least one body-scale probe,
    /// and settles onto a higher walkable floor; any failed phase leaves the
    /// original position untouched so walls and pinches remain ordinary slides.
    /// </summary>
    /// <summary>
    /// A FLIGHT of steps needs two looks, not one. The fixed body-scale advance (1.19 yd) that
    /// certifies a lone curb lands three treads up a steep staircase — the Stormwind Trade
    /// District shop steps, 0.45 yd risers on 0.35 yd treads — where the rise exceeds StepHeight
    /// and the climb is refused, so the player had to jump every flight. Reported 2026-09-01.
    /// The second look advances just past the blocking riser's edge onto its own tread.
    /// </summary>
    private bool TryStepUp(Vector3 move, float wallDistance = float.PositiveInfinity)
    {
        if (!Grounded || !HasGeometryCollision) return false;
        float travel = new Vector2(move.X, move.Y).Length();
        if (travel <= 1e-6f) return false;
        _stepWallDistance = wallDistance;
        float far = MathF.Max(travel, StepUpAdvance);
        if (TryStepUpBy(move, far)) return true;
        string farVerdict = _stepVerdict;
        // Just past the riser's edge: the tread begins at the face, so the target centre only
        // needs a small margin beyond it (the feet may overlap the edge; Position is then seated
        // on the tread).
        float near = MathF.Max(travel, wallDistance + MathF.Min(0.15f, _opts.Radius * 0.5f));
        bool nearOk = float.IsFinite(wallDistance) && near < far - 1e-3f && TryStepUpBy(move, near);
        if (!nearOk)
        {
            // Throttled diagnostic: what refused the climb, and the wall the sweep hit. This is
            // the number a "won't step up this curb" report is about.
            double now = Environment.TickCount64 / 1000.0;
            if (now - _stepLogAt > 0.5)
            {
                _stepLogAt = now;
                Console.WriteLine($"[step] refused at ({Position.X:F1},{Position.Y:F1},{Position.Z:F2}) wall={wallDistance:F2} " +
                    $"far[{farVerdict}] near[{_stepVerdict}] block n=({LastBlockNormal.X:F2},{LastBlockNormal.Y:F2},{LastBlockNormal.Z:F2}) p.z={LastBlockPoint.Z:F2}");
                // The surface profile ahead (benilla's `stup` scan): the collision floor under a
                // down-ray every 0.1 yd along the input direction, as height above the feet.
                Vector3 dir = new(move.X / travel, move.Y / travel, 0f);
                var profile = new System.Text.StringBuilder("[step] profile ahead:");
                for (int i = 0; i <= 16; i++)
                {
                    float d = i * 0.1f;
                    Vector3 from = Position + dir * d + Vector3.UnitZ * 2.5f;
                    string cell = RaycastGeometry(from, Down, 4f) is { } h
                        ? $"{(from.Z - h.Distance - Position.Z):+0.00;-0.00}/{h.Normal.Z:F2}" : "--";
                    profile.Append($" {d:F1}:{cell}");
                }
                Console.WriteLine(profile.ToString());
            }
        }
        return nearOk;
    }

    private double _stepLogAt;
    private string _stepVerdict = "";
    private float _stepWallDistance = float.PositiveInfinity;

    private bool TryStepUpBy(Vector3 move, float advance)
    {
        float travel = new Vector2(move.X, move.Y).Length();
        Vector3 direction = new(move.X / travel, move.Y / travel, 0f);
        _stepVerdict = "";

        // Sweep the raised body before asking what floor lies below it. A point
        // ray alone can certify a tread through a wall or beneath a low lintel.
        Span<float> bodyBands =
        [
            GroundContactEpsilon,
            _opts.Height * 0.5f,
            _opts.Height * 0.9f,
        ];
        // The raised sweep CLIPS the advance instead of refusing it (benilla mover::step_up:
        // `fwd = cast(raised, dir * advance).map_or(advance, |h| h.distance)`). On a flight of
        // steps the raised body meets the NEXT riser within the advance; refusing on that hit
        // is what made every staircase a jump, since the far look always sees another riser.
        // Advancing only as far as the free run and settling there lands on the tread in
        // between — the atomic climb, one tread per frame.
        float clipped = advance;
        foreach (float height in bodyBands)
        {
            Vector3 origin = Position +
                             new Vector3(0f, 0f, _opts.StepHeight + height);
            if (RaycastGeometry(origin, direction, advance + _opts.Radius) is not { } hit)
                continue;
            if (MathF.Abs(hit.Normal.Z) <= _minGroundZ)
                clipped = MathF.Min(clipped, MathF.Max(0f, hit.Distance - _opts.Radius));
        }
        if (clipped < 0.05f) { _stepVerdict = $"no-run advance={advance:F2} clipped={clipped:F2}"; return false; }
        advance = clipped;

        Vector3 target = Position + direction * advance;
        Vector3 probe = target + Vector3.UnitZ * (_opts.StepHeight + GroundContactEpsilon);
        float settleReach = _opts.StepHeight +
                            advance * StepSlopeRatio + StepSnapSlack +
                            GroundContactEpsilon;
        if (RaycastGeometry(probe, Down, settleReach) is not { } down)
        { _stepVerdict = $"no-floor advance={advance:F2} reach={settleReach:F2}"; return false; }
        if (down.Normal.Z <= _minGroundZ)
        { _stepVerdict = $"steep-floor advance={advance:F2} nz={down.Normal.Z:F2} dist={down.Distance:F2}"; return false; }

        float stepTop = probe.Z - down.Distance;
        float rise = stepTop - Position.Z;
        if (rise <= GroundContactEpsilon || rise > _opts.StepHeight)
        { _stepVerdict = $"rise advance={advance:F2} rise={rise:F2}"; return false; }

        // The top of the capsule must be able to make the same rise. Probe the
        // centre and a footprint cross so an overhang cannot accept the feet and
        // leave the body embedded.
        float inset = _opts.Radius * 0.7f;
        Span<Vector2> footprint =
        [
            Vector2.Zero,
            new Vector2(inset, 0f),
            new Vector2(-inset, 0f),
            new Vector2(0f, inset),
            new Vector2(0f, -inset),
        ];
        Span<Vector2> headroomBases =
        [
            new Vector2(Position.X, Position.Y),
            new Vector2(target.X, target.Y),
        ];
        foreach (Vector2 headroomBase in headroomBases)
        foreach (Vector2 offset in footprint)
        {
            Vector3 head = new(
                headroomBase.X + offset.X,
                headroomBase.Y + offset.Y,
                Position.Z + _opts.Height - GroundContactEpsilon);
            if (RaycastGeometry(head, Vector3.UnitZ, rise + GroundContactEpsilon) is { } ceiling &&
                ceiling.Distance < rise)
            { _stepVerdict = $"headroom advance={advance:F2} rise={rise:F2} ceiling={ceiling.Distance:F2}"; return false; }
        }

        // The climb is atomic for physics (one frame, no mid-riser state to be caught in) but
        // the EYE eases: the render/camera height starts where the feet were and catches up over
        // a few frames, so a flight of steps reads as walking up rather than a series of hops.
        // COMMIT ONLY THIS FRAME'S TRAVEL. The probe advance certified the tread; it is not the
        // move. Landing the body a full probe length forward was the "teleport up the stairs":
        // a walk into a riser should lift you over the lip where you are and let the next frames
        // walk on. The front of the footprint sits over the tread edge and the ground resolver's
        // footprint support holds it there.
        float commit = MathF.Min(advance, MathF.Max(travel,
            (float.IsFinite(_stepWallDistance) ? _stepWallDistance : advance) - _opts.Radius * 0.7f + 0.02f));
        Vector3 landed = Position + direction * MathF.Max(0f, commit);
        Position = new Vector3(landed.X, landed.Y, stepTop);
        Velocity.Z = 0f;
        Grounded = true;
        return true;
    }
    /// <summary>
    /// Keep an airborne body outside a steep heightfield without changing Z.
    /// The push is down the face's horizontal normal by exactly the distance
    /// needed to preserve this frame's vertical descent. That is the Z-up form
    /// of vanilla's horizontal-only steep response.
    /// </summary>
    private void ResolveSteepTerrainContact(Vector2 horizontalStart)
    {
        Vector2 end = new(Position.X, Position.Y);
        Vector2 travel = end - horizontalStart;

        bool IsSteepPenetration(Vector2 point)
        {
            return _terrain.TrySampleMovementSurface(
                       point.X, point.Y, out TerrainSurfaceSample sample, out _) &&
                   sample.Normal.Z < _minGroundZ &&
                   Position.Z < sample.Height;
        }

        // If this frame crossed from clear space into the hillside, retain the
        // last clear horizontal point. Z and Velocity.Z are deliberately never
        // touched, so a jump cannot bank height at the contact.
        if (travel.LengthSquared() > 1e-10f && !IsSteepPenetration(horizontalStart))
        {
            float lo = 0f, hi = 1f;
            for (int i = 0; i < 16; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (IsSteepPenetration(Vector2.Lerp(horizontalStart, end, mid)))
                    hi = mid;
                else
                    lo = mid;
            }

            Vector2 clear = Vector2.Lerp(horizontalStart, end, lo);
            Position = new Vector3(clear.X, clear.Y, Position.Z);
            return;
        }

        // Already touching the face: gravity's downward travel becomes a
        // downslope horizontal displacement. For z=f(x,y), the upward face
        // normal's XY projection points downhill and |grad z|=|n.xy|/n.z.
        for (int i = 0; i < 4; i++)
        {
            if (!_terrain.TrySampleMovementSurface(
                    Position.X, Position.Y, out TerrainSurfaceSample surface, out _) ||
                surface.Normal.Z >= _minGroundZ)
                return;

            float penetration = surface.Height - Position.Z;
            if (penetration <= 0f) return;

            Vector2 downhill = new(surface.Normal.X, surface.Normal.Y);
            float horizontalNormal = downhill.Length();
            if (horizontalNormal <= 1e-6f) return;

            downhill /= horizontalNormal;
            float distance = penetration * MathF.Max(0f, surface.Normal.Z) /
                             horizontalNormal + 0.001f;
            Position = new Vector3(
                Position.X + downhill.X * distance,
                Position.Y + downhill.Y * distance,
                Position.Z);
        }
    }

    /// <summary>
    /// Ground resolution. The height grid is authoritative for terrain where
    /// terrain exists; a downward raycast covers everything built on top of it.
    ///
    /// TWO THINGS DECIDE WHICH ONE HOLDS YOU UP, AND BOTH MATTER FOR DUNGEONS.
    /// First, the height grid now returns nothing inside an MCNK hole — the
    /// deliberate opening the artists cut so a mine or crypt entrance is
    /// reachable — instead of reporting terrain that was never drawn. Second,
    /// selection follows vanilla's Map::GetHeight: the collision surface wins
    /// when it is higher than terrain, or when the character is already
    /// underneath terrain and the collision surface is closer. Plain
    /// highest-wins can only ever put you on the mountain, never in the mine
    /// under it.
    ///
    /// THE PROBE NORMALLY STARTS AT StepHeight, NOT AT Height, AND THAT MATTERS.
    /// Casting from head height finds surfaces up to two yards ABOVE the feet,
    /// and the snap below then yanks the character onto them — so walking near
    /// a staircase teleports you up it before you reach it, and standing under
    /// any low overhang glues you to its underside. Starting the ray at
    /// StepHeight means the highest surface it can normally report is exactly
    /// one step up, which is the whole rule: you may climb a step, you may not
    /// be levitated onto a landing. During a fall the origin grows only by this
    /// frame's downward travel, making the probe a swept landing test without
    /// admitting a higher step.
    ///
    /// The browser build had this same structure and the same latent bug. It
    /// was ported faithfully, which was the right default and the wrong outcome
    /// here.
    /// </summary>
    private void ResolveGround(
        bool allowGroundAdhesion, float downwardTravel, Vector2 terrainMoveStart)
    {
        // Consumed by the probe lift below, then re-established only if this
        // frame actually ends grounded. Clearing it up front means every early
        // return - no ground, terrain hole, terrain shell - forgets the old
        // support rather than carrying a stale floor into the next frame.
        float? heldSupportZ = _lastSupportZ;
        _lastSupportZ = null;

        bool haveTerrain = _terrain.TrySampleMovementSurface(
            Position.X, Position.Y, out TerrainSurfaceSample terrainSurface, out bool inHole);
        float? sampledTerrainZ = haveTerrain ? terrainSurface.Height : null;

        bool terrainIsOverhead = sampledTerrainZ is float sampledSurface &&
                                 sampledSurface - Position.Z > UndergroundSlack;

        // A teleport/fly exit below ADT proves that the ADT is a shell, not a
        // floor. Once WMO precedence proves the same thing during continuous
        // movement, retain that fact across short gaps in collision support.
        if (_landingAfterDiscontinuousMove && terrainIsOverhead)
            _underTerrainShell = true;
        else if (_underTerrainShell && sampledTerrainZ is not null && !terrainIsOverhead)
            _underTerrainShell = false;

        bool collisionWalkway = haveTerrain &&
            CollisionWalkwayDisplacesSteepTerrain(terrainSurface, Position.X, Position.Y);
        bool ignoreTerrainShell = (_underTerrainShell && terrainIsOverhead) || collisionWalkway;

        bool steepContact = haveTerrain && !ignoreTerrainShell &&
                            terrainSurface.Normal.Z < _minGroundZ &&
                            Position.Z < terrainSurface.Height;
        if (steepContact)
        {
            ResolveSteepTerrainContact(terrainMoveStart);
            haveTerrain = _terrain.TrySampleMovementSurface(
                Position.X, Position.Y, out terrainSurface, out inHole);
            sampledTerrainZ = haveTerrain ? terrainSurface.Height : null;
            terrainIsOverhead = sampledTerrainZ is float movedSurface &&
                                movedSurface - Position.Z > UndergroundSlack;
            collisionWalkway = haveTerrain &&
                CollisionWalkwayDisplacesSteepTerrain(terrainSurface, Position.X, Position.Y);
            ignoreTerrainShell = (_underTerrainShell && terrainIsOverhead) || collisionWalkway;
        }

        bool terrainSteep = haveTerrain && !ignoreTerrainShell &&
                            terrainSurface.Normal.Z < _minGroundZ;
        float? groundZ = ignoreTerrainShell || terrainSteep ? null : sampledTerrainZ;

        // A server teleport and the F-key fly rig both supply an intentional Z.
        // If that Z is well below the outdoor height field, the terrain sample
        // is overhead (a mountain/tunnel roof), not a floor. A discontinuous
        // placement establishes that relationship immediately; continuous
        // tunnel entry establishes it below when WMO collision wins the
        // closer-surface test.
        bool terrainOverheadDuringLanding = _landingAfterDiscontinuousMove &&
                                             ignoreTerrainShell;
        bool deepCollisionLandingProbe = _landingAfterDiscontinuousMove &&
            (terrainOverheadDuringLanding || groundZ is null);

        InTerrainHole = inHole;
        TerrainGroundZ = sampledTerrainZ;
        TerrainGroundNormal = haveTerrain ? terrainSurface.Normal : Vector3.UnitZ;
        TerrainGroundSteep = terrainSteep;
        TerrainChunkImpassable = haveTerrain && terrainSurface.Impassable;
        CollisionGroundZ = null;
        if (ignoreTerrainShell)
        {
            GroundSource = "terrain-overhead";
        }
        else if (terrainSteep)
        {
            GroundSource = "terrain-steep";
        }
        else GroundSource = groundZ is null ? (inHole ? "hole" : "none") : "terrain";
        GroundProbeOffset = Vector2.Zero;
        GroundProbesLastFrame = 0;
        GroundAdhesion = false;
        GroundOwnerGuid = 0;

        if (HasGeometryCollision)
        {
            GroundTriangle = -1;

            float bestSurfaceZ = float.NegativeInfinity;
            int bestTriangle = -1;
            Vector2 bestOffset = Vector2.Zero;

            // Stay slightly inside the capsule edge. Sampling exactly on the
            // radius makes a touching wall eligible as floor due to float noise.
            float probeRadius = MathF.Max(0f, _opts.Radius * 0.85f);

            // Include this frame's downward travel in the probe origin. At
            // terminal velocity a frame can cover more than StepHeight; a
            // fixed origin would then start below a floor crossed this frame
            // and let the character tunnel straight through it.
            float probeLift = MathF.Max(_opts.StepHeight,
                downwardTravel + GroundContactEpsilon);

            // AND NEVER LOSE SIGHT OF THE FLOOR YOU WERE JUST STANDING ON.
            //
            // A fixed StepHeight lift makes any surface above the configured ceiling
            // feet invisible to this probe - INCLUDING the one holding you up a
            // moment ago. Sink below it by any means (a step-up into a solid, a
            // stair-lip miss, an adhesion frame that guessed low) and it can
            // never support you again: the origin is under it, the ray travels
            // away from it, and the character falls out of a floor that is
            // still there. That is the "no gap in x-ray but I drop through
            // every single time" bug, and it is why it was so repeatable.
            //
            // Raising the origin just past last frame's support closes it. This
            // cannot invent ground - it only lets the ray see a surface the
            // character verifiably stood on one frame earlier - and the contact
            // branch at the end then lifts the feet back onto it. Capped at
            // body height so it never becomes the head-height probe this
            // method's summary warns about, and it only ever raises the lift,
            // so the fast-fall reach above is preserved.
            if (Grounded && heldSupportZ is float heldZ && heldZ > Position.Z)
            {
                float reach = MathF.Min(_opts.Height,
                    heldZ - Position.Z + GroundContactEpsilon);
                probeLift = MathF.Max(probeLift, reach);
            }

            void ProbeCollision(Vector2 direction)
            {
                GroundProbesLastFrame++;
                var offset = direction * probeRadius;
                var origin = Position + new Vector3(offset.X, offset.Y, probeLift);

                // Reach well below the feet so a fast fall onto a bridge or a
                // WMO floor is not missed between frames. A discontinuous move
                // into an interior (terrain overhead or no terrain answer) gets
                // one stronger guarantee: search to the bottom of the resident
                // collision world. Five yards was too shallow for teleports and
                // for leaving fly mode high inside a cavern, so the terrain
                // above won before the lower floor was even considered.
                float probeDepth = probeLift + 5f;
                if (deepCollisionLandingProbe)
                {
                    if (Collision is { IsEmpty: false })
                    {
                        float collisionBottom = Collision.BoundsMin.Z + Collision.Offset.Z;
                        probeDepth = MathF.Max(probeDepth, origin.Z - collisionBottom + 0.01f);
                    }
                }
                var hit = RaycastGeometry(origin, Down, probeDepth);
                if (hit is null || hit.Value.Normal.Z <= _minGroundZ) return;

                float surfaceZ = origin.Z - hit.Value.Distance;
                if (surfaceZ <= bestSurfaceZ) return;

                bestSurfaceZ = surfaceZ;
                bestTriangle = hit.Value.Triangle;
                bestOffset = offset;
            }

            ProbeCollision(SupportProbeDirections[0]);

            // THE FOOTPRINT FAN MUST RUN WHENEVER THE CENTRE RAY IS AMBIGUOUS,
            // AND "A SURFACE A FEW TENTHS BELOW" IS THE AMBIGUOUS CASE.
            //
            // This gate used to skip the fan whenever the centre found anything
            // within GroundSnapDistance below the feet, to keep flat walking at
            // one BVH query. But that is exactly the reading a floor EDGE
            // produces: step a hair past the triangle you are standing on and
            // the centre ray lands on the next plank, beam or stair lip a few
            // tenths down. The fan - the one thing that would have found the
            // surface still under your heels - was switched off at precisely
            // the moment it was needed. Support fell to a single ray, and the
            // adhesion branch at the end then walked the character down through
            // the geometry a frame at a time until the real floor was out of
            // probe reach entirely and it dropped through.
            //
            // So skip the fan only for the unambiguous case: a surface already
            // AT the feet. Flat ground and resting contact still cost one ray -
            // the snap leaves Position.Z on the surface, so the delta is a
            // single frame of gravity - while every height transition pays for
            // nine and gets an answer that accounts for the whole footprint.
            //
            // Both tests are absolute. The one-sided form counted a surface far
            // ABOVE the feet as "near": the same defect already fixed on the
            // terrain line and left in place on the collision line.
            bool terrainUnderfoot = groundZ is float terrainZ &&
                                    MathF.Abs(Position.Z - terrainZ) <= GroundContactEpsilon;
            bool collisionUnderfoot = bestTriangle >= 0 &&
                                      MathF.Abs(Position.Z - bestSurfaceZ) <= GroundContactEpsilon;

            if (!terrainUnderfoot && !collisionUnderfoot)
            {
                for (int i = 1; i < SupportProbeDirections.Length; i++)
                    ProbeCollision(SupportProbeDirections[i]);
            }

            if (bestTriangle >= 0)
            {
                CollisionGroundZ = bestSurfaceZ;

                // Vanilla's Map::GetHeight, in the two clauses it actually is:
                // take the collision surface when it is above terrain, or when
                // the feet are genuinely under terrain and it is the closer of
                // the two. The second clause is what lets a tunnel floor beat
                // the mountain sitting on top of it; the slack gate is what
                // stops an ordinary uphill step from qualifying.
                bool underTerrain = groundZ is float overhead &&
                                    overhead - Position.Z > UndergroundSlack;
                bool closerThanTerrain = groundZ is float rival &&
                                         MathF.Abs(rival - Position.Z) >
                                         MathF.Abs(bestSurfaceZ - Position.Z);

                if (groundZ is null ||
                    bestSurfaceZ > groundZ.Value ||
                    (VanillaHeightPrecedence && underTerrain && closerThanTerrain))
                {
                    groundZ = bestSurfaceZ;
                    GroundSource = "collision";
                    GroundTriangle = bestTriangle;
                    GroundProbeOffset = bestOffset;
                }

                // Continuous tunnel entry establishes the same fact as an
                // interior teleport: this collision floor is below an outdoor
                // terrain shell.  Remember it before a jump or seam can make a
                // later footprint probe miss collision for a frame.
                if (GroundSource == "collision" &&
                    sampledTerrainZ is float terrainShell &&
                    terrainShell - Position.Z > UndergroundSlack)
                    _underTerrainShell = true;
            }
        }

        if (MovingGroundProbe is not null)
        {
            float bestSurfaceZ = float.NegativeInfinity;
            MovingGroundHit bestHit = default;
            Vector2 bestOffset = Vector2.Zero;
            float probeRadius = MathF.Max(0f, _opts.Radius * 0.85f);
            float probeLift = MathF.Max(_opts.StepHeight,
                downwardTravel + GroundContactEpsilon);
            float probeDepth = probeLift + 5f;

            void ProbeMoving(Vector2 direction)
            {
                Vector2 offset = direction * probeRadius;
                Vector3 origin = Position + new Vector3(offset.X, offset.Y, probeLift);
                MovingGroundHit? candidate = MovingGroundProbe(origin, probeDepth);
                if (candidate is not { } moving || moving.Normal.Z <= _minGroundZ ||
                    moving.Point.Z <= bestSurfaceZ)
                    return;
                bestSurfaceZ = moving.Point.Z;
                bestHit = moving;
                bestOffset = offset;
            }

            ProbeMoving(SupportProbeDirections[0]);
            // Same underfoot rule as the static lane, for the same reason: a
            // boat deck has edges too, and the one-sided test also suppressed
            // the fan when the centre reported a surface above the feet.
            if (bestHit.OwnerGuid == 0 ||
                MathF.Abs(Position.Z - bestSurfaceZ) > GroundContactEpsilon)
                for (int i = 1; i < SupportProbeDirections.Length; i++)
                    ProbeMoving(SupportProbeDirections[i]);

            if (bestHit.OwnerGuid != 0)
            {
                CollisionGroundZ = CollisionGroundZ is float staticZ
                    ? MathF.Max(staticZ, bestSurfaceZ) : bestSurfaceZ;
                bool underTerrain = groundZ is float overhead &&
                                    overhead - Position.Z > UndergroundSlack;
                bool closerThanTerrain = groundZ is float rival &&
                    MathF.Abs(rival - Position.Z) > MathF.Abs(bestSurfaceZ - Position.Z);
                if (groundZ is null || bestSurfaceZ > groundZ.Value ||
                    (VanillaHeightPrecedence && underTerrain && closerThanTerrain))
                {
                    groundZ = bestSurfaceZ;
                    GroundSource = "transport";
                    GroundTriangle = -1;
                    GroundProbeOffset = bestOffset;
                    GroundOwnerGuid = bestHit.OwnerGuid;
                }
            }
        }

        if (WaterWalking && ExternalWalkableSurfaceZ is float liquidZ &&
            liquidZ - Position.Z <= _opts.StepHeight &&
            (groundZ is null || liquidZ > groundZ.Value))
        {
            groundZ = liquidZ;
            GroundSource = "liquid-water-walk";
            GroundOwnerGuid = 0;
        }
        if (Hovering && groundZ is not null) groundZ += 1f;
        GroundZ = groundZ;

        if (groundZ is null &&
            (inHole || ignoreTerrainShell || TerrainAbsentByDesign || terrainSteep))
        {
            // None of these cases is missing data: a hole is authored geometry,
            // a global-WMO map has no terrain by design, and the discarded
            // sample is known to be above an explicitly placed interior point.
            // Keep falling so a deeper WMO floor can become reachable; do not
            // freeze or snap onto the roof.
            NoGroundBelow = false;
            _warnedNoGround = false;
            Grounded = false;
            return;
        }

        if (groundZ is null)
        {
            // Nothing knows what is below. Do NOT keep falling: a missing height
            // grid once presented as a physics bug, with the character dropping
            // 5,300 units over 23 seconds and no error anywhere. Freeze, say so
            // once, and let the HUD flag stay up.
            NoGroundBelow = true;
            Grounded = false;
            Velocity.Z = 0;

            if (!_warnedNoGround)
            {
                var (col, row) = TerrainRenderer.TileAt(Position.X, Position.Y);
                Console.WriteLine(
                    $"[move] NO GROUND at ({Position.X:F1}, {Position.Y:F1}, {Position.Z:F1}) " +
                    $"tile [{col},{row}] - off the loaded tiles, or that chunk has no MCVT. " +
                    "Vertical motion frozen rather than falling.");
                _warnedNoGround = true;
            }

            return;
        }

        NoGroundBelow = false;
        _warnedNoGround = false;

        // ── The `Velocity.Z <= 0f` guard is why jumping works ────────────────
        //
        // This landing test used to be distance-only. Ascending inside the
        // epsilon, it yanked Position.Z back down to groundZ and set Grounded -
        // which SWALLOWS A JUMP ENTIRELY, as a function of frame rate. That is
        // why it presented as "space works maybe 1 in 10 times" rather than as a
        // clean break.
        //
        // The first frame of a jump rises by (JumpVelocity - Gravity*dt) * dt.
        // With the shipped 7.9558 / 19.2911:
        //
        //      60 fps (dt 16.7 ms) -> 0.127  clears 0.05  works
        //     144 fps (dt  6.9 ms) -> 0.054  clears 0.05  barely
        //     165 fps (dt  6.1 ms) -> 0.047  FAILS
        //     300 fps (dt  3.3 ms) -> 0.026  FAILS
        //
        // Above roughly 160 fps the rise never clears the epsilon, so every
        // frame re-clamped Position.Z back to groundZ. Height could not
        // accumulate, gravity kept eating the velocity, and the character stayed
        // pinned to the floor - with Grounded still true, so holding space just
        // re-triggered a jump that was cancelled again. The presses that DID
        // work were the ones landing on a frame long enough to clear 0.05 by
        // itself: a stutter frame.
        //
        // So this got WORSE as frame times got better, and it is broken outright
        // with vsync off. That is also how to confirm it: vsync ON at 60 always
        // worked, vsync OFF was almost totally dead.
        //
        // Snapping to ground is for landing, and for staying on the floor. A
        // character moving UPWARD is doing neither and must be left alone.
        if (Velocity.Z <= 0f && Position.Z <= groundZ.Value + GroundContactEpsilon)
        {
            Position.Z = groundZ.Value;
            Velocity.Z = 0f;
            Grounded = true;
            _lastSupportZ = groundZ.Value;
            _landingAfterDiscontinuousMove = false;
        }
        else if (allowGroundAdhesion && Velocity.Z <= 0f &&
                 Position.Z - groundZ.Value <= MathF.Max(0f, _opts.GroundSnapDistance))
        {
            // Descending stairs and short gaps between narrow support triangles
            // should read as continuous ground. Physics still leaves support
            // immediately for a deliberate jump because allowGroundAdhesion is
            // false on that frame.
            Position.Z = groundZ.Value;
            Velocity.Z = 0f;
            Grounded = true;
            GroundAdhesion = true;
            _lastSupportZ = groundZ.Value;
            _landingAfterDiscontinuousMove = false;
        }
        else
        {
            Grounded = false;
        }
    }

    /// <summary>
    /// State in the exact form a movement packet wants, in Phase 2. No
    /// conversion: the client already works in WoW space, and Yaw already is the
    /// orientation value.
    /// </summary>
    public (float X, float Y, float Z, float Orientation, float FallTime) WowState
        => (Position.X, Position.Y, Position.Z, Yaw, FallTimeMs);

    private static float Normalize(float radians)
    {
        const float tau = MathF.PI * 2f;
        return ((radians % tau) + tau) % tau;
    }
}
