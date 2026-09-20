using System.Reflection;
using MSUIClient;
using MSUIClient.World.Units;

internal static class CharacterModelResetClinicalChecks
{
    public static void Run()
    {
        // No assets or GL handles are allocated: exercise production model teardown.
        var renderer = new CharacterRenderer(null!, new ClientConfig());
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(CharacterRenderer);
        var clips = type.GetFields(flags).Where(f => f.FieldType == typeof(M2Animator.Clip)).ToArray();
        var oldRigClip = new M2Animator.Clip { Bones = new M2Animator.BoneChannels[1] };
        foreach (var field in clips) field.SetValue(renderer, oldRigClip);
        foreach (string name in new[] { "_seatedLoopAnimId", "_seatedUpAnimId" }) type.GetField(name, flags)!.SetValue(renderer, 96);
        type.GetMethod("ResetModelState", flags)!.Invoke(renderer, null);
        var retained = clips.Where(f => f.GetValue(renderer) is not null).Select(f => f.Name).ToArray();
        if (retained.Length != 0) throw new InvalidDataException("Old rig clips survived model reset: " + string.Join(", ", retained));
        foreach (string name in new[] { "_seatedLoopAnimId", "_seatedUpAnimId" })
            if ((int)type.GetField(name, flags)!.GetValue(renderer)! != 0) throw new InvalidDataException("Old seated state survived: " + name);
        if (renderer.SheathCeremonyActive) throw new InvalidDataException("Old sheath ceremony survived reset");
        Console.WriteLine($"interface-wire-check: CharacterModelReset PASS ({clips.Length} clip slots cleared by actual teardown)");
    }
}
