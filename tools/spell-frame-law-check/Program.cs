using System.Numerics;
using MSUIClient.World.Spells;

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

Console.WriteLine("spell-frame-law-check: PASS (moving bone, moving root, attachment rotation)");
