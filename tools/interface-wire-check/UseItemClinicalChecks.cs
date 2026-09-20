using MSUIClient.Formats;
using MSUIClient.Net;
using System.Numerics;

internal static class UseItemClinicalChecks
{
    public static void Run()
    {
        const ulong corpseGuid = 0xF130001C9600B6E2;
        Check(WorldSession.BuildUseItemBody(255, 23, 0).SequenceEqual(
            Convert.FromHexString("FF17000000")), "implicit-self item use body changed");
        Check(WorldSession.BuildUseItemBody(255, 23, 0, corpseGuid).SequenceEqual(
            Convert.FromHexString("FF17000200DBE2B6961C30F1")),
            "unit item use must carry mask 0x0002 and the packed selected GUID");
        Check(WorldSession.BuildUseItemBody(255, 26, 0, destination: new Vector3(1, -2, 3)).SequenceEqual(
            Convert.FromHexString("FF1A0040000000803F000000C000004040")),
            "ground item use must carry destination mask and three floats without a transport GUID");
        Check(WorldSession.BuildUseItemBody(255, 26, 0, corpseGuid, new Vector3(1, -2, 3)).SequenceEqual(
            Convert.FromHexString("FF1A004200DBE2B6961C30F10000803F000000C000004040")),
            "combined item target blocks must retain unit-before-destination ordering");
        SpellInfo charm = default(SpellInfo) with { Id = 10617, ImplicitTarget = 25 };
        var corpse = new CastTargetCandidate(corpseGuid, false, false, false, true);
        Check(CastTargetLaw.Resolve(charm, corpse, null).Guid == corpseGuid,
            "generic unit-target quest items must retain a selected corpse");
        SpellInfo friendly = charm with { ImplicitTarget = 21 };
        var actor = new CastTargetCandidate(0x221, true, true, false, false);
        Check(CastTargetLaw.Resolve(friendly, corpse, actor).Guid == actor.Guid,
            "friendly consumables must fall back to the acting body, not a hostile selection");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
