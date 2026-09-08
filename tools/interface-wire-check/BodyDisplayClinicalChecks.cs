using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Units;

internal static class BodyDisplayClinicalChecks
{
    public static void Run()
    {
        var fields = new ObjectFields();
        fields.SetU32(ObjectFields.UNIT_DISPLAYID, 51);
        Check(!fields.HasDisplayTransform, "missing native display must not transform the body");
        fields.SetU32(ObjectFields.UNIT_NATIVEDISPLAYID, 51);
        Check(!fields.HasDisplayTransform, "native body must retain character customization");
        fields.SetU32(ObjectFields.UNIT_DISPLAYID, 4613);
        Check(fields.HasDisplayTransform, "Ghost Wolf display replacement was ignored");
        var player = new WorldEntity { Guid = 5, Type = ObjectTypeId.Player,
            Fields = fields, Position = new Vector3(1, 2, 3) };
        var wolf = new CreatureModelInfo(@"Creature\Wolf\Wolf.m2", 1, 1,
            [], false, 0, 0, 0, 0, 0, 0, 0, 0, [], "");
        Check(CreatureRenderer.TryBuildPlayerModelInfo(player, wolf,
            _ => throw new InvalidOperationException("transforms must not request player gear"),
            out var rendered) && !rendered.HasExtended && !rendered.IsPlayerAppearance &&
            rendered.ModelPath == wolf.ModelPath,
            "transformed player must use the replacement model appearance");
        var view = player.WithRenderPose(new Vector3(9, 8, 7), 1.5f, 7, 1, false);
        Check(view.Position == new Vector3(9, 8, 7) && view.IsMoving &&
            player.Position == new Vector3(1, 2, 3) && !player.IsMoving &&
            ReferenceEquals(view.Fields, player.Fields) && ReferenceEquals(view.AuraVisual, player.AuraVisual),
            "predicted render pose must preserve the network entity and actor-owned descriptor/aura");
        fields.SetU32(ObjectFields.UNIT_DISPLAYID, 51);
        Check(!fields.HasDisplayTransform, "cancelled form must restore the character renderer");
        CheckSwimmingModel();
        CheckStealthModels();
    }

    private static void CheckSwimmingModel()
    {
        string root = Directory.GetCurrentDirectory();
        while (!Directory.Exists(Path.Combine(root, "MSUIClient")))
            root = Directory.GetParent(root)?.FullName ?? throw new InvalidDataException("repository not found");
        using var mpq = new MpqMount(Path.Combine(root, "GameData", "Data"));
        var model = M2Reader.Parse(mpq.ReadFile(@"Creature\SeaLion\SeaLion.m2") ??
            throw new InvalidDataException("Aquatic Form model unavailable"))!;
        var animator = M2Animator.Build(model, [0, 4, 5, 41, 42, 43, 44, 45], true)!;
        MethodInfo selector = typeof(CreatureRenderer).GetMethod("SelectClip",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var (direction, expected) in new (MovementFlags, int)[] {
            (MovementFlags.None, 41), (MovementFlags.Forward, 42),
            (MovementFlags.Backward, 45), (MovementFlags.StrafeLeft, 43),
            (MovementFlags.StrafeRight, 44),
            (MovementFlags.Backward | MovementFlags.StrafeLeft, 43),
            (MovementFlags.Forward | MovementFlags.TurnLeft, 41) })
        {
            var entity = new WorldEntity().WithRenderPose(Vector3.Zero, 0, 7.083333f,
                (uint)(MovementFlags.Swimming | direction), false);
            object?[] args = [entity, animator, "swim-regression", false, 0f];
            var clip = (M2Animator.Clip?)selector.Invoke(null, args);
            int resolved = model.Sequences.Any(s => s.AnimationId == expected) ? expected
                : expected is 43 or 44 && model.Sequences.Any(s => s.AnimationId == 42) ? 42 : 41;
            Check(clip?.AnimationId == resolved,
                $"Aquatic Form {direction}: expected swim {resolved}, got {clip?.AnimationId}");
            if (resolved == 41) Check((float)args[4]! == 1f, "swim idle must retain authored rate");
        }
        object?[] dryArgs = [new WorldEntity(), animator, "dry-regression", false, 0f];
        Check(((M2Animator.Clip?)selector.Invoke(null, dryArgs))?.AnimationId == 0,
            "dry seal must leave swimming animation");
    }

    private static void CheckStealthModels()
    {
        using var mpq = new MpqMount(Path.Combine(Directory.GetCurrentDirectory(), "GameData", "Data"));
        foreach (string path in new[] { @"Creature\DruidCat\DruidCat.m2",
            @"Character\NightElf\Male\NightElfMale.m2" })
        {
            var model = M2Reader.Parse(mpq.ReadFile(path) ?? throw new InvalidDataException(path))!;
            var animator = M2Animator.Build(model, [0, 4, 5, 13, 41, 119, 120], true)!;
            Check(model.Sequences.Any(s => s.AnimationId == 119) &&
                model.Sequences.Any(s => s.AnimationId == 120), "fixture must author both stealth poses");
            foreach (Type renderer in new[] { typeof(CreatureRenderer), typeof(PlayerRenderer) })
            {
                MethodInfo selector = renderer.GetMethod("SelectClip", BindingFlags.NonPublic | BindingFlags.Static)!;
                foreach (var (flags, speed, stealth, expected) in new (MovementFlags, float, bool, int)[] {
                    (MovementFlags.None, 0, true, 120), (MovementFlags.Forward, 7, true, 119),
                    (MovementFlags.StrafeLeft, 7, true, 119), (MovementFlags.Backward, 2.5f, true, 13),
                    (MovementFlags.Swimming, 0, true, 41), (MovementFlags.None, 0, false, 0) })
                {
                    var entity = new WorldEntity { MoveFlags = (uint)flags };
                    entity.Fields.SetU32(ObjectFields.UNIT_FIELD_BYTES_1, stealth ? 0x02000000u : 0u);
                    if (speed > 0) entity.Spline = new CreatureSpline([Vector3.Zero, new Vector3(speed, 0, 0)], 1000, false, 0);
                    object?[] args = renderer == typeof(CreatureRenderer)
                        ? [entity, animator, "stealth-regression", false, 0f]
                        : [entity, animator, "stealth-regression", 0f];
                    var clip = (M2Animator.Clip?)selector.Invoke(null, args);
                    int resolved = model.Sequences.Any(s => s.AnimationId == expected) ? expected : expected == 13 ? 4 : 0;
                    Check(clip?.AnimationId == resolved, $"{renderer.Name}/{path}/{flags}: expected{resolved}, got{clip?.AnimationId}");
                    if (expected is 119 or 120) Check((float)args[^1]! == 1f, "stealth pose must use authored rate");
                }
            }
            // Exercise the local selector without constructing a GL renderer.
            var local = (CharacterRenderer)RuntimeHelpers.GetUninitializedObject(typeof(CharacterRenderer));
            typeof(CharacterRenderer).GetField("_animator", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(local, animator);
            MethodInfo choose = typeof(CharacterRenderer).GetMethod("ChooseClip", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var (forward, stealth, expected) in new[] { (0f, true, 120), (1f, true, 119), (0f, false, 0) })
            {
                object?[] args = [new CharacterRenderer.UnitState { Grounded = true, HasIntent = true,
                    Forward = forward, Stealthed = stealth }, 0f];
                Check(((M2Animator.Clip?)choose.Invoke(local, args))?.AnimationId == expected,
                    $"local {path} stealth={stealth} forward={forward} lost authored pose{expected}");
                Check((float)args[1]! == 1f, "local stealth pose rate drift");
            }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
