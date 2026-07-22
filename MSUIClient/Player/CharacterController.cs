using System.Numerics;
using MSUIClient.World;
using MSUIClient.World.Collision;

namespace MSUIClient.Player;

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

    private readonly TerrainRenderer _terrain;
    private readonly ClientConfig.MovementConfig _opts;

    /// <summary>cos(maxSlope): a surface normal's Z must exceed this to be standable.</summary>
    private readonly float _minGroundZ;

    private bool _warnedNoGround;

    /// <summary>vmap collision. Null until vmaps are configured - terrain still works.</summary>
    public CollisionWorld? Collision { get; set; }

    public Vector3 Position;
    public Vector3 Velocity;

    /// <summary>Radians CCW about +Z from +X. This IS the WoW orientation value.</summary>
    public float Yaw;

    public bool Grounded { get; private set; }
    public bool Flying { get; set; }
    public float FallTimeMs { get; private set; }

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
    public float? CollisionGroundZ { get; private set; }

    /// <summary>Which candidate won: "terrain", "collision", or "none".</summary>
    public string GroundSource { get; private set; } = "none";

    /// <summary>Index of the collision triangle currently acting as ground, or -1.</summary>
    public int GroundTriangle { get; private set; } = -1;

    /// <summary>True when neither terrain nor collision could say what is below.</summary>
    public bool NoGroundBelow { get; private set; }

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

    public void Teleport(float x, float y, float z)
    {
        Position = new Vector3(x, y, z);
        Velocity = Vector3.Zero;
        Grounded = false;
        FallTimeMs = 0;
        _warnedNoGround = false;
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

        if (Collision is null || Collision.IsEmpty) return;

        var origin = Position + new Vector3(0, 0, _opts.Height * 0.5f);
        var push = Vector3.Zero;

        const int rays = 8;
        for (int i = 0; i < rays; i++)
        {
            float angle = i * MathF.PI * 2f / rays;
            var dir = new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0);

            var hit = Collision.Raycast(origin, dir, _opts.Radius);
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

        // Facing and its right-hand side, in WoW space. Matches Camera.FlatForward
        // and Camera.FlatRight exactly - +Y is west, so right is (sin, -cos).
        var forward = new Vector3(MathF.Cos(Yaw), MathF.Sin(Yaw), 0f);
        var right = new Vector3(MathF.Sin(Yaw), -MathF.Cos(Yaw), 0f);

        if (Flying)
        {
            UpdateFlying(dt, input, forward, right);
            return;
        }

        float speed = input.Walking ? _opts.WalkSpeed : _opts.RunSpeed;

        var wish = forward * input.Forward + right * input.Strafe;
        var move = Vector3.Zero;
        if (wish.LengthSquared() > 1e-6f)
            move = Vector3.Normalize(wish) * speed * dt;

        if (input.Jump && Grounded)
        {
            Velocity.Z = _opts.JumpVelocity;
            Grounded = false;
        }

        Depenetrate();

        MoveHorizontal(ref move);

        Velocity.Z -= _opts.Gravity * dt;
        if (Velocity.Z < -_opts.TerminalVelocity) Velocity.Z = -_opts.TerminalVelocity;
        Position.Z += Velocity.Z * dt;

        ResolveGround();

        LastBlockAgeSeconds += dt;

        FallTimeMs = Grounded ? 0f : FallTimeMs + dt * 1000f;
    }

    /// <summary>
    /// Free-fly. Keeps the pre-collision camera behaviour available as a toggle,
    /// which is the fastest way to check whether a movement problem is the
    /// controller or the world.
    /// </summary>
    private void UpdateFlying(float dt, in MovementInput input, Vector3 forward, Vector3 right)
    {
        float speed = _opts.FlySpeed * (input.Boost ? _opts.FlyBoost : 1f);

        var wish = forward * input.Forward + right * input.Strafe + Vector3.UnitZ * input.Up;
        if (wish.LengthSquared() > 1e-6f)
            Position += Vector3.Normalize(wish) * speed * dt;

        Velocity = Vector3.Zero;
        Grounded = false;
        FallTimeMs = 0;
        GroundZ = _terrain.SampleHeight(Position.X, Position.Y);
        NoGroundBelow = false;
    }

    /// <summary>
    /// Horizontal sweep. On a wall hit the remaining motion is projected onto the
    /// wall plane so the character slides instead of sticking, then a step-up is
    /// attempted - which is what makes stairs and the abbey steps walkable
    /// without jumping. Two iterations handles inside corners.
    /// </summary>
    private void MoveHorizontal(ref Vector3 move)
    {
        if (move.LengthSquared() < 1e-8f) return;

        if (Collision is null || Collision.IsEmpty)
        {
            Position += move;
            return;
        }

        for (int iter = 0; iter < 2; iter++)
        {
            float dist = move.Length();
            if (dist < 1e-5f) return;

            var dir = move / dist;

            // Cast from mid-body so low rubble doesn't count as a wall.
            var origin = Position + new Vector3(0, 0, _opts.Height * 0.5f);

            var hit = Collision.Raycast(origin, dir, dist + _opts.Radius);

            if (hit is null || hit.Value.Distance > dist + _opts.Radius)
            {
                Position += move;
                return;
            }

            // A walkable slope is not a wall - let the ground resolver take it.
            if (hit.Value.Normal.Z > _minGroundZ)
            {
                Position += move;
                return;
            }

            if (TryStepUp(move)) return;

            // This is the surface that actually stops the character. Record it
            // before sliding, because after the slide the information is gone.
            LastBlockPoint = hit.Value.Point;
            LastBlockNormal = hit.Value.Normal;
            LastBlockTriangle = hit.Value.Triangle;
            LastBlockAgeSeconds = 0f;

            float advance = MathF.Max(0f, hit.Value.Distance - _opts.Radius);
            if (advance > 1e-5f) Position += dir * advance;

            // Slide: strip the component pushing into the wall.
            float into = Vector3.Dot(move, hit.Value.Normal);
            move -= hit.Value.Normal * into;
            move *= 0.98f;
        }
    }

    /// <summary>Probe for a ledge within stepHeight that the move could stand on.</summary>
    private bool TryStepUp(Vector3 move)
    {
        if (!Grounded || Collision is null) return false;

        var probe = Position + move;
        probe.Z += _opts.StepHeight + 0.1f;

        var down = Collision.Raycast(probe, Down, _opts.StepHeight + 0.4f);
        if (down is null || down.Value.Normal.Z <= _minGroundZ) return false;

        float stepTop = probe.Z - down.Value.Distance;
        float rise = stepTop - Position.Z;
        if (rise < -0.05f || rise > _opts.StepHeight) return false;

        Position.X += move.X;
        Position.Y += move.Y;
        Position.Z = stepTop;
        Velocity.Z = 0;
        Grounded = true;
        return true;
    }

    /// <summary>
    /// Ground resolution. The height grid is authoritative for terrain. A
    /// downward raycast covers anything standing above it; whichever surface is
    /// higher, and within reach, wins.
    ///
    /// THE PROBE STARTS AT StepHeight, NOT AT Height, AND THAT MATTERS.
    /// Casting from head height finds surfaces up to two yards ABOVE the feet,
    /// and the snap below then yanks the character onto them — so walking near
    /// a staircase teleports you up it before you reach it, and standing under
    /// any low overhang glues you to its underside. Starting the ray at
    /// StepHeight means the highest surface it can possibly report is exactly
    /// one step up, which is the whole rule: you may climb a step, you may not
    /// be levitated onto a landing.
    ///
    /// The browser build had this same structure and the same latent bug. It
    /// was ported faithfully, which was the right default and the wrong outcome
    /// here.
    /// </summary>
    private void ResolveGround()
    {
        float? groundZ = _terrain.SampleHeight(Position.X, Position.Y);

        TerrainGroundZ = groundZ;
        CollisionGroundZ = null;
        GroundSource = groundZ is null ? "none" : "terrain";

        if (Collision is { IsEmpty: false })
        {
            var origin = Position + new Vector3(0, 0, _opts.StepHeight);

            // Reach well below the feet so a fast fall onto a bridge or a WMO
            // floor is not missed between frames.
            var hit = Collision.Raycast(origin, Down, _opts.StepHeight + 5f);

            GroundTriangle = -1;

            if (hit is not null && hit.Value.Normal.Z > _minGroundZ)
            {
                float surfaceZ = origin.Z - hit.Value.Distance;
                CollisionGroundZ = surfaceZ;
                GroundTriangle = hit.Value.Triangle;

                if (groundZ is null || surfaceZ > groundZ.Value)
                {
                    groundZ = surfaceZ;
                    GroundSource = "collision";
                }
            }
        }

        GroundZ = groundZ;

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

        if (Position.Z <= groundZ.Value + 0.05f)
        {
            Position.Z = groundZ.Value;
            if (Velocity.Z < 0) Velocity.Z = 0;
            Grounded = true;
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
