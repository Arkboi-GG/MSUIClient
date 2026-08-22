using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;

internal static class StanceBarClinicalChecks
{
    public static void Run()
    {
        SpellInfo ghostWolf = Spell(2645, attributesEx2: 0x2,
            auraIds: [36u], miscValues: [16]);
        SpellInfo devotionAura = Spell(465, attributesEx2: 0x10,
            activeIconId: 12, activeIconPath: "active-aura");
        SpellInfo battleStance = Spell(2457, auraIds: [36u], miscValues: [17]);
        Check(!StanceBarUiLaw.Admitted(ghostWolf) &&
              StanceBarUiLaw.Admitted(devotionAura) &&
              StanceBarUiLaw.FormId(devotionAura) == 0 &&
              StanceBarUiLaw.Admitted(battleStance) &&
              StanceBarUiLaw.FormId(battleStance) == 17,
            "stance admission/form extraction drift");

        SpellInfo first = Spell(30, attributesEx2: 0x10, order: 0);
        SpellInfo second = Spell(20, attributesEx2: 0x10, order: 1);
        SpellInfo negative = Spell(10, attributesEx2: 0x10, order: -1);
        uint[] ordered = StanceBarUiLaw.Forms([negative, second, first])
            .Select(spell => spell.Id).ToArray();
        Check(ordered.SequenceEqual(new[] { 30u, 20u, 10u }) &&
              StanceBarUiLaw.Active(battleStance, 17, liveOwnAura: false) &&
              !StanceBarUiLaw.Active(battleStance, 1, liveOwnAura: true) &&
              StanceBarUiLaw.Active(devotionAura, 0, liveOwnAura: true),
            "stance signed order/active-state drift");

        Check(StanceBarUiLaw.Icon(devotionAura, active: true) == "active-aura" &&
              StanceBarUiLaw.Icon(devotionAura, active: false) == "icon" &&
              !StanceBarUiLaw.CancelActive(17, active: true, formCancelable: false) &&
              StanceBarUiLaw.CancelActive(17, active: true, formCancelable: true) &&
              StanceBarUiLaw.CancelActive(0, active: true, formCancelable: false) &&
              !new ShapeshiftFormInfo(17, 1, "Battle Stance", 0x2).Cancelable,
            "stance active-icon/cancel law drift");

        var raised = new UiParentManagedState(BottomLeftShown: true,
            BottomRightShown: true, RightLeftShown: false, RightRightShown: false,
            PetOrStanceShown: true, ReputationShown: false, MaxLevelShown: false);
        Check(StanceBarUiLaw.ButtonX(0) == 11 &&
              StanceBarUiLaw.ButtonX(9) == 344 &&
              StanceBarUiLaw.ButtonTop == -1 &&
              StanceBarUiLaw.FrameWidth(10) == 374 &&
              StanceBarUiLaw.RingSize(raised: true) == 50 &&
              StanceBarUiLaw.RingSize(raised: false) == 64 &&
              StanceBarUiLaw.FrameOrigin(new Vector2(100, 700), 1, raised) ==
                  new Vector2(130, 623),
            "stance button/managed-position geometry drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.StanceBar.cs"));
        string actionBars = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.ActionBars.cs"));
        string spellCatalog = SourceText.Read(Path.Combine(root, "MSUIClient", "Formats",
            "SpellCatalog.cs"));
        Check(runtime.Contains("StanceBarUiLaw.FrameOrigin", StringComparison.Ordinal) &&
              runtime.Contains("PetOrStanceActionBarVisible", StringComparison.Ordinal) &&
              runtime.Contains("TryCast(spell.Id)", StringComparison.Ordinal) &&
              runtime.Contains("_net.CancelAura(spell.Id)", StringComparison.Ordinal) &&
              actionBars.Contains("ShapeshiftFormCatalog.Load", StringComparison.Ordinal) &&
              spellCatalog.Contains("spells.GetInt(row, 166)", StringComparison.Ordinal),
            "stance production/data wiring drift");
    }

    private static SpellInfo Spell(uint id, uint attributesEx2 = 0,
        uint[]? auraIds = null, int[]? miscValues = null, int order = 0,
        uint activeIconId = 0, string activeIconPath = "") => new(
        Id: id, Name: $"Spell {id}", Rank: "", IconPath: "icon",
        Attributes: 0, AttributesEx2: attributesEx2, AttributesEx3: 0,
        InterruptFlags: 0, ChannelInterruptFlags: 0, Targets: 0, ImplicitTarget: 0,
        RecoveryMs: 0, CategoryRecoveryMs: 0, PowerType: 0, ManaCost: 0,
        ManaCostPercent: 0, StartRecoveryCategory: 0, StartRecoveryMs: 0,
        VisualId: 0, Speed: 0, Description: "", RangeIndex: 0,
        AuraIds: auraIds, EffectMiscValues: miscValues,
        ActiveIconId: activeIconId, ActiveIconPath: activeIconPath,
        StanceBarOrder: order);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
