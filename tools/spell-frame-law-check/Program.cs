using System.Numerics;
using MSUIClient.Engine.UI;
using MSUIClient.Net;
using MSUIClient.World.Spells;
using MSUIClient.World.Units;

const float Epsilon = 1e-5f;

static void Check(Vector3 actual, Vector3 expected, string name)
{
    if (Vector3.DistanceSquared(actual, expected) > Epsilon * Epsilon)
        throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
}

// Stationary root, moving emitter bone: two zero-velocity births retain distinct positions.
Vector3 stationaryRoot = new(10, 20, 30);
Vector3 firstBirthWorld = stationaryRoot + new Vector3(0, 0, 2);
Vector3 secondBirthWorld = stationaryRoot + new Vector3(3, 0, -4);
Vector3 firstStored = SpellParticleFrameLaw.StoreAtBirth(firstBirthWorld, stationaryRoot,
    Quaternion.Identity);
Vector3 secondStored = SpellParticleFrameLaw.StoreAtBirth(secondBirthWorld, stationaryRoot,
    Quaternion.Identity);
Check(SpellParticleFrameLaw.DrawWorld(firstStored, stationaryRoot, Quaternion.Identity),
    firstBirthWorld, "moving-bone old birth");
Check(SpellParticleFrameLaw.DrawWorld(secondStored, stationaryRoot, Quaternion.Identity),
    secondBirthWorld, "moving-bone new birth");
if (Vector3.Distance(firstStored, secondStored) < 4.99f)
    throw new InvalidOperationException("moving-bone cloud history collapsed");

// Moving root, static emitter offset: the root carries the old particle by the root delta.
Vector3 rootAtBirth = new(1, 2, 3);
Vector3 rootAtDraw = new(11, -3, 8);
Vector3 emitterOffset = new(2, 0, 1);
Vector3 rootStored = SpellParticleFrameLaw.StoreAtBirth(rootAtBirth + emitterOffset,
    rootAtBirth, Quaternion.Identity);
Check(SpellParticleFrameLaw.DrawWorld(rootStored, rootAtDraw, Quaternion.Identity),
    rootAtDraw + emitterOffset, "moving-root cloud carry");

// Host attachment rotation is live, and remains distinct from emitter-bone animation.
Quaternion quarterTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
Vector3 attachedStored = SpellParticleFrameLaw.StoreAtBirth(rootAtBirth + Vector3.UnitX,
    rootAtBirth, Quaternion.Identity);
Check(SpellParticleFrameLaw.DrawWorld(attachedStored, rootAtBirth, quarterTurn),
    rootAtBirth + Vector3.UnitY, "host attachment rotation");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}

Require(!GameplayInputLaw.BlocksMovement(wantsKeyboard: true, wantsTextInput: false,
        settingsModalOpen: false, bindingCaptureActive: false),
    "ordinary focused UI button reset locomotion");
Require(GameplayInputLaw.BlocksMovement(wantsKeyboard: true, wantsTextInput: true,
        settingsModalOpen: false, bindingCaptureActive: false) &&
        GameplayInputLaw.BlocksMovement(wantsKeyboard: false, wantsTextInput: false,
            settingsModalOpen: true, bindingCaptureActive: false) &&
        GameplayInputLaw.BlocksMovement(wantsKeyboard: false, wantsTextInput: false,
            settingsModalOpen: false, bindingCaptureActive: true),
    "text/modal/keybinding capture leaked gameplay movement");

const float QuarterTurn = MathF.PI / 2f;
Require(CharacterPoseLaw.TorsoCounterYaw(false, false, true, moving: false,
        forcedDiagnostic: false, 0.66f, QuarterTurn) == 0f,
    "stationary body chase leaked into the moving-strafe torso twist");
Require(MathF.Abs(CharacterPoseLaw.TorsoCounterYaw(false, false, true, moving: true,
        forcedDiagnostic: false, 0.66f, QuarterTurn) - (-0.34f * QuarterTurn)) < 0.0001f,
    "moving split-strafe lost its torso counter-twist");
Require(CharacterPoseLaw.TorsoCounterYaw(false, false, true, moving: false,
        forcedDiagnostic: true, 0.66f, QuarterTurn) != 0f &&
        CharacterPoseLaw.TorsoCounterYaw(true, false, true, moving: true,
            forcedDiagnostic: false, 0.66f, QuarterTurn) == 0f,
    "force-angle diagnostic or bind-pose isolation regressed");

float releasedStep = CharacterPoseLaw.StandingBodyStep(
    QuarterTurn, steering: false, QuarterTurn, 1f / 60f, MathF.PI, 0.8f);
Require(MathF.Abs(releasedStep - MathF.PI * 0.8f / 60f) < 0.0001f,
    "released stationary turn snapped instead of using the chase-rate cap");
Require(MathF.Abs(CharacterPoseLaw.StandingBodyStep(
        QuarterTurn + 0.1f, steering: true, QuarterTurn, 1f / 60f, MathF.PI, 0.8f) - 0.1f) < 0.0001f,
    "held stationary turn no longer enforced its lag ceiling immediately");

static float QuadArea(CooldownVisualLaw.Quad q)
{
    static float Triangle(Vector2 a, Vector2 b, Vector2 c) =>
        MathF.Abs((b - a).X * (c - a).Y - (b - a).Y * (c - a).X) * 0.5f;
    return Triangle(q.A, q.B, q.C) + Triangle(q.A, q.C, q.D);
}

Vector2 wipeMin = new(10f, 20f), wipeMax = new(46f, 56f);
float previousWipeArea = float.PositiveInfinity;
foreach (float fraction in Enumerable.Range(0, 101).Select(i => i / 100f))
{
    IReadOnlyList<CooldownVisualLaw.Quad> quads =
        CooldownVisualLaw.BuildWipe(wipeMin, wipeMax, fraction);
    Require(quads.Count <= 4, "cooldown wipe exceeded four authored quadrants");
    foreach (Vector2 p in quads.SelectMany(q => new[] { q.A, q.B, q.C, q.D }))
        Require(p.X >= wipeMin.X - 0.001f && p.X <= wipeMax.X + 0.001f &&
                p.Y >= wipeMin.Y - 0.001f && p.Y <= wipeMax.Y + 0.001f,
            $"cooldown wipe escaped icon at fraction {fraction}: {p}");
    float area = quads.Sum(QuadArea);
    Require(area <= previousWipeArea + 0.1f,
        $"cooldown wipe reversed at fraction {fraction}");
    previousWipeArea = area;
}
foreach ((float fraction, float covered) in new[]
         { (0f, 1f), (0.25f, 0.75f), (0.5f, 0.5f), (0.75f, 0.25f), (1f, 0f) })
    Require(MathF.Abs(CooldownVisualLaw.BuildWipe(wipeMin, wipeMax, fraction)
                          .Sum(QuadArea) - 36f * 36f * covered) < 0.1f,
        $"cooldown quadrant coverage drift at fraction {fraction}");
Require(MathF.Abs(CooldownVisualLaw.FlashScale(0.333f) - 1.853f) < 0.001f &&
        MathF.Abs(CooldownVisualLaw.FlashAlpha(0.4f) - 1f) < 0.001f &&
        CooldownVisualLaw.FlashAlpha(1f) == 0f,
    "authored cooldown flash curve drift");

var phases = new PlayerActions();
phases.StartCooldown(133, 0, 1_000, 10.0);
Require(phases.TryCooldownDisplay(133, 10.25, 0, out CooldownDisplay sweep) &&
        MathF.Abs(sweep.SweepFraction!.Value - 0.25f) < 0.001f &&
        sweep.FlashProgress is null,
    "running cooldown did not expose sweep");
Require(!phases.IsOnCooldown(133, 11.4) &&
        phases.TryCooldownDisplay(133, 11.4, 0, out CooldownDisplay flash) &&
        flash.SweepFraction is null &&
        MathF.Abs(flash.FlashProgress!.Value - 0.4f) < 0.001f,
    "readiness and finish flash did not remain independent");
Require(!phases.TryCooldownDisplay(133, 12.01, 0, out _),
    "cooldown flash survived past one second");

Console.WriteLine("spell-frame-law-check: PASS (spell frames + cooldown square/flash)");
