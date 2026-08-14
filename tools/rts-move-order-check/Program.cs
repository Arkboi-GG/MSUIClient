using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using MSUIClient.Net;
using MSUIClient.World.Units;

int checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}

void Near(float actual, float expected, float epsilon, string message) =>
    Check(MathF.Abs(actual - expected) <= epsilon,
        $"{message}: expected {expected}, got {actual}");

// A command-frame facing prediction may rotate, but may never fabricate translation.
var entities = new EntityStore();
var ordered = new WorldEntity
{
    Guid = 7,
    Type = ObjectTypeId.Player,
    Position = new Vector3(10f, 20f, 5f),
    Orientation = 0f,
};
entities.AddSynthetic(ordered);
Vector3 before = ordered.Position;
entities.PredictServerMoveFacing(ordered.Guid, new Vector3(10f, 30f, 99f));
Check(ordered.Position == before, "move prediction changed authoritative position");
Near(ordered.Orientation, MathF.PI / 2f, 0.0001f,
    "move prediction did not face the horizontal destination");

// A Player can move under client packets while possessed, then under an AI spline after an RTS
// order. The latter must reclaim facing so the body follows its path instead of retaining aim.
entities.ApplyRemotePlayerMove(ordered.Guid, MovementInfo.Create(
    ordered.Position, 0f, MovementFlags.Forward), nowMs: 1);
var serverMove = new MonsterMove
{
    Guid = ordered.Guid,
    Points = [ordered.Position, ordered.Position + new Vector3(0f, 10f, 0f)],
    DurationMs = 1_000,
};
entities.ApplyMonsterMove(serverMove, nowMs: 1);
entities.TickSplines(nowMs: 501);
Near(ordered.Orientation, MathF.PI / 2f, 0.0001f,
    "server player spline did not reclaim travel facing");
Near(ordered.Position.Y, 25f, 0.001f, "server spline did not remain authoritative");

// CreatureRenderer owns transient body-action dictionaries. Construct only that lightweight
// state (no GL/MPQ work), arm a cast + swing, and prove the movement boundary clears both and
// restarts locomotion phase at zero.
var renderer = (CreatureRenderer)RuntimeHelpers.GetUninitializedObject(typeof(CreatureRenderer));
foreach (string fieldName in new[] { "_animTime", "_combatActions", "_spellHolds" })
{
    FieldInfo field = typeof(CreatureRenderer).GetField(fieldName,
        BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException($"missing renderer state {fieldName}");
    field.SetValue(renderer, Activator.CreateInstance(field.FieldType));
}

renderer.TriggerCombatSwing(ordered.Guid, offHand: false);
renderer.BeginSpellVisual(ordered.Guid, animationId: 1);
renderer.InterruptActionForMovement(ordered.Guid);

IDictionary State(string name) => (IDictionary)(typeof(CreatureRenderer).GetField(name,
    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(renderer) ??
    throw new InvalidOperationException($"missing renderer state {name}"));

Check(State("_combatActions").Count == 0, "movement boundary retained combat one-shot");
Check(State("_spellHolds").Count == 0, "movement boundary retained spell hold");
Near((float)(State("_animTime")[ordered.Guid] ?? -1f), 0f, 0f,
    "movement boundary did not reset animation phase");

// Source-level routing law: only a replacement move gets the command-frame prediction. A queued
// future leg must keep the body's current facing until its authoritative spline begins.
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
string control = File.ReadAllText(Path.Combine(root, "MSUIClient", "Program.Control.cs"))
    .Replace("\r\n", "\n", StringComparison.Ordinal);
int queuedCase = control.IndexOf("if (queue)", StringComparison.Ordinal);
int plainCase = control.IndexOf("else\n            {", queuedCase, StringComparison.Ordinal);
int helper = control.IndexOf("private void BeginRtsMovePresentation", plainCase, StringComparison.Ordinal);
Check(queuedCase >= 0 && plainCase > queuedCase && helper > plainCase,
    "move-order routing blocks were not found");
Check(!control[queuedCase..plainCase].Contains("BeginRtsMovePresentation", StringComparison.Ordinal),
    "queued waypoint predicted a future leg before it began");
Check(control[plainCase..helper].Contains("BeginRtsMovePresentation(subjects, point)",
        StringComparison.Ordinal),
    "plain move did not apply its immediate presentation boundary");

Console.WriteLine($"RTS move-order checks passed ({checks}).");
