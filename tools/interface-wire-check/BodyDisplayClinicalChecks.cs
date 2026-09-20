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
        var productionAnimations = (int[])typeof(CharacterRenderer).GetField("BakedAnimations",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Check(productionAnimations.Contains(119) && productionAnimations.Contains(120),
            "local character load must bake stealth poses before its non-baking selector requests them");
        var animationData = DbcFile.Parse(mpq.ReadFile(@"DBFilesClient\AnimationData.dbc")!)!;
        foreach (var (id, name) in new[] { (119u, "StealthWalk"), (120u, "StealthStand") })
            Check(Enumerable.Range(0, animationData.RecordCount).Any(row =>
                animationData.GetUInt(row, 0) == id && animationData.GetString(row, 1) == name),
                $"mounted animation {id} must be {name}");
        // The mounted druid cat has neither stealth clip. Pin its Walk/Stand
        // fallback separately from the character model's authored stealth poses.
        foreach (var (path, stealthIdle, stealthWalk) in new[] {
            (@"Creature\DruidCat\DruidCat.m2", 0, 4),
            (@"Character\NightElf\Male\NightElfMale.m2", 120, 119) })
        {
            var model = M2Reader.Parse(mpq.ReadFile(path) ?? throw new InvalidDataException(path))!;
            var animator = M2Animator.Build(model, productionAnimations, true)!;
            Check(model.Sequences.Any(s => s.AnimationId == 119) == (stealthWalk == 119) &&
                model.Sequences.Any(s => s.AnimationId == 120) == (stealthIdle == 120),
                $"{path}: mounted stealth animation coverage changed; re-audit fallbacks");
            foreach (Type renderer in new[] { typeof(CreatureRenderer), typeof(PlayerRenderer) })
            {
                MethodInfo selector = renderer.GetMethod("SelectClip", BindingFlags.NonPublic | BindingFlags.Static)!;
                foreach (var (flags, speed, stealth, expected) in new (MovementFlags, float, bool, int)[] {
                    (MovementFlags.None, 0, true, stealthIdle), (MovementFlags.Forward, 7, true, stealthWalk),
                    (MovementFlags.StrafeLeft, 7, true, stealthWalk), (MovementFlags.Backward, 2.5f, true, 13),
                    (MovementFlags.Backward | MovementFlags.StrafeLeft, 2.5f, true, 13),
                    (MovementFlags.Swimming, 0, true, 41), (MovementFlags.None, 0, false, 0),
                    (MovementFlags.Forward, 7, false, 5) })
                {
                    var entity = new WorldEntity().WithRenderPose(Vector3.Zero, 0, speed, (uint)flags, false);
                    entity.Fields.SetU32(ObjectFields.UNIT_FIELD_BYTES_1, stealth ? 0x02000000u : 0u);
                    if (speed > 0) entity.Spline = new CreatureSpline([Vector3.Zero, new Vector3(speed, 0, 0)], 1000, false, 0);
                    object?[] args = renderer == typeof(CreatureRenderer)
                        ? [entity, animator, "stealth-regression", false, 0f]
                        : [entity, animator, "stealth-regression", 0f];
                    var clip = (M2Animator.Clip?)selector.Invoke(null, args);
                    int resolved = model.Sequences.Any(s => s.AnimationId == expected) ? expected : expected == 13 ? 4 : 0;
                    Check(clip?.AnimationId == resolved, $"{renderer.Name}/{path}/{flags}: expected{resolved}, got{clip?.AnimationId}");
                    if (stealth && (expected == stealthIdle || expected == stealthWalk))
                    {
                        float expectedRate = expected == 4 && clip!.MoveSpeed > 0.01f
                            ? Math.Clamp(speed / clip.MoveSpeed, 0.25f, 3f) : 1f;
                        Check(MathF.Abs((float)args[^1]! - expectedRate) < 0.001f,
                            "exact stealth poses retain 1x; Walk fallback must track ground speed");
                    }
                }
            }
            // Exercise the local selector without constructing a GL renderer.
            // Transformed cats take CreatureRenderer, covered above, not this rig.
            if (stealthWalk != 119) continue;
            var local = (CharacterRenderer)RuntimeHelpers.GetUninitializedObject(typeof(CharacterRenderer));
            typeof(CharacterRenderer).GetField("_animator", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(local, animator);
            MethodInfo choose = typeof(CharacterRenderer).GetMethod("ChooseClip", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var (forward, stealth, expected) in new[] {
                (0f, true, stealthIdle), (1f, true, stealthWalk), (0f, false, 0) })
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
